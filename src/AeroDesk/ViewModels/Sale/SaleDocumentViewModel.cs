using System.Collections.ObjectModel;
using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using AeroDesk.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels.Sale;

public enum SaleStage { Search, Results, Passengers, Extras, Review, Payment, Confirmation }

/// <summary>
/// The guided New Sale flow: search → pick a fare → capture passengers →
/// OrderCreate (PendingPayment) → seats &amp; extras (OrderChange) → review →
/// payment → confirmation. One instance per sale tab.
/// </summary>
public sealed partial class SaleDocumentViewModel : DocumentViewModel
{
    private readonly IRetailingService _service;
    private readonly IDialogService _dialogs;

    public SaleDocumentViewModel(IRetailingService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        Title = "New Sale";
        Legs.Add(new LegRowViewModel());
        RefreshLegs();

        var names = new[] { "Search", "Flights", "Passengers", "Seats & Extras", "Review", "Payment", "Done" };
        for (var i = 0; i < names.Length; i++)
            Steps.Add(new StepViewModel { Name = names[i], Index = i });
        SyncSteps();
    }

    // ---------------- stage machinery ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageIndex))]
    private SaleStage _stage = SaleStage.Search;

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    public int StageIndex => (int)Stage;

    partial void OnStageChanged(SaleStage value) => SyncSteps();

    private void SyncSteps()
    {
        foreach (var step in Steps)
        {
            step.IsCurrent = step.Index == StageIndex;
            step.IsDone = step.Index < StageIndex;
        }
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorText = "";

    private async Task RunAsync(Func<Task> action)
    {
        ErrorText = "";
        IsBusy = true;
        try { await action(); }
        catch (EtagConflictException)
        {
            ErrorText = "This order was changed elsewhere — it has been reloaded; please retry.";
            await ReloadOrderAsync();
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsBusy = false; }
    }

    // ---------------- Search stage ----------------

    public IReadOnlyList<string> Airports => FlightSchedule.Airports;
    public IReadOnlyList<Cabin> Cabins { get; } = Enum.GetValues<Cabin>();
    public IReadOnlyList<int> PaxCounts { get; } = [0, 1, 2, 3, 4, 5, 6];
    public IReadOnlyList<int> AdultCounts { get; } = [1, 2, 3, 4, 5, 6];

    [ObservableProperty] private TripType _tripType = TripType.Return;
    [ObservableProperty] private int _adults = 1;
    [ObservableProperty] private int _children;
    [ObservableProperty] private int _infants;
    [ObservableProperty] private Cabin _cabin = Cabin.Economy;

    public ObservableCollection<LegRowViewModel> Legs { get; } = [];

    public bool IsMultiCity => TripType == TripType.MultiCity;
    public bool IsReturn => TripType == TripType.Return;

    partial void OnTripTypeChanged(TripType value)
    {
        RefreshLegs();
        OnPropertyChanged(nameof(IsMultiCity));
        OnPropertyChanged(nameof(IsReturn));
    }

    private void RefreshLegs()
    {
        // One-way/return keep a single editable leg (return adds an inbound date);
        // multi-city keeps at least two.
        while (Legs.Count > 1 && TripType != TripType.MultiCity) Legs.RemoveAt(Legs.Count - 1);
        if (TripType == TripType.MultiCity && Legs.Count < 2) AddLeg();
    }

    [RelayCommand]
    private void AddLeg()
    {
        var previous = Legs.LastOrDefault();
        Legs.Add(new LegRowViewModel
        {
            Origin = previous?.Destination ?? "",
            DepartureDate = previous?.DepartureDate?.AddDays(3),
        });
    }

    [RelayCommand]
    private void RemoveLeg(LegRowViewModel leg)
    {
        if (Legs.Count > 2) Legs.Remove(leg);
    }

    [ObservableProperty] private DateTime? _returnDate;

    [RelayCommand]
    private Task SearchAsync() => RunAsync(async () =>
    {
        var request = BuildRequest();
        var offers = await _service.SearchOffersAsync(request);
        ItineraryCards.Clear();
        foreach (var group in offers.GroupBy(o => string.Join("|", o.Segments.Select(s => s.SegmentId))))
            ItineraryCards.Add(new ItineraryCardViewModel(group.ToList(), SelectOfferCommand));
        if (ItineraryCards.Count == 0)
        {
            ErrorText = "No flights on that routing/date — the demo airline serves: " + string.Join(", ", Airports);
            return;
        }
        Stage = SaleStage.Results;
    });

    private ShopRequest BuildRequest()
    {
        var legs = new List<JourneyLeg>();
        foreach (var row in Legs)
            legs.Add(row.ToLeg());

        if (TripType == TripType.Return)
        {
            if (ReturnDate is null) throw new ArgumentException("Pick a return date.");
            var outbound = legs[0];
            legs.Add(new JourneyLeg(outbound.Destination, outbound.Origin, DateOnly.FromDateTime(ReturnDate.Value)));
        }

        return new ShopRequest
        {
            Legs = legs,
            TripType = TripType,
            Adults = Adults,
            Children = Children,
            Infants = Infants,
            Cabin = Cabin,
        };
    }

    // ---------------- Results stage ----------------

    public ObservableCollection<ItineraryCardViewModel> ItineraryCards { get; } = [];

    [ObservableProperty] private Offer? _selectedOffer;

    [RelayCommand]
    private void SelectOffer(Offer offer)
    {
        SelectedOffer = offer;
        Title = $"Sale — {offer.Segments[0].Origin}→{offer.Segments[^1].Destination}";
        PassengerForms.Clear();
        for (var i = 0; i < Adults; i++) PassengerForms.Add(new PassengerFormViewModel(Ptc.ADT, $"ADT{i + 1}", i == 0));
        for (var i = 0; i < Children; i++) PassengerForms.Add(new PassengerFormViewModel(Ptc.CHD, $"CHD{i + 1}", false));
        for (var i = 0; i < Infants; i++) PassengerForms.Add(new PassengerFormViewModel(Ptc.INF, $"INF{i + 1}", false));
        Stage = SaleStage.Passengers;
    }

    [RelayCommand]
    private void BackToSearch() => Stage = SaleStage.Search;

    [RelayCommand]
    private void BackToResults() => Stage = SaleStage.Results;

    // ---------------- Passengers stage ----------------

    public ObservableCollection<PassengerFormViewModel> PassengerForms { get; } = [];

    /// <summary>Leaving Passengers creates the airline Order (PendingPayment).</summary>
    [RelayCommand]
    private Task CreateOrderAsync() => RunAsync(async () =>
    {
        if (SelectedOffer is null) return;
        var errors = PassengerForms.SelectMany(p => p.Validate()).ToList();
        if (errors.Count > 0) { ErrorText = string.Join("\n", errors.Take(4)); return; }

        var passengers = PassengerForms.Select(p => p.ToPassenger()).ToList();
        Envelope = await _service.CreateOrderAsync(SelectedOffer.OfferId, passengers);
        await Extras.LoadAsync(Envelope.Order);
        Stage = SaleStage.Extras;
    });

    // ---------------- Extras stage (seats + ancillaries) ----------------

    [ObservableProperty] private OrderEnvelope? _envelope;

    public Order? Order => Envelope?.Order;

    partial void OnEnvelopeChanged(OrderEnvelope? value)
    {
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(TotalLine));
    }

