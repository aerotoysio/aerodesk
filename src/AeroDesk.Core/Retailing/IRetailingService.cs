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

/// <summary>
/// The Offers &amp; Orders seam the app binds to — analogue of Studio's IDfConnection.
/// Implementations: DocumentForge-backed (HTTP) and in-memory (offline demo/tests).
/// Order mutations take the ETag from the envelope that produced the order and
/// throw <see cref="Connections.EtagConflictException"/> when it has gone stale.
/// </summary>
public interface IRetailingService : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }

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

    // ---- Bootstrap ----
    /// <summary>Create collections + sample flight inventory so a fresh node demos end to end.</summary>
    Task SeedInventoryAsync(CancellationToken ct = default);
}
