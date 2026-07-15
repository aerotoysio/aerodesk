using System.Collections.ObjectModel;
using AeroDesk.Core.Operations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels.Operations;

/// <summary>The departures board — pick a station + day, list its flights, open one.</summary>
public sealed partial class DeparturesDocumentViewModel : DocumentViewModel
{
    private readonly Action<DepartureFlight> _openFlight;

    public IOperationsService Operations { get; }

    [ObservableProperty] private string _station;
    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty] private DepartureFlight? _selectedFlight;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    public ObservableCollection<DepartureFlight> Flights { get; } = [];

    public DeparturesDocumentViewModel(IOperationsService operations, Action<DepartureFlight> openFlight)
    {
        Operations = operations;
        _openFlight = openFlight;
        _station = operations.DefaultStation;
        Title = "Departures";
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try
        {
            var station = Station.Trim().ToUpperInvariant();
            var list = await Operations.ListDeparturesAsync(station, DateOnly.FromDateTime(Date));
            Flights.Clear();
            foreach (var f in list) Flights.Add(f);
            if (Flights.Count == 0) Error = $"No departures from {station} on {Date:yyyy-MM-dd}.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFlight()
    {
        if (SelectedFlight is { } flight) _openFlight(flight);
    }
}
