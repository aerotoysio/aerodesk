using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// DocumentForge-backed retailing service: offers/orders/passengers/payments
/// persisted as JSON documents on a dfdb serve node.
///
/// Phase 0 skeleton — connectivity and health are real; the retailing flows
/// arrive with their phases (shopping in Phase 1, ordering in Phase 2, …) and
/// throw <see cref="NotSupportedException"/> until then.
/// </summary>
public sealed class DocumentForgeRetailingService : IRetailingService
{
    public const string FlightsCollection = "flights";
    public const string OffersCollection = "offers";
    public const string OrdersCollection = "orders";
    public const string PassengersCollection = "passengers";
    public const string PaymentsCollection = "payments";
    public const string EticketsCollection = "etickets";

    private readonly DfHttpClient _client;

    public DocumentForgeRetailingService(DfConnectionDescriptor descriptor, string? apiKey, HttpMessageHandler? handler = null)
        => _client = new DfHttpClient(descriptor, apiKey, handler);

    public string Name => $"{_client.Descriptor.Name} ({_client.Descriptor.Url}, db '{_client.Descriptor.Database}')";
    public bool IsConnected => _client.IsConnected;

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);

    /// <summary>Server health, surfaced in the shell's status bar.</summary>
    public Task<(bool Healthy, string Status, string? Version)> GetHealthAsync(CancellationToken ct = default)
        => _client.GetHealthAsync(ct);

    // ---- Phase 1 ----
    public Task<IReadOnlyList<Offer>> SearchOffersAsync(ShopRequest request, CancellationToken ct = default)
        => throw NotYet("Shopping", 1);
    public Task<Offer?> RepriceOfferAsync(string offerId, CancellationToken ct = default)
        => throw NotYet("Repricing", 1);
    public Task SeedInventoryAsync(CancellationToken ct = default)
        => throw NotYet("Inventory bootstrap", 1);

    // ---- Phase 2 ----
    public Task<OrderEnvelope> CreateOrderAsync(string offerId, IReadOnlyList<Passenger> passengers, CancellationToken ct = default)
        => throw NotYet("Order creation", 2);
    public Task<OrderEnvelope?> GetOrderAsync(string orderIdOrLocator, CancellationToken ct = default)
        => throw NotYet("Order retrieval", 2);

    // ---- Phase 3 ----
    public Task<OrderEnvelope> PayOrderAsync(string orderId, PaymentToken payment, string expectedEtag, CancellationToken ct = default)
        => throw NotYet("Payment", 3);

    // ---- Phase 4 ----
    public Task<OrderEnvelope> ChangeOrderAsync(OrderChange change, string expectedEtag, CancellationToken ct = default)
        => throw NotYet("Order servicing", 4);
    public Task<OrderEnvelope> CancelOrderAsync(string orderId, string expectedEtag, CancellationToken ct = default)
        => throw NotYet("Order cancellation", 4);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private static NotSupportedException NotYet(string feature, int phase) =>
        new($"{feature} over DocumentForge arrives in Phase {phase}. Use the in-memory (offline demo) connection meanwhile.");
}
