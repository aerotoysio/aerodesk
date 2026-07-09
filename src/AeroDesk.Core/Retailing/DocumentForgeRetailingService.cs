using System.Text.Json;
using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Settings;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// DocumentForge-backed retailing service: flights inventory, offers, orders,
/// payments and e-tickets are JSON documents on a dfdb serve node.
///
/// Concurrency: the <see cref="OrderEnvelope"/> ETag is DocumentForge's
/// server-stamped <c>_etag</c>; every mutation re-reads the current document,
/// verifies the caller's ETag, and writes with <c>If-Match</c> so a concurrent
/// change surfaces as <see cref="EtagConflictException"/> (locally or as a 412).
/// </summary>
public sealed class DocumentForgeRetailingService : IRetailingService
{
    public const string FlightsCollection = "flights";
    public const string OffersCollection = "offers";
    public const string OrdersCollection = "orders";
    public const string PaymentsCollection = "payments";
    public const string EticketsCollection = "etickets";

    private static readonly JsonSerializerOptions Wire = JsonDefaults.Wire;

    private readonly DfHttpClient _client;
    private readonly IPaymentGateway _gateway;
    private readonly Func<DateTime> _clock;

    public DocumentForgeRetailingService(DfConnectionDescriptor descriptor, string? apiKey,
        IPaymentGateway? gateway = null, HttpMessageHandler? handler = null, Func<DateTime>? clock = null)
    {
        _client = new DfHttpClient(descriptor, apiKey, handler);
        _gateway = gateway ?? new MockPaymentGateway();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string Name => $"{_client.Descriptor.Name} ({_client.Descriptor.Url}, db '{_client.Descriptor.Database}')";
    public bool IsConnected => _client.IsConnected;
    public RetailingCapabilities Capabilities => RetailingCapabilities.All;

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);

    /// <summary>Server health, surfaced in the shell's status bar.</summary>
    public Task<(bool Healthy, string Status, string? Version)> GetHealthAsync(CancellationToken ct = default)
        => _client.GetHealthAsync(ct);

    // ---- Bootstrap ----

    /// <summary>Create the database and load the demo schedule into <c>flights</c>
    /// (idempotent — an already-seeded inventory is left alone).</summary>
    public async Task SeedInventoryAsync(CancellationToken ct = default)
    {
        await _client.CreateDatabaseAsync(_client.Descriptor.Database, ct).ConfigureAwait(false);

        // Querying a collection that doesn't exist yet is a 400 on DocumentForge,
        // so probe the collection list instead of SELECTing.
        var collections = await _client.GetCollectionNamesAsync(ct).ConfigureAwait(false);
        if (collections.Contains(FlightsCollection, StringComparer.OrdinalIgnoreCase)) return;

        foreach (var route in FlightSchedule.Routes)
        {
            ct.ThrowIfCancellationRequested();
            await _client.InsertDocumentAsync(FlightsCollection,
                JsonSerializer.Serialize(route, Wire), ct).ConfigureAwait(false);
        }
    }

    // ---- Shopping ----

    public async Task<IReadOnlyList<Offer>> SearchOffersAsync(ShopRequest request, CancellationToken ct = default)
    {
        if (request.Legs.Count == 0) throw new ArgumentException("At least one journey leg is required.", nameof(request));
        if (request.SeatedPassengers == 0) throw new ArgumentException("At least one seated passenger (ADT/CHD) is required.", nameof(request));
        if (request.Infants > request.Adults) throw new ArgumentException("Each infant must travel with an adult.", nameof(request));

        // Flight availability per leg from the flights inventory (route schedule docs).
        var perLeg = new List<IReadOnlyList<FlightSegment>>();
        foreach (var leg in request.Legs)
        {
            DfQueryResult result;
            try
            {
                result = await _client.ExecuteAsync(
                    $"SELECT * FROM {FlightsCollection} WHERE origin = '{Sql(leg.Origin.ToUpperInvariant())}' " +
                    $"AND destination = '{Sql(leg.Destination.ToUpperInvariant())}'", ct).ConfigureAwait(false);
            }
            catch (DfHttpException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Database '{_client.Descriptor.Database}' has no flight inventory yet — " +
                    "right-click the connection and choose 'Seed demo data' first.");
            }
            perLeg.Add(result.Documents
                .Select(json => JsonSerializer.Deserialize<FlightSchedule.Route>(json, Wire)!)
                .Select(route => FlightSchedule.BuildSegment(route, leg.DepartureDate, request.Cabin))
                .ToList());
        }

