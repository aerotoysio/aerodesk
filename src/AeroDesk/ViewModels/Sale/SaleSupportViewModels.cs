using AeroDesk.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels.Sale;

/// <summary>One journey leg row in the search form.</summary>
public sealed partial class LegRowViewModel : ObservableObject
{
    [ObservableProperty] private string _origin = "";
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private DateTime? _departureDate = DateTime.Today.AddDays(14);

    public JourneyLeg ToLeg()
    {
        if (string.IsNullOrWhiteSpace(Origin)) throw new ArgumentException("Pick an origin airport.");
        if (string.IsNullOrWhiteSpace(Destination)) throw new ArgumentException("Pick a destination airport.");
        if (Origin == Destination) throw new ArgumentException("Origin and destination must differ.");
        if (DepartureDate is null) throw new ArgumentException("Pick a departure date.");
        if (DepartureDate.Value.Date < DateTime.Today) throw new ArgumentException("Departure date is in the past.");
        return new JourneyLeg(Origin, Destination, DateOnly.FromDateTime(DepartureDate.Value));
    }
}

/// <summary>One itinerary (same flights) offered in several fare families.</summary>
public sealed class ItineraryCardViewModel
{
    public IReadOnlyList<Offer> Offers { get; }
    public IRelayCommand<Offer> SelectCommand { get; }

    public ItineraryCardViewModel(IReadOnlyList<Offer> offers, IRelayCommand<Offer> selectCommand)
    {
        Offers = offers;
        SelectCommand = selectCommand;
    }

    private Offer First => Offers[0];
    public IReadOnlyList<FlightSegment> Segments => First.Segments;

    public string RouteLine => string.Join("  →  ", Segments.Select(s => s.Origin).Append(Segments[^1].Destination));
    public string Summary => string.Join("   •   ", Segments.Select(s =>
        $"{s.FlightNumber}  {s.DepartureUtc:ddd d MMM HH:mm}–{s.ArrivalUtc:HH:mm}  ({s.Equipment})"));
    public string CabinLine => $"{First.Segments[0].Cabin} • {Segments.Count} flight(s)";
}

/// <summary>Per-passenger capture form with friendly validation.</summary>
public sealed partial class PassengerFormViewModel : ObservableObject
{
    public Ptc Type { get; }
    public string PassengerId { get; }
    public bool IsLead { get; }

    public PassengerFormViewModel(Ptc type, string passengerId, bool isLead)
    {
        Type = type;
        PassengerId = passengerId;
        IsLead = isLead;
    }

    public string Header => Type switch
    {
        Ptc.CHD => $"{PassengerId} — Child (2–11)",
        Ptc.INF => $"{PassengerId} — Infant (on lap)",
        _ => IsLead ? $"{PassengerId} — Adult (lead passenger)" : $"{PassengerId} — Adult",
    };

    public IReadOnlyList<string> Titles { get; } = ["Ms", "Mr", "Mrs", "Mx", "Dr"];

    [ObservableProperty] private string _title = "Ms";
    [ObservableProperty] private string _givenName = "";
    [ObservableProperty] private string _surname = "";
    [ObservableProperty] private DateTime? _dateOfBirth;
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _documentNumber = "";
    [ObservableProperty] private string _documentCountry = "";
    [ObservableProperty] private DateTime? _documentExpiry;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(GivenName)) errors.Add($"{PassengerId}: given name is required.");
        if (string.IsNullOrWhiteSpace(Surname)) errors.Add($"{PassengerId}: surname is required.");
        if (Type != Ptc.ADT && DateOfBirth is null) errors.Add($"{PassengerId}: date of birth is required for children/infants.");
        if (IsLead && string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(Phone))
            errors.Add("Lead passenger needs an email or phone for the confirmation.");
        if (DocumentNumber.Length > 0 && DocumentExpiry is null)
            errors.Add($"{PassengerId}: travel document needs an expiry date.");
        return errors;
    }

    public Passenger ToPassenger() => new()
    {
        PassengerId = PassengerId,
        Type = Type,
        GivenName = GivenName.Trim(),
        Surname = Surname.Trim(),
        Title = Title,
        DateOfBirth = DateOfBirth is { } dob ? DateOnly.FromDateTime(dob) : null,
        Email = Email.Trim(),
        Phone = Phone.Trim(),
        Document = DocumentNumber.Trim().Length == 0 ? null : new TravelDocument(
            "PT", DocumentNumber.Trim(), DocumentCountry.Trim(),
            DocumentExpiry is { } exp ? DateOnly.FromDateTime(exp) : DateOnly.MaxValue),
    };
}

/// <summary>A seated passenger in the extras stage's picker.</summary>
public sealed class PaxChoiceViewModel
{
    public Passenger Passenger { get; }
    public PaxChoiceViewModel(Passenger passenger) => Passenger = passenger;
    public string Display => $"{Passenger.GivenName} {Passenger.Surname} ({Passenger.Type})";
}

/// <summary>An ancillary option with a live checkbox tick.</summary>
public sealed partial class AncillaryChoiceViewModel : ObservableObject
{
    public AncillaryOption Option { get; }
    public AncillaryChoiceViewModel(AncillaryOption option) => Option = option;

    [ObservableProperty] private bool _isSelected;

    public string Display => $"{Option.Name} — {Option.Price.Total:0.00} {Option.Price.Currency}";
}

/// <summary>One rendered row of the seat map.</summary>
public sealed class SeatRowViewModel
{
    public int Row { get; init; }
    public IReadOnlyList<SeatCellViewModel> Cells { get; init; } = [];
}

/// <summary>A seat button or an aisle spacer in the seat-map grid.</summary>
public sealed partial class SeatCellViewModel : ObservableObject
{
    public SeatOption? Seat { get; init; }
    public bool IsAisle => Seat is null;
    public string Label => Seat?.Column ?? "";
    public string Tooltip => Seat is null ? "" :
        $"{Seat.SeatNumber} • {Seat.Zone}{(Seat.ExitRow ? " • Exit row" : "")} • {Seat.Price.Total:0.00} {Seat.Price.Currency}";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>One entry in the wizard's step rail.</summary>
public sealed partial class StepViewModel : ObservableObject
{
    public string Name { get; init; } = "";
    public int Index { get; init; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isDone;
}
