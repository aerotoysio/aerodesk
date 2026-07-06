using System.Collections.ObjectModel;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using AeroDesk.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels.Orders;

/// <summary>The Orders workspace: recent orders + open by OrderID / record locator.</summary>
public sealed partial class OrdersDocumentViewModel : DocumentViewModel
{
    private readonly IDialogService _dialogs;
    private readonly Action<OrderEnvelope> _openDetail;

    public IRetailingService Service { get; }

    public OrdersDocumentViewModel(IRetailingService service, IDialogService dialogs, Action<OrderEnvelope> openDetail)
    {
        Service = service;
        _dialogs = dialogs;
        _openDetail = openDetail;
        Title = "Orders";
        _ = RefreshAsync();
    }

    public ObservableCollection<OrderRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusLine = "";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var orders = await Service.ListOrdersAsync(50);
            Rows.Clear();
            foreach (var envelope in orders)
                Rows.Add(new OrderRowViewModel(envelope, OpenCommand));
            StatusLine = Rows.Count == 0
                ? "No orders yet — make a sale to see it here."
                : $"{Rows.Count} order(s), newest first.";
        }
        catch (Exception ex)
        {
            StatusLine = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var key = SearchText.Trim();
        if (key.Length == 0) { await RefreshAsync(); return; }

        IsBusy = true;
        try
        {
            var envelope = await Service.GetOrderAsync(key);
            if (envelope is null)
            {
                StatusLine = $"No order found for '{key}' (try the OrderID or the 6-letter locator).";
                return;
            }
            _openDetail(envelope);
            StatusLine = "";
        }
        catch (Exception ex)
        {
            StatusLine = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Open(OrderRowViewModel row) => _openDetail(row.Envelope);
}

/// <summary>One row in the orders list.</summary>
public sealed class OrderRowViewModel
{
    public OrderEnvelope Envelope { get; }
    public IRelayCommand<OrderRowViewModel> OpenCommand { get; }

    public OrderRowViewModel(OrderEnvelope envelope, IRelayCommand<OrderRowViewModel> openCommand)
    {
        Envelope = envelope;
        OpenCommand = openCommand;
    }

    private Order Order => Envelope.Order;

    public string OrderId => Order.OrderId;
    public string Locator => Order.RecordLocator;
    public OrderStatus Status => Order.Status;
    public string Route => string.Join(" → ", Order.Segments.Select(s => s.Origin).Append(Order.Segments[^1].Destination));
    public string FirstDeparture => Order.Segments.Count == 0 ? "" : Order.Segments[0].DepartureUtc.ToString("ddd d MMM HH:mm");
    public string PaxLine => $"{Order.Passengers.Count} pax";
    public string Total => $"{Order.TotalPrice.Total:N2} {Order.Currency}";
    public string Created => Order.CreatedAtUtc.ToString("d MMM HH:mm");
}
