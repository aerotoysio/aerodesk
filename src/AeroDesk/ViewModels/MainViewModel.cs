using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
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
            await AttachAsync(new InMemoryRetailingService(), "Offline demo (in-memory)");
            return;
        }

        if (request.Save)
            _workspace.UpsertConnection(request.Descriptor, request.ApiKey);

        var service = new DocumentForgeRetailingService(request.Descriptor, request.ApiKey);
        await AttachAsync(service, request.Descriptor.Name);
    }

    [RelayCommand]
    private Task WorkOfflineAsync() => AttachAsync(new InMemoryRetailingService(), "Offline demo (in-memory)");

    private async Task AttachAsync(IRetailingService service, string displayName)
    {
        StatusText = $"Connecting to {displayName}…";
        try
        {
            await service.ConnectAsync();
            Connections.Add(new ConnectionNodeViewModel(this, service, displayName));
            StatusText = service is DocumentForgeRetailingService df && await HealthLineAsync(df) is { } health
                ? $"Connected to {displayName} — {health}"
                : $"Connected to {displayName}";
        }
        catch (Exception ex)
        {
            await service.DisposeAsync();
            StatusText = "Connection failed.";
            _dialogs.ShowError("Connect", ex.Message);
        }
    }

    private static async Task<string?> HealthLineAsync(DocumentForgeRetailingService service)
    {
        try
        {
            var (healthy, status, version) = await service.GetHealthAsync();
            return $"health: {status}{(version is null ? "" : $", v{version}")}{(healthy ? "" : " (degraded)")}";
        }
        catch
        {
            return null;
        }
    }

    public async Task DisconnectAsync(ConnectionNodeViewModel node)
    {
        Connections.Remove(node);
        await node.Service.DisposeAsync();
        StatusText = $"Disconnected from {node.Name}.";
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
            await node.Service.DisposeAsync();
    }
}
