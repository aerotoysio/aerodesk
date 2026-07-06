using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using AeroDesk.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels.Orders;

/// <summary>
/// One open order: itinerary, passengers, services, documents, and the
/// servicing actions (pay, add seats/extras, cancel) — all ETag-guarded.
/// </summary>
public sealed partial class OrderDetailDocumentViewModel : DocumentViewModel
{
    private readonly IRetailingService _service;
    private readonly IDialogService _dialogs;

    public OrderDetailDocumentViewModel(IRetailingService service, IDialogService dialogs, OrderEnvelope envelope)
    {
        _service = service;
        _dialogs = dialogs;
        Extras = new ExtrasSelectorViewModel(service);
        Envelope = envelope;
        _ = Extras.LoadAsync(envelope.Order);
    }

    [ObservableProperty] private OrderEnvelope? _envelope;

    public Order? Order => Envelope?.Order;
    public ExtrasSelectorViewModel Extras { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";

    partial void OnEnvelopeChanged(OrderEnvelope? value)
    {
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(TotalLine));
        OnPropertyChanged(nameof(IsPendingPayment));
        OnPropertyChanged(nameof(CanModify));
        OnPropertyChanged(nameof(StatusBrushKey));
        Title = value is null ? "Order" : $"Order {value.Order.RecordLocator}";
    }

    public bool IsPendingPayment => Order?.Status == OrderStatus.PendingPayment;
    public bool CanModify => Order?.Status is OrderStatus.PendingPayment or OrderStatus.Paid or OrderStatus.Ticketed;

    public string StatusBrushKey => Order?.Status switch
    {
        OrderStatus.Ticketed or OrderStatus.Paid => "Success",
        OrderStatus.Cancelled => "Danger",
        _ => "Warning",
    };

    public string TotalLine => Order is null ? "" :
        $"{Order.TotalPrice.Total:N2} {Order.Currency}  (fare {Order.TotalPrice.BaseAmount:N2} + taxes/fees {Order.TotalPrice.Taxes:N2})";

    private async Task RunAsync(Func<Task> action)
    {
        ErrorText = "";
        IsBusy = true;
        try { await action(); }
        catch (EtagConflictException)
        {
            ErrorText = "This order was changed elsewhere — it has been reloaded; please retry.";
            await ReloadAsync();
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        if (Order is null) return;
        var fresh = await _service.GetOrderAsync(Order.OrderId);
        if (fresh is null) { ErrorText = "The order no longer exists."; return; }
        Envelope = fresh;
        await Extras.LoadAsync(fresh.Order);
    }

    // Tokenized demo payment — never a real PAN/CVV.
    [ObservableProperty] private string _cardToken = "tok_demo_visa";
    [ObservableProperty] private string _cardLast4 = "4242";

    [RelayCommand]
    private Task PayAsync() => RunAsync(async () =>
    {
        if (Envelope is null || Order is null) return;
        if (!_dialogs.Confirm("Take payment",
            $"Authorize {Order.TotalPrice.Total:N2} {Order.Currency} on card •••• {CardLast4} and issue documents?"))
            return;
        Envelope = await _service.PayOrderAsync(Order.OrderId, new PaymentToken(CardToken, CardLast4), Envelope.Etag);
        await Extras.LoadAsync(Envelope.Order);
    });

    [RelayCommand]
    private Task ApplyExtrasAsync() => RunAsync(async () =>
    {
        if (Envelope is null || Order is null) return;
        if (!Extras.HasPending) { ErrorText = "Pick a seat or tick an extra first."; return; }
        var change = await Extras.BuildChangeAsync(Order.OrderId);
        Envelope = await _service.ChangeOrderAsync(change, Envelope.Etag);
        await Extras.LoadAsync(Envelope.Order);
    });

    [RelayCommand]
    private Task RemoveAncillaryAsync(Ancillary ancillary) => RunAsync(async () =>
    {
        if (Envelope is null || Order is null) return;
        Envelope = await _service.ChangeOrderAsync(
            new OrderChange(Order.OrderId, [], [ancillary.ServiceId], []), Envelope.Etag);
        await Extras.LoadAsync(Envelope.Order);
    });

    // ---- Change flight (rebook) ----

    [ObservableProperty] private FlightSegment? _changeSegment;
    [ObservableProperty] private DateTime? _changeDate;
    [ObservableProperty] private FlightSegment? _selectedAlternative;
    public System.Collections.ObjectModel.ObservableCollection<FlightSegment> Alternatives { get; } = [];

    public string ChangeFeeLine
    {
        get
        {
            if (Order is null) return "";
            try
            {
                var fee = Core.Retailing.OrderFactory.GetFlightChangeFee(Order);
                return fee.Total > 0
                    ? $"Change fee: {fee.Total:N2} {fee.Currency} ({Order.Passengers.Count(p => p.Type != Ptc.INF)} pax)"
                    : "Free changes on this fare.";
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }
    }

    partial void OnChangeSegmentChanged(FlightSegment? value)
    {
        Alternatives.Clear();
        SelectedAlternative = null;
        if (value is not null) ChangeDate = value.DepartureUtc.Date.AddDays(1);
    }

    [RelayCommand]
    private Task FindAlternativesAsync() => RunAsync(async () =>
    {
        if (ChangeSegment is null || ChangeDate is null)
            throw new ArgumentException("Pick the flight to change and a new date first.");
        var flights = await _service.GetAlternativeFlightsAsync(ChangeSegment, DateOnly.FromDateTime(ChangeDate.Value));
        Alternatives.Clear();
        foreach (var f in flights) Alternatives.Add(f);
        if (Alternatives.Count == 0) ErrorText = "No flights on that route/date.";
        SelectedAlternative = Alternatives.FirstOrDefault();
    });

    [RelayCommand]
    private Task RebookAsync() => RunAsync(async () =>
    {
        if (Envelope is null || Order is null || ChangeSegment is null) return;
        if (SelectedAlternative is null) throw new ArgumentException("Pick a replacement flight.");

        var fee = Core.Retailing.OrderFactory.GetFlightChangeFee(Order); // throws for non-changeable fares
        var feeText = fee.Total > 0 ? $"A change fee of {fee.Total:N2} {fee.Currency} applies." : "No change fee (flexible fare).";
        if (!_dialogs.Confirm("Change flight",
            $"Move from {ChangeSegment.FlightNumber} ({ChangeSegment.DepartureUtc:d MMM HH:mm}) to " +
            $"{SelectedAlternative.FlightNumber} ({SelectedAlternative.DepartureUtc:d MMM HH:mm})?\n\n" +
            $"{feeText} Seats on the old flight are released."))
            return;

        Envelope = await _service.ChangeFlightAsync(Order.OrderId, ChangeSegment.SegmentId, SelectedAlternative, Envelope.Etag);
        ChangeSegment = null;
        Alternatives.Clear();
        await Extras.LoadAsync(Envelope.Order);
        OnPropertyChanged(nameof(ChangeFeeLine));
    });

    [RelayCommand]
    private Task CancelOrderAsync() => RunAsync(async () =>
    {
        if (Envelope is null || Order is null) return;
        if (!_dialogs.Confirm("Cancel order",
            $"Cancel order {Order.OrderId} ({Order.RecordLocator})? " +
            (Order.Status == OrderStatus.Ticketed ? "A refund will be simulated." : "The held fare will be released.")))
            return;
        Envelope = await _service.CancelOrderAsync(Order.OrderId, Envelope.Etag);
    });
}
