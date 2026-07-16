using System.Collections.ObjectModel;
using AeroDesk.Core.Operations;
using AeroDesk.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels.Operations;

/// <summary>
/// One flight's departure-control screen: header + status actions (Start Boarding /
/// Depart) and the passenger manifest with per-passenger Check In / Board and Board
/// All. Every action re-reads from the service so the view reflects backend state.
/// </summary>
public sealed partial class FlightDocumentViewModel : DocumentViewModel
{
    private readonly IOperationsService _ops;
    private readonly IDialogService _dialogs;

    public Guid FlightId { get; }

    [ObservableProperty] private DepartureFlight _flight;
    [ObservableProperty] private ManifestPassenger? _selectedPassenger;
    [ObservableProperty] private string _seatInput = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _statusText;

    public ObservableCollection<ManifestPassenger> Manifest { get; } = [];

    public FlightDocumentViewModel(IOperationsService ops, IDialogService dialogs, DepartureFlight flight)
    {
        _ops = ops;
        _dialogs = dialogs;
        _flight = flight;
        FlightId = flight.Id;
        Title = $"Flight {flight.FlightNumber ?? flight.Id.ToString()[..8]}";
        _ = RefreshAsync();
    }

    // Status-driven affordances.
    public bool CanStartBoarding => Flight.Status == FlightOpStatus.Scheduled;
    public bool CanDepart => Flight.Status == FlightOpStatus.Boarding;
    public bool IsClosed => Flight.Status is FlightOpStatus.Departed or FlightOpStatus.Cancelled;

    partial void OnFlightChanged(DepartureFlight value)
    {
        OnPropertyChanged(nameof(CanStartBoarding));
        OnPropertyChanged(nameof(CanDepart));
        OnPropertyChanged(nameof(IsClosed));
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        if (await _ops.GetFlightAsync(FlightId) is { } f) Flight = f;
        await ReloadManifestAsync();
        StatusText = $"{Flight.Status} — {Manifest.Count(p => p.Status == PaxOpStatus.Boarded)}/{Manifest.Count} boarded";
    });

    [RelayCommand]
    private Task StartBoardingAsync() => RunAsync(async () =>
    {
        Flight = await _ops.ChangeStatusAsync(FlightId, FlightOpAction.StartBoarding);
        StatusText = "Boarding started.";
    });

    [RelayCommand]
    private Task DepartAsync() => RunAsync(async () =>
    {
        if (!_dialogs.Confirm("Depart flight",
            $"Depart {Flight.FlightNumber} to {Flight.ArrivalStation}? This closes the flight."))
            return;
        Flight = await _ops.ChangeStatusAsync(FlightId, FlightOpAction.Depart);
        StatusText = "Flight departed.";
    });

    [RelayCommand]
    private Task CheckInAsync() => RunAsync(async () =>
    {
        if (SelectedPassenger is not { } pax) return;
        var (row, col) = ParseSeat(SeatInput);
        await _ops.CheckInAsync(FlightId, pax.PassengerId, row, col);
        await ReloadManifestAsync();
        StatusText = $"Checked in {pax.FullName}.";
    });

    [RelayCommand]
    private Task BoardAsync() => RunAsync(async () =>
    {
        if (SelectedPassenger is not { } pax) return;
        await _ops.BoardAsync(FlightId, pax.PassengerId);
        await ReloadManifestAsync();
        StatusText = $"Boarded {pax.FullName}.";
    });

    [RelayCommand]
    private Task BoardAllAsync() => RunAsync(async () =>
    {
        var boarded = await _ops.BoardAllAsync(FlightId);
        await ReloadManifestAsync();
        StatusText = $"Boarded {boarded} passenger(s).";
    });

    private async Task ReloadManifestAsync()
    {
        var manifest = await _ops.GetManifestAsync(FlightId);
        var selectedId = SelectedPassenger?.PassengerId;
        Manifest.Clear();
        foreach (var p in manifest) Manifest.Add(p);
        if (selectedId is { } id)
            SelectedPassenger = Manifest.FirstOrDefault(p => p.PassengerId == id);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Parse a seat like "12C" into (row, column). Empty → no seat.</summary>
    private static (int? Row, string? Col) ParseSeat(string seat)
    {
        seat = seat.Trim().ToUpperInvariant();
        if (seat.Length < 2) return (null, null);
        var digits = new string(seat.TakeWhile(char.IsDigit).ToArray());
        var letters = new string(seat.SkipWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var row) && letters.Length > 0 ? (row, letters) : (null, null);
    }
}
