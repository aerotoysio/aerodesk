using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// Self-contained retailing service: offers and orders live in dictionaries,
/// flights come from the deterministic <see cref="FlightSchedule"/>. Used for
/// the offline demo and as the behavioural reference for unit tests. Mimics
/// DocumentForge's optimistic concurrency: every mutation rotates the order's
/// ETag and a stale one throws <see cref="EtagConflictException"/>.
/// </summary>
public sealed class InMemoryRetailingService : IRetailingService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Offer> _offers = [];
    private readonly Dictionary<string, (Order Order, string Etag)> _orders = [];
    private readonly Dictionary<string, Payment> _payments = [];
    private readonly IPaymentGateway _gateway;
    private readonly Func<DateTime> _clock;
    private int _orderSequence;

    public InMemoryRetailingService(IPaymentGateway? gateway = null, Func<DateTime>? clock = null)
    {
        _gateway = gateway ?? new MockPaymentGateway();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string Name => "In-memory (offline demo)";
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SeedInventoryAsync(CancellationToken ct = default) => Task.CompletedTask; // schedule is built in

    // ---- Shopping ----

    public Task<IReadOnlyList<Offer>> SearchOffersAsync(ShopRequest request, CancellationToken ct = default)
    {
        if (request.Legs.Count == 0) throw new ArgumentException("At least one journey leg is required.", nameof(request));
        if (request.SeatedPassengers == 0) throw new ArgumentException("At least one seated passenger (ADT/CHD) is required.", nameof(request));
        if (request.Infants > request.Adults) throw new ArgumentException("Each infant must travel with an adult.", nameof(request));

        var offers = OfferFactory.Build(request, _clock(), () => $"OF-{Guid.NewGuid():N}");
        lock (_gate)
        {
            foreach (var offer in offers) _offers[offer.OfferId] = offer;
        }
        return Task.FromResult(offers);
    }

    public Task<Offer?> RepriceOfferAsync(string offerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_offers.TryGetValue(offerId, out var offer)) return Task.FromResult<Offer?>(null);
            // Same price while the offer is alive; a reprice extends the guarantee window.
            var repriced = offer with { OfferExpiry = new TimeLimit(TimeLimit.OfferExpiry, _clock().AddMinutes(30)) };
            _offers[offerId] = repriced;
            return Task.FromResult<Offer?>(repriced);
        }
    }

    // ---- Ordering ----

    public Task<OrderEnvelope> CreateOrderAsync(string offerId, IReadOnlyList<Passenger> passengers, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_offers.TryGetValue(offerId, out var offer))
                throw new KeyNotFoundException($"Offer '{offerId}' not found — search again.");
            if (offer.OfferExpiry.IsExpired(_clock()))
                throw new InvalidOperationException("The offer has expired — reprice before ordering.");

            var now = _clock();
            var order = OrderFactory.Create(offer, passengers,
                $"AT-{now:yyyyMMdd}-{Interlocked.Increment(ref _orderSequence):D5}", now);

            var etag = NewEtag();
            _orders[order.OrderId] = (order, etag);
            return Task.FromResult(new OrderEnvelope(order, etag));
        }
    }

    // ---- Seats & extras ----

    public Task<SeatMap> GetSeatMapAsync(FlightSegment segment, CancellationToken ct = default) =>
        Task.FromResult(SeatMapFactory.Build(segment));

    public Task<IReadOnlyList<AncillaryOption>> GetAncillaryCatalogAsync(FlightSegment segment, CancellationToken ct = default) =>
        Task.FromResult(SeatMapFactory.Catalog(segment));

    public Task<IReadOnlyList<OrderEnvelope>> ListOrdersAsync(int limit = 25, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<OrderEnvelope> result = _orders.Values
                .OrderByDescending(o => o.Order.CreatedAtUtc)
                .Take(limit)
                .Select(o => new OrderEnvelope(o.Order, o.Etag))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<OrderEnvelope?> GetOrderAsync(string orderIdOrLocator, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_orders.TryGetValue(orderIdOrLocator, out var byId))
                return Task.FromResult<OrderEnvelope?>(new OrderEnvelope(byId.Order, byId.Etag));

            var byLocator = _orders.Values.FirstOrDefault(o =>
                o.Order.RecordLocator.Equals(orderIdOrLocator, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(byLocator.Order is null ? null : new OrderEnvelope(byLocator.Order, byLocator.Etag));
        }
    }

    // ---- Payment ----

    public async Task<OrderEnvelope> PayOrderAsync(string orderId, PaymentToken payment, string expectedEtag, CancellationToken ct = default)
    {
        Order order;
        lock (_gate)
        {
            order = TakeGuarded(orderId, expectedEtag);
            if (order.Status != OrderStatus.PendingPayment)
                throw new InvalidOperationException($"Order {orderId} is {order.Status}, not awaiting payment.");
        }

        var amount = order.TotalPrice;
        var auth = await _gateway.AuthorizeAsync(payment, amount, ct).ConfigureAwait(false);
        if (!auth.Approved)
            throw new InvalidOperationException($"Payment declined: {auth.DeclineReason ?? "unknown reason"}.");

        lock (_gate)
        {
            // Re-check under the lock: the gateway call ran outside it.
            order = TakeGuarded(orderId, expectedEtag);
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
            _payments[pay.PaymentId] = pay;

            var documents = OrderFactory.IssueDocuments(order, now);
            var paid = order with
            {
                Status = OrderStatus.Ticketed,
                PaymentIds = [.. order.PaymentIds, pay.PaymentId],
                Documents = documents,
                TimeLimits = order.TimeLimits.Where(t => t.Kind != TimeLimit.PaymentDeadline).ToList(),
            };
            return Store(paid);
        }
    }

    // ---- Servicing ----

    public Task<OrderEnvelope> ChangeOrderAsync(OrderChange change, string expectedEtag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var order = TakeGuarded(change.OrderId, expectedEtag);
            return Task.FromResult(Store(OrderFactory.ApplyChange(order, change)));
        }
    }

    public Task<IReadOnlyList<FlightSegment>> GetAlternativeFlightsAsync(FlightSegment segment, DateOnly newDate, CancellationToken ct = default)
    {
        IReadOnlyList<FlightSegment> flights = FlightSchedule
            .FlightsFor(segment.Origin, segment.Destination, newDate, segment.Cabin)
            .Where(f => f.SegmentId != segment.SegmentId)
            .ToList();
        return Task.FromResult(flights);
    }

    public Task<OrderEnvelope> ChangeFlightAsync(string orderId, string oldSegmentId, FlightSegment newSegment, string expectedEtag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var order = TakeGuarded(orderId, expectedEtag);
            var fee = OrderFactory.GetFlightChangeFee(order);
            return Task.FromResult(Store(OrderFactory.ApplyFlightChange(order, oldSegmentId, newSegment, fee)));
        }
    }

    public Task<OrderEnvelope> CancelOrderAsync(string orderId, string expectedEtag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var order = TakeGuarded(orderId, expectedEtag);
            if (order.Status == OrderStatus.Cancelled) throw new InvalidOperationException($"Order {orderId} is already cancelled.");
            return Task.FromResult(Store(order with { Status = OrderStatus.Cancelled }));
        }
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    // ---- helpers ----

    /// <summary>Load an order and verify the caller's ETag is still current.</summary>
    private Order TakeGuarded(string orderId, string expectedEtag)
    {
        if (!_orders.TryGetValue(orderId, out var entry))
            throw new KeyNotFoundException($"Order '{orderId}' not found.");
        if (!string.Equals(entry.Etag, expectedEtag, StringComparison.Ordinal))
            throw new EtagConflictException(expectedEtag, entry.Etag,
                "The order was modified concurrently — reload it and retry.");
        return entry.Order;
    }

    private OrderEnvelope Store(Order order)
    {
        var etag = NewEtag();
        _orders[order.OrderId] = (order, etag);
        return new OrderEnvelope(order, etag);
    }

    private static string NewEtag() => Guid.NewGuid().ToString("N");
}