    /// <summary>The shared seats-and-extras picker (same component as order servicing).</summary>
    public ExtrasSelectorViewModel Extras => _extras ??= new ExtrasSelectorViewModel(_service);
    private ExtrasSelectorViewModel? _extras;

    [RelayCommand]
    private Task ApplyExtrasAsync() => RunAsync(async () =>
    {
        if (Envelope is null || Order is null) return;
        if (Extras.HasPending)
        {
            var change = await Extras.BuildChangeAsync(Order.OrderId);
            Envelope = await _service.ChangeOrderAsync(change, Envelope.Etag);
            Extras.ClearPending();
        }
        Stage = SaleStage.Review;
    });

    // ---------------- Review / Payment / Confirmation ----------------

    public string TotalLine => Order is null ? "" :
        $"{Order.TotalPrice.Total:N2} {Order.Currency}  (fare {Order.TotalPrice.BaseAmount:N2} + taxes/fees {Order.TotalPrice.Taxes:N2})";

    [RelayCommand]
    private void BackToExtras() => Stage = SaleStage.Extras;

    [RelayCommand]
    private void GoToPayment() => Stage = SaleStage.Payment;

    [RelayCommand]
    private void GoToReview() => Stage = SaleStage.Review;

    // Tokenized demo payment — never a real PAN/CVV (PCI note in IPaymentGateway).
    [ObservableProperty] private string _cardToken = "tok_demo_visa";
    [ObservableProperty] private string _cardLast4 = "4242";

    [RelayCommand]
    private Task PayAsync() => RunAsync(async () =>
    {
        if (Envelope is null || Order is null) return;
        if (CardLast4.Length != 4 || !CardLast4.All(char.IsAsciiDigit))
            throw new ArgumentException("Last-4 must be exactly four digits.");
        Envelope = await _service.PayOrderAsync(Order.OrderId, new PaymentToken(CardToken, CardLast4), Envelope.Etag);
        Stage = SaleStage.Confirmation;
    });

    private async Task ReloadOrderAsync()
    {
        if (Order is null) return;
        var fresh = await _service.GetOrderAsync(Order.OrderId);
        if (fresh is not null) Envelope = fresh;
    }

    [RelayCommand]
    private void StartNewSale()
    {
        Envelope = null;
        SelectedOffer = null;
        ItineraryCards.Clear();
        PassengerForms.Clear();
        Extras.ClearPending();
        Title = "New Sale";
        Stage = SaleStage.Search;
    }
}
