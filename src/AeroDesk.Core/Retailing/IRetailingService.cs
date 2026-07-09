using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

/// <summary>Change requested against an existing order (Phase 4 servicing).</summary>
public sealed record OrderChange(
    string OrderId,
    IReadOnlyList<Ancillary> AddAncillaries,
    IReadOnlyList<string> RemoveServiceIds,
    IReadOnlyList<SeatAssignment> AddSeats);

/// <summary>An order handed back with the concurrency token guarding its next mutation.</summary>
public sealed record OrderEnvelope(Order Order, string Etag);

/// <summary>Tokenized payment input — token + last-4 only, never a PAN/CVV.</summary>
public sealed record PaymentToken(string Token, string CardLast4, string Method = "CARD");

/// <summary>What a retailing backend can do — the UI gates screens on these.</summary>
[Flags]
public enum RetailingCapabilities
{
    None = 0,
    SeatsAndExtras = 1,     // seat maps + ancillaries can be written to orders
    FlightChange = 2,       // rebooking (segment swap)
    MultiLeg = 4,           // return / multi-city itineraries in one order
    IssuesDocuments = 8,    // simulated e-tickets/EMDs on payment
    Seeding = 16,           // bootstrap demo inventory
    All = SeatsAndExtras | FlightChange | MultiLeg | IssuesDocuments | Seeding,
}

/// <summary>
/// The Offers &amp; Orders seam the app binds to — analogue of Studio's IDfConnection.
/// Implementations: DocumentForge-backed (HTTP), AeroBus-backed (the retailing
/// backbone), and in-memory (offline demo/tests).
/// Order mutations take the ETag from the envelope that produced the order and
/// throw <see cref="Connections.EtagConflictException"/> when it has gone stale.
/// </summary>
public interface IRetailingService : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    RetailingCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct = default);

    // ---- Shopping ----
    Task<IReadOnlyList<Offer>> SearchOffersAsync(ShopRequest request, CancellationToken ct = default);
    Task<Offer?> RepriceOfferAsync(string offerId, CancellationToken ct = default);

    // ---- Seats & extras ----
    Task<SeatMap> GetSeatMapAsync(FlightSegment segment, CancellationToken ct = default);
    Task<IReadOnlyList<AncillaryOption>> GetAncillaryCatalogAsync(FlightSegment segment, CancellationToken ct = default);

    // ---- Ordering ----
    Task<OrderEnvelope> CreateOrderAsync(string offerId, IReadOnlyList<Passenger> passengers, CancellationToken ct = default);
    Task<OrderEnvelope?> GetOrderAsync(string orderIdOrLocator, CancellationToken ct = default);
    Task<IReadOnlyList<OrderEnvelope>> ListOrdersAsync(int limit = 25, CancellationToken ct = default);

    // ---- Payment ----
    Task<OrderEnvelope> PayOrderAsync(string orderId, PaymentToken payment, string expectedEtag, CancellationToken ct = default);

    // ---- Servicing ----
    Task<OrderEnvelope> ChangeOrderAsync(OrderChange change, string expectedEtag, CancellationToken ct = default);
    Task<OrderEnvelope> CancelOrderAsync(string orderId, string expectedEtag, CancellationToken ct = default);

    /// <summary>Alternative flights for a rebooking: same O&amp;D and cabin on the new date.</summary>
    Task<IReadOnlyList<FlightSegment>> GetAlternativeFlightsAsync(FlightSegment segment, DateOnly newDate, CancellationToken ct = default);

    /// <summary>Swap one flight for another. Fare rules apply: non-changeable fares
    /// are rejected, change fees land as an order-level CHG service line.</summary>
    Task<OrderEnvelope> ChangeFlightAsync(string orderId, string oldSegmentId, FlightSegment newSegment, string expectedEtag, CancellationToken ct = default);

    // ---- Bootstrap ----
    /// <summary>Create collections + sample flight inventory so a fresh node demos end to end.</summary>
    Task SeedInventoryAsync(CancellationToken ct = default);
}