        var offers = OfferFactory.Build(request, perLeg, _clock(), () => $"OF-{Guid.NewGuid():N}");

        // Persist so reprice/order-create can find them later (offer store).
        foreach (var offer in offers)
            await _client.InsertDocumentAsync(OffersCollection,
                JsonSerializer.Serialize(offer, Wire), ct).ConfigureAwait(false);

        return offers;
    }

    public async Task<Offer?> RepriceOfferAsync(string offerId, CancellationToken ct = default)
    {
        var found = await FindOneAsync(OffersCollection, "offerId", offerId, ct).ConfigureAwait(false);
        if (found is null) return null;
        var (docId, etag, json) = found.Value;

        var offer = JsonSerializer.Deserialize<Offer>(json, Wire)!;
        var repriced = offer with { OfferExpiry = new TimeLimit(TimeLimit.OfferExpiry, _clock().AddMinutes(30)) };
        await _client.UpdateDocumentAsync(OffersCollection, docId,
            JsonSerializer.Serialize(repriced, Wire), etag, ct).ConfigureAwait(false);
        return repriced;
    }

    // ---- Seats & extras (deterministic; no server round-trip needed) ----

    public Task<SeatMap> GetSeatMapAsync(FlightSegment segment, CancellationToken ct = default) =>
        Task.FromResult(SeatMapFactory.Build(segment));

    public Task<IReadOnlyList<AncillaryOption>> GetAncillaryCatalogAsync(FlightSegment segment, CancellationToken ct = default) =>
        Task.FromResult(SeatMapFactory.Catalog(segment));

    // ---- Ordering ----

    public async Task<OrderEnvelope> CreateOrderAsync(string offerId, IReadOnlyList<Passenger> passengers, CancellationToken ct = default)
    {
        var found = await FindOneAsync(OffersCollection, "offerId", offerId, ct).ConfigureAwait(false);
        if (found is null) throw new KeyNotFoundException($"Offer '{offerId}' not found — search again.");

        var offer = JsonSerializer.Deserialize<Offer>(found.Value.Json, Wire)!;
        if (offer.OfferExpiry.IsExpired(_clock()))
            throw new InvalidOperationException("The offer has expired — reprice before ordering.");

        var now = _clock();
        var order = OrderFactory.Create(offer, passengers, OrderFactory.NewOrderId(now), now);

        var docId = await _client.InsertDocumentAsync(OrdersCollection,
            JsonSerializer.Serialize(order, Wire), ct).ConfigureAwait(false);
        var etag = await ReadEtagAsync(OrdersCollection, docId, ct).ConfigureAwait(false);
        return new OrderEnvelope(order, etag);
    }

    public async Task<OrderEnvelope?> GetOrderAsync(string orderIdOrLocator, CancellationToken ct = default)
    {
        var doc = await FindOrderDocAsync(orderIdOrLocator, ct).ConfigureAwait(false);
        return doc is null ? null : new OrderEnvelope(doc.Order, doc.Etag);
    }

    public async Task<IReadOnlyList<OrderEnvelope>> ListOrdersAsync(int limit = 25, CancellationToken ct = default)
    {
        var result = await _client.ExecuteAsync($"SELECT * FROM {OrdersCollection} LIMIT 500", ct).ConfigureAwait(false);
        return result.Documents
            .Select(ParseOrderDoc)
            .OrderByDescending(d => d.Order.CreatedAtUtc)
            .Take(limit)
            .Select(d => new OrderEnvelope(d.Order, d.Etag))
            .ToList();
    }

    // ---- Payment ----

    public async Task<OrderEnvelope> PayOrderAsync(string orderId, PaymentToken payment, string expectedEtag, CancellationToken ct = default)
    {
        var doc = await TakeGuardedAsync(orderId, expectedEtag, ct).ConfigureAwait(false);
        if (doc.Order.Status != OrderStatus.PendingPayment)
            throw new InvalidOperationException($"Order {orderId} is {doc.Order.Status}, not awaiting payment.");

        var amount = doc.Order.TotalPrice;
        var auth = await _gateway.AuthorizeAsync(payment, amount, ct).ConfigureAwait(false);
        if (!auth.Approved)
            throw new InvalidOperationException($"Payment declined: {auth.DeclineReason ?? "unknown reason"}.");

        var now = _clock();
        var pay = new Payment(
            PaymentId: Guid.NewGuid().ToString("N"),
            OrderId: orderId,
            Amount: amount,
            Method: payment.Method,
            CardToken: payment.Token,
            CardLast4: payment.CardLast4,
            AuthorizationCode: auth.AuthorizationCode,
            AuthorizedAtUtc: now);
        await _client.InsertDocumentAsync(PaymentsCollection,
            JsonSerializer.Serialize(pay, Wire), ct).ConfigureAwait(false);

        var documents = OrderFactory.IssueDocuments(doc.Order, now);
        foreach (var issued in documents)
            await _client.InsertDocumentAsync(EticketsCollection,
                JsonSerializer.Serialize(issued, Wire), ct).ConfigureAwait(false);

        var paid = doc.Order with
        {
            Status = OrderStatus.Ticketed,
            PaymentIds = [.. doc.Order.PaymentIds, pay.PaymentId],
            Documents = documents,
            TimeLimits = doc.Order.TimeLimits.Where(t => t.Kind != TimeLimit.PaymentDeadline).ToList(),
        };
        return await WriteBackAsync(doc.DocId, paid, doc.Etag, ct).ConfigureAwait(false);
    }

    // ---- Servicing ----

    public async Task<OrderEnvelope> ChangeOrderAsync(OrderChange change, string expectedEtag, CancellationToken ct = default)
    {
        var doc = await TakeGuardedAsync(change.OrderId, expectedEtag, ct).ConfigureAwait(false);
        var changed = OrderFactory.ApplyChange(doc.Order, change);
        return await WriteBackAsync(doc.DocId, changed, doc.Etag, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FlightSegment>> GetAlternativeFlightsAsync(FlightSegment segment, DateOnly newDate, CancellationToken ct = default)
    {
        var result = await _client.ExecuteAsync(
            $"SELECT * FROM {FlightsCollection} WHERE origin = '{Sql(segment.Origin)}' " +
            $"AND destination = '{Sql(segment.Destination)}'", ct).ConfigureAwait(false);
        return result.Documents
            .Select(json => JsonSerializer.Deserialize<FlightSchedule.Route>(json, Wire)!)
            .Select(route => FlightSchedule.BuildSegment(route, newDate, segment.Cabin))
            .Where(f => f.SegmentId != segment.SegmentId)
            .ToList();
    }

    public async Task<OrderEnvelope> ChangeFlightAsync(string orderId, string oldSegmentId, FlightSegment newSegment, string expectedEtag, CancellationToken ct = default)
    {
        var doc = await TakeGuardedAsync(orderId, expectedEtag, ct).ConfigureAwait(false);
        var fee = OrderFactory.GetFlightChangeFee(doc.Order);
        var changed = OrderFactory.ApplyFlightChange(doc.Order, oldSegmentId, newSegment, fee);
        return await WriteBackAsync(doc.DocId, changed, doc.Etag, ct).ConfigureAwait(false);
    }

    public async Task<OrderEnvelope> CancelOrderAsync(string orderId, string expectedEtag, CancellationToken ct = default)
    {
        var doc = await TakeGuardedAsync(orderId, expectedEtag, ct).ConfigureAwait(false);
        if (doc.Order.Status == OrderStatus.Cancelled)
            throw new InvalidOperationException($"Order {orderId} is already cancelled.");
        return await WriteBackAsync(doc.DocId, doc.Order with { Status = OrderStatus.Cancelled }, doc.Etag, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    // ---- document plumbing ----

    private sealed record OrderDoc(string DocId, string Etag, Order Order);

    /// <summary>Load the order and verify the caller's ETag is still the stored one.</summary>
    private async Task<OrderDoc> TakeGuardedAsync(string orderId, string expectedEtag, CancellationToken ct)
    {
        var doc = await FindOrderDocAsync(orderId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Order '{orderId}' not found.");
        if (!string.Equals(doc.Etag, expectedEtag, StringComparison.Ordinal))
            throw new EtagConflictException(expectedEtag, doc.Etag,
                "The order was modified concurrently — reload it and retry.");
        return doc;
    }

    /// <summary>Replace the order document with If-Match and hand back the fresh envelope.</summary>
    private async Task<OrderEnvelope> WriteBackAsync(string docId, Order order, string etag, CancellationToken ct)
    {
        var newEtag = await _client.UpdateDocumentAsync(OrdersCollection, docId,
            JsonSerializer.Serialize(order, Wire), etag, ct).ConfigureAwait(false);
        return new OrderEnvelope(order, newEtag);
    }

    private async Task<OrderDoc?> FindOrderDocAsync(string orderIdOrLocator, CancellationToken ct)
    {
        var byId = await FindOneAsync(OrdersCollection, "orderId", orderIdOrLocator, ct).ConfigureAwait(false);
        var found = byId ?? await FindOneAsync(OrdersCollection, "recordLocator", orderIdOrLocator.ToUpperInvariant(), ct).ConfigureAwait(false);
        if (found is null) return null;
        var parsed = ParseOrderDoc(found.Value.Json);
        return new OrderDoc(found.Value.DocId, found.Value.Etag, parsed.Order);
    }

    private static (string DocId, string Etag, Order Order) ParseOrderDoc(string json)
    {
        var (docId, etag) = ReadMeta(json);
        return (docId, etag, JsonSerializer.Deserialize<Order>(json, Wire)!);
    }

    /// <summary>Single-document lookup by a business key field. Returns DF's _id/_etag + raw JSON.</summary>
    private async Task<(string DocId, string Etag, string Json)?> FindOneAsync(string collection, string field, string value, CancellationToken ct)
    {
        var result = await _client.ExecuteAsync(
            $"SELECT * FROM {collection} WHERE {field} = '{Sql(value)}' LIMIT 1", ct).ConfigureAwait(false);
        if (result.Documents.Count == 0) return null;
        var json = result.Documents[0];
        var (docId, etag) = ReadMeta(json);
        return (docId, etag, json);
    }

    private async Task<string> ReadEtagAsync(string collection, string docId, CancellationToken ct)
    {
        var json = await _client.GetDocumentAsync(collection, docId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Document '{docId}' vanished from '{collection}'.");
        return ReadMeta(json).Etag;
    }

    /// <summary>DocumentForge stamps <c>_id</c> and <c>_etag</c> into every stored document.</summary>
    private static (string DocId, string Etag) ReadMeta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.TryGetProperty("_id", out var id) ? id.GetString() ?? "" : "",
            root.TryGetProperty("_etag", out var etag) ? etag.GetString() ?? "" : "");
    }

    /// <summary>Escape a value for a single-quoted SQL literal.</summary>
    private static string Sql(string value) => value.Replace("'", "''");
}
