namespace AeroDesk.Core.Domain;

/// <summary>One requested journey leg: fly from origin to destination on a date.</summary>
public sealed record JourneyLeg(string Origin, string Destination, DateOnly DepartureDate);

/// <summary>
/// AirShopping request. One leg = one-way, two mirrored legs = return, N legs = multi-city.
/// Passenger counts are by PTC.
/// </summary>
public sealed record ShopRequest
{
    public required IReadOnlyList<JourneyLeg> Legs { get; init; }
    public TripType TripType { get; init; } = TripType.OneWay;
    public int Adults { get; init; } = 1;
    public int Children { get; init; }
    public int Infants { get; init; }
    public Cabin Cabin { get; init; } = Cabin.Economy;

    public int SeatedPassengers => Adults + Children; // infants travel on an adult's lap
    public int TotalPassengers => Adults + Children + Infants;
}

/// <summary>A single operated flight leg inside an offer or order.</summary>
public sealed record FlightSegment(
    string SegmentId,
    string Carrier,
    string FlightNumber,
    string Origin,
    string Destination,
    DateTime DepartureUtc,
    DateTime ArrivalUtc,
    string Equipment,
    Cabin Cabin,
    string BookingClass);

/// <summary>Fare basis + branded fare family for a set of segments.</summary>
public sealed record FareComponent(
    string FareBasisCode,
    string FareFamily,          // e.g. "Basic", "Flex", "Business Flex"
    string BaggageAllowance,    // e.g. "1 x 23kg"
    bool Changeable,
    bool Refundable);

/// <summary>One priced item inside an offer: a fare for one passenger type across segments.</summary>
public sealed record OfferItem(
    string OfferItemId,
    Ptc PassengerType,
    int PassengerCount,
    IReadOnlyList<string> SegmentIds,
    FareComponent Fare,
    PriceDetail PricePerPassenger)
{
    public PriceDetail TotalPrice => new(
        PricePerPassenger.BaseAmount * PassengerCount,
        PricePerPassenger.Taxes * PassengerCount,
        PricePerPassenger.Currency);
}

/// <summary>
/// A priced, expiring proposal from the airline: segments + per-PTC offer items.
/// Immutable — a reprice produces a new Offer.
/// </summary>
public sealed record Offer
{
    public required string OfferId { get; init; }
    public required string Owner { get; init; }                       // airline designator, e.g. "AT"
    public required IReadOnlyList<FlightSegment> Segments { get; init; }
    public required IReadOnlyList<OfferItem> Items { get; init; }
    public required TimeLimit OfferExpiry { get; init; }
    public required string Currency { get; init; }

    public PriceDetail TotalPrice => PriceDetail.Sum(Items.Select(i => i.TotalPrice), Currency);
    public string FareFamily => Items.Count > 0 ? Items[0].Fare.FareFamily : "";
}
