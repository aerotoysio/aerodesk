using System.Collections.ObjectModel;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using AeroDesk.ViewModels.Sale;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels;

/// <summary>
/// The reusable seats-and-extras picker: pick a flight + passenger, click seats
/// on the cabin grid, tick ancillaries. Choices are staged locally; the host
/// (sale wizard or order detail) turns them into one OrderChange via
/// <see cref="BuildChangeAsync"/>. Shared so the sale flow and order servicing
/// behave identically.
/// </summary>
public sealed partial class ExtrasSelectorViewModel : ObservableObject
{
    private readonly IRetailingService _service;
    private Order? _order;

    public ExtrasSelectorViewModel(IRetailingService service) => _service = service;

    public ObservableCollection<FlightSegment> Segments { get; } = [];
    public ObservableCollection<PaxChoiceViewModel> PaxChoices { get; } = [];
    public ObservableCollection<SeatRowViewModel> SeatRows { get; } = [];
    public ObservableCollection<AncillaryChoiceViewModel> AncillaryChoices { get; } = [];

    [ObservableProperty] private FlightSegment? _selectedSegment;
    [ObservableProperty] private PaxChoiceViewModel? _selectedPax;
    [ObservableProperty] private SeatMap? _seatMap;

    private readonly Dictionary<(string SegmentId, string PaxId), SeatOption> _pendingSeats = [];
    private readonly HashSet<(string SegmentId, string PaxId, string Code)> _pendingExtras = [];

    public async Task LoadAsync(Order order)
    {
        _order = order;
        ClearPending();
        Segments.Clear();
        foreach (var s in order.Segments) Segments.Add(s);
        PaxChoices.Clear();
        foreach (var p in order.Passengers.Where(p => p.Type != Ptc.INF))
            PaxChoices.Add(new PaxChoiceViewModel(p));
        SelectedPax = PaxChoices.FirstOrDefault();
        SelectedSegment = Segments.FirstOrDefault();
        await LoadSegmentAsync();
    }

    partial void OnSelectedSegmentChanged(FlightSegment? value) => _ = LoadSegmentAsync();

    partial void OnSelectedPaxChanged(PaxChoiceViewModel? value)
    {
        RefreshAncillaryTicks();
        RefreshSeatSelection();
        OnPropertyChanged(nameof(SelectedSeatSummary));
    }

    private async Task LoadSegmentAsync()
    {
        if (SelectedSegment is null) { SeatMap = null; return; }
        SeatMap = await _service.GetSeatMapAsync(SelectedSegment);
        var catalog = await _service.GetAncillaryCatalogAsync(SelectedSegment);
        AncillaryChoices.Clear();
        foreach (var option in catalog) AncillaryChoices.Add(new AncillaryChoiceViewModel(option));
        RefreshAncillaryTicks();
        OnPropertyChanged(nameof(SelectedSeatSummary));
    }

    partial void OnSeatMapChanged(SeatMap? value)
    {
        SeatRows.Clear();
        if (value is null) return;
        foreach (var group in value.Rows)
        {
            var byColumn = group.ToDictionary(s => s.Column);
            var cells = value.Columns
                .Select(col => col.Length == 0
                    ? new SeatCellViewModel()                                   // aisle
                    : new SeatCellViewModel { Seat = byColumn.GetValueOrDefault(col) })
                .ToList();
            SeatRows.Add(new SeatRowViewModel { Row = group.Key, Cells = cells });
        }
        RefreshSeatSelection();
    }

    public string SelectedSeatSummary =>
        SelectedSegment is null || SelectedPax is null ? "" :
        _pendingSeats.TryGetValue((SelectedSegment.SegmentId, SelectedPax.Passenger.PassengerId), out var seat)
            ? $"Selected: {seat.SeatNumber} ({seat.Price.Total:0.00} {seat.Price.Currency})"
            : CurrentAssignedSeat() is { } existing
                ? $"Currently assigned: {existing} — click a seat to change."
                : "No seat selected for this passenger on this flight.";

    private string? CurrentAssignedSeat() =>
        _order?.Seats.FirstOrDefault(s =>
            s.SegmentId == SelectedSegment?.SegmentId &&
            s.PassengerId == SelectedPax?.Passenger.PassengerId)?.SeatNumber;

    [RelayCommand]
    private void PickSeat(SeatOption? seat)
    {
        if (seat is null || SelectedSegment is null || SelectedPax is null || !seat.Available) return;
        _pendingSeats[(SelectedSegment.SegmentId, SelectedPax.Passenger.PassengerId)] = seat;
        RefreshSeatSelection();
        OnPropertyChanged(nameof(SelectedSeatSummary));
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(HasPending));
    }

    [RelayCommand]
    private void ToggleExtra(AncillaryChoiceViewModel choice)
    {
        if (SelectedSegment is null || SelectedPax is null) return;
        var key = (SelectedSegment.SegmentId, SelectedPax.Passenger.PassengerId, choice.Option.Code);
        if (!_pendingExtras.Add(key)) _pendingExtras.Remove(key);
        RefreshAncillaryTicks();
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(HasPending));
    }

    private void RefreshSeatSelection()
    {
        if (SelectedSegment is null || SelectedPax is null) return;
        _pendingSeats.TryGetValue((SelectedSegment.SegmentId, SelectedPax.Passenger.PassengerId), out var chosen);
        var assigned = chosen is null ? CurrentAssignedSeat() : null;
        foreach (var cell in SeatRows.SelectMany(r => r.Cells))
            cell.IsSelected = cell.Seat is not null &&
                (cell.Seat.SeatNumber == chosen?.SeatNumber || cell.Seat.SeatNumber == assigned);
    }

    private void RefreshAncillaryTicks()
    {
        if (SelectedSegment is null || SelectedPax is null) return;
        foreach (var c in AncillaryChoices)
            c.IsSelected = _pendingExtras.Contains((SelectedSegment.SegmentId, SelectedPax.Passenger.PassengerId, c.Option.Code));
    }

    public bool HasPending => _pendingSeats.Count > 0 || _pendingExtras.Count > 0;

    public string PendingSummary
    {
        get
        {
            var parts = new List<string>();
            if (_pendingSeats.Count > 0) parts.Add($"{_pendingSeats.Count} seat(s)");
            if (_pendingExtras.Count > 0) parts.Add($"{_pendingExtras.Count} extra(s)");
            return parts.Count == 0 ? "Nothing selected yet — seats and extras are optional." : "To add: " + string.Join(", ", parts);
        }
    }

    /// <summary>Turn the staged choices into one servicing change for the order.</summary>
    public async Task<OrderChange> BuildChangeAsync(string orderId)
    {
        var seats = _pendingSeats.Select(kv => new SeatAssignment(
            kv.Key.SegmentId, kv.Key.PaxId, kv.Value.SeatNumber, kv.Value.Price)).ToList();

        var ancillaries = new List<Ancillary>();
        foreach (var (segmentId, paxId, code) in _pendingExtras)
        {
            var segment = Segments.First(s => s.SegmentId == segmentId);
            var option = (await _service.GetAncillaryCatalogAsync(segment)).First(o => o.Code == code);
            ancillaries.Add(new Ancillary(Guid.NewGuid().ToString("N"), option.Code, option.Name,
                segmentId, paxId, option.Price));
        }
        return new OrderChange(orderId, ancillaries, [], seats);
    }

    public void ClearPending()
    {
        _pendingSeats.Clear();
        _pendingExtras.Clear();
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(HasPending));
    }
}
