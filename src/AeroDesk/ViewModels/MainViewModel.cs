using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using AeroDesk.Core.Operations;
using AeroDesk.Core.Retailing;
using AeroDesk.Core.Settings;
using AeroDesk.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AeroDeskWorkspace _workspace;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty]
    private string _statusText = "Ready — connect to a DocumentForge node or work offline.";

    public string VersionText { get; } =
        $"AeroDesk {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev"}";

    public ObservableCollection<TreeNodeViewModel> Connections { get; } = [];
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public MainViewModel(AeroDeskWorkspace workspace, IDialogService dialogs)
    {
        _workspace = workspace;
        _dialogs = dialogs;

        var welcome = new WelcomeDocumentViewModel();
        Documents.Add(welcome);
        ActiveDocument = welcome;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var request = _dialogs.ShowConnectDialog();
        if (request is null) return;

        if (request.Offline)
        {
            await AttachAsync(new InMemoryRetailingService(), new InMemoryOperationsService(), "Offline demo (in-memory)");
            return;
        }

        if (request.Save)
            _workspace.UpsertConnection(request.Descriptor, request.ApiKey);

        // One agent, one login: sign in to Keycloak ONCE; retailing and departure
        // control share the session (the token refreshes for the whole shift).
        var auth = new Core.Connections.KeycloakAuthClient(
            request.Descriptor.KeycloakAuthority,
            request.Descriptor.KeycloakRealm,
            request.Descriptor.KeycloakClientId);
        try
        {
            await auth.SignInAsync(request.Descriptor.Email, request.ApiKey ?? "");
        }
        catch (Exception ex)
        {
            auth.Dispose();
            StatusText = "Sign-in failed.";
            _dialogs.ShowError("Sign in", ex.Message);
            return;
        }

        await AttachAsync(
            new AeroBusRetailingService(request.Descriptor, auth),
            new AeroBusOperationsService(request.Descriptor, auth),
            request.Descriptor.Name,
            auth);
    }

    [RelayCommand]
    private Task WorkOfflineAsync() =>
        AttachAsync(new InMemoryRetailingService(), new InMemoryOperationsService(), "Offline demo (in-memory)");

    /// <summary>`--offline` startup: attach the in-memory airline and open Departure Control.</summary>
    public async Task StartOfflineDemoAsync()
    {
        await WorkOfflineAsync();
        if (Connections.OfType<ConnectionNodeViewModel>().LastOrDefault() is { } node)
            OpenDepartureControl(node);
    }

    /// <summary>Attach a backend's available surfaces. Each is connected independently
    /// so a connection can still offer one when the other is unavailable. When both
    /// fail, the shared Keycloak session (if any) is disposed too.</summary>
    private async Task AttachAsync(
        IRetailingService? retailing, IOperationsService? operations, string displayName,
        Core.Connections.KeycloakAuthClient? auth = null)
    {
        StatusText = $"Connecting to {displayName}…";
        var problems = new List<string>();

        if (retailing is not null)
        {
            try { await retailing.ConnectAsync(); }
            catch (Exception ex) { await retailing.DisposeAsync(); retailing = null; problems.Add($"retailing ({ex.Message})"); }
        }
        if (operations is not null)
        {
            try { await operations.ConnectAsync(); }
            catch (Exception ex) { await operations.DisposeAsync(); operations = null; problems.Add($"departure control ({ex.Message})"); }
        }

        if (retailing is null && operations is null)
        {
            auth?.Dispose();
            StatusText = "Connection failed.";
            _dialogs.ShowError("Connect", problems.Count > 0 ? string.Join("\n", problems) : "Nothing to connect.");
            return;
        }

        Connections.Add(new ConnectionNodeViewModel(this, retailing, operations, displayName, auth));
        var caps = new List<string>();
        if (retailing is not null) caps.Add("retailing");
        if (operations is not null) caps.Add("departure control");
        StatusText = $"Connected to {displayName} — {string.Join(" + ", caps)}"
                     + (problems.Count > 0 ? $"; unavailable: {string.Join(", ", problems)}" : "");
    }

    public async Task DisconnectAsync(ConnectionNodeViewModel node)
    {
        Connections.Remove(node);
        if (node.Retailing is not null) await node.Retailing.DisposeAsync();
        if (node.Operations is not null) await node.Operations.DisposeAsync();
        node.Auth?.Dispose();
        StatusText = $"Disconnected from {node.Name}.";
    }

    public void OpenSale(ConnectionNodeViewModel node)
    {
        if (node.Retailing is not { } service) return;
        var sale = new Sale.SaleDocumentViewModel(service, _dialogs);
        Documents.Add(sale);
        ActiveDocument = sale;
    }

    public void OpenOrders(ConnectionNodeViewModel node)
    {
        if (node.Retailing is not { } service) return;
        // Reuse an existing Orders tab for this connection if one is open.
        var existing = Documents.OfType<Orders.OrdersDocumentViewModel>()
            .FirstOrDefault(d => ReferenceEquals(d.Service, service));
        if (existing is null)
        {
            existing = new Orders.OrdersDocumentViewModel(service, _dialogs,
                envelope => OpenOrderDetail(service, envelope));
            Documents.Add(existing);
        }
        ActiveDocument = existing;
    }

    public void OpenDepartureControl(ConnectionNodeViewModel node)
    {
        if (node.Operations is not { } ops) return;
        // One departures board per connection.
        var existing = Documents.OfType<Operations.DeparturesDocumentViewModel>()
            .FirstOrDefault(d => ReferenceEquals(d.Operations, ops));
        if (existing is null)
        {
            existing = new Operations.DeparturesDocumentViewModel(ops, flight => OpenFlight(ops, flight));
            Documents.Add(existing);
        }
        ActiveDocument = existing;
    }

    public void OpenFlight(IOperationsService ops, DepartureFlight flight)
    {
        // One tab per flight — focus it if already open.
        var existing = Documents.OfType<Operations.FlightDocumentViewModel>()
            .FirstOrDefault(d => d.FlightId == flight.Id);
        if (existing is null)
        {
            existing = new Operations.FlightDocumentViewModel(ops, _dialogs, flight);
            Documents.Add(existing);
        }
        ActiveDocument = existing;
    }

    public void OpenOrderDetail(Core.Retailing.IRetailingService service, Core.Retailing.OrderEnvelope envelope)
    {
        // One detail tab per order — focus it if already open.
        var existing = Documents.OfType<Orders.OrderDetailDocumentViewModel>()
            .FirstOrDefault(d => d.Order?.OrderId == envelope.Order.OrderId);
        if (existing is null)
        {
            existing = new Orders.OrderDetailDocumentViewModel(service, _dialogs, envelope);
            Documents.Add(existing);
        }
        ActiveDocument = existing;
    }

    public async Task SeedDemoDataAsync(ConnectionNodeViewModel node)
    {
        if (node.Retailing is not { } service) return;
        StatusText = "Seeding demo inventory…";
        try
        {
            await service.SeedInventoryAsync();
            StatusText = $"Demo inventory ready on {node.Name}.";
        }
        catch (Exception ex)
        {
            StatusText = "Seeding failed.";
            _dialogs.ShowError("Seed demo data", ex.Message);
        }
    }

    [RelayCommand]
    private void ExportSettings()
    {
        var path = _dialogs.PickSaveFile("AeroDesk settings bundle (*.json)|*.json", "aerodesk-settings.json");
        if (path is null) return;
        if (!_dialogs.Confirm("Export Settings",
            "The bundle contains connection secrets in PLAINTEXT so it can move between machines. Store it safely. Continue?"))
            return;
        _workspace.ExportBundle(path);
        StatusText = $"Settings exported to {path}.";
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var path = _dialogs.PickOpenFile("AeroDesk settings bundle (*.json)|*.json");
        if (path is null) return;
        var replace = _dialogs.Confirm("Import Settings",
            "Replace existing settings and connections with the bundle? Choose No to merge instead.");
        try
        {
            _workspace.ImportBundle(path, replace);
            StatusText = "Settings imported.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Import Settings", ex.Message);
        }
    }

    [RelayCommand]
    private void About() => _dialogs.ShowInfo("About AeroDesk",
        $"{VersionText}\n\nAirline call-centre agent desktop for IATA Offers & Orders (NDC).\n" +
        "Order store: DocumentForge. Payments are simulated (mock, tokenized) — this demo never handles real card data.");

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    public async Task ShutdownAsync()
    {
        foreach (var node in Connections.OfType<ConnectionNodeViewModel>().ToList())
        {
            if (node.Retailing is not null) await node.Retailing.DisposeAsync();
            if (node.Operations is not null) await node.Operations.DisposeAsync();
            node.Auth?.Dispose();
        }
    }
}
