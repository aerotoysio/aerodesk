namespace AeroDesk.Core.Domain;

/// <summary>Travel document / form of identification for APIS.</summary>
public sealed record TravelDocument(
    string Type,                // e.g. "PT" passport
    string Number,
    string IssuingCountry,
    DateOnly ExpiryDate);

public sealed record Passenger
{
    public required string PassengerId { get; init; }
    public required Ptc Type { get; init; }
    public required string GivenName { get; init; }
    public required string Surname { get; init; }
    public string Title { get; init; } = "";
    public DateOnly? DateOfBirth { get; init; }
    public string Email { get; init; } = "";
    public string Phone { get; init; } = "";
    public TravelDocument? Document { get; init; }
}

/// <summary>An ancillary service attached to an order (bag, meal, seat…).</summary>
public sealed record Ancillary(
    string ServiceId,
    string Code,                // e.g. "XBAG", "SEAT", "MEAL"
    string Name,
    string SegmentId,
    string PassengerId,
    PriceDetail Price);

public sealed record SeatAssignment(
    string SegmentId,
    string PassengerId,
    string SeatNumber,          // e.g. "12A"
    PriceDetail Price);

/// <summary>Mock-gateway payment record. Card data is TOKENIZED — token + last-4
/// only. Full PAN and CVV are NEVER accepted, transmitted, or persisted.
/// Production requires a PCI-DSS-compliant hosted/tokenized gateway.</summary>
public sealed record Payment(
    string PaymentId,
    string OrderId,
    PriceDetail Amount,
    string Method,              // e.g. "CARD"
    string CardToken,           // opaque gateway token — never a PAN
    string CardLast4,
    string AuthorizationCode,
    DateTime AuthorizedAtUtc);

/// <summary>A simulated accountable document (e-ticket or EMD).</summary>
public sealed record IssuedDocument(
    string DocumentNumber,      // e.g. "999-2401234567"
    string Kind,                // "ETKT" | "EMD"
    string PassengerId,
    string OrderId,
    DateTime IssuedAtUtc);

/// <summary>One purchased offer item inside an order.</summary>
public sealed record OrderItem(
    string OrderItemId,
    string SourceOfferItemId,
    Ptc PassengerType,
    IReadOnlyList<string> PassengerIds,
    IReadOnlyList<string> SegmentIds,
    FareComponent Fare,
    PriceDetail PricePerPassenger)
{
    public PriceDetail TotalPrice => new(
        PricePerPassenger.BaseAmount * PassengerIds.Count,
        PricePerPassenger.Taxes * PassengerIds.Count,
        PricePerPassenger.Currency);
}

/// <summary>
/// The airline Order: accepted offer(s) + passengers + services + payments.
/// Persisted in DocumentForge keyed by OrderId; mutations are ETag-guarded.
/// </summary>
public sealed record Order
{
    public required string OrderId { get; init; }
    public required string RecordLocator { get; init; }        // 6-char PNR-style locator
    public required string Owner { get; init; }                 // airline designator
    public required OrderStatus Status { get; init; }
    public required IReadOnlyList<FlightSegment> Segments { get; init; }
    public required IReadOnlyList<OrderItem> Items { get; init; }
    public required IReadOnlyList<Passenger> Passengers { get; init; }
    public IReadOnlyList<Ancillary> Ancillaries { get; init; } = [];
    public IReadOnlyList<SeatAssignment> Seats { get; init; } = [];
    public IReadOnlyList<string> PaymentIds { get; init; } = [];
    public IReadOnlyList<IssuedDocument> Documents { get; init; } = [];
    public required IReadOnlyList<TimeLimit> TimeLimits { get; init; }
    public required string Currency { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public AgentContext? CreatedBy { get; init; }

    public PriceDetail TotalPrice
    {
        get
        {
            var items = Items.Select(i => i.TotalPrice)
                .Concat(Ancillaries.Select(a => a.Price))
                .Concat(Seats.Select(s => s.Price));
            return PriceDetail.Sum(items, Currency);
        }
    }
}
