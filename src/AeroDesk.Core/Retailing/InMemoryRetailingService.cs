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

            ValidatePassengersMatchOffer(offer, passengers);

            var now = _clock();
            var order = new Order
            {
                OrderId = $"AT-{now:yyyyMMdd}-{Interlocked.Increment(ref _orderSequence):D5}",
                RecordLocator = NewRecordLocator(),
                Owner = offer.Owner,
                Status = OrderStatus.PendingPayment,
                Segments = offer.Segments,
                Items = offer.Items.Select(i => new OrderItem(
                    OrderItemId: Guid.NewGuid().ToString("N"),
                    SourceOfferItemId: i.OfferItemId,
                    PassengerType: i.PassengerType,
                    PassengerIds: passengers.Where(p => p.Type == i.PassengerType).Select(p => p.PassengerId).ToList(),
                    SegmentIds: i.SegmentIds,
                    Fare: i.Fare,
                    PricePerPassenger: i.PricePerPassenger)).ToList(),
                Passengers = passengers,
                TimeLimits = [new TimeLimit(TimeLimit.PaymentDeadline, now.AddHours(24)),
                              new TimeLimit(TimeLimit.PriceGuarantee, now.AddHours(24))],
                Currency = offer.Currency,
                CreatedAtUtc = now,
            };

            var etag = NewEtag();
            _orders[order.OrderId] = (order, etag);
            return Task.FromResult(new OrderEnvelope(order, etag));
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

            var documents = IssueDocuments(order, now);
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
            if (order.Status == OrderStatus.Cancelled)
                throw new InvalidOperationException($"Order {change.OrderId} is cancelled.");

            var ancillaries = order.Ancillaries
                .Where(a => !change.RemoveServiceIds.Contains(a.ServiceId))
                .Concat(change.AddAncillaries)
                .ToList();
            var seats = order.Seats
                .Where(s => !change.AddSeats.Any(n => n.SegmentId == s.SegmentId && n.PassengerId == s.PassengerId))
                .Concat(change.AddSeats)
                .ToList();

            return Task.FromResult(Store(order with { Ancillaries = ancillaries, Seats = seats }));
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

    private static void ValidatePassengersMatchOffer(Offer offer, IReadOnlyList<Passenger> passengers)
    {
        foreach (var item in offer.Items)
        {
            var supplied = passengers.Count(p => p.Type == item.PassengerType);
            if (supplied != item.PassengerCount)
                throw new ArgumentException(
                    $"Offer expects {item.PassengerCount} x {item.PassengerType}, got {supplied}.");
        }
        foreach (var p in passengers)
        {
            if (string.IsNullOrWhiteSpace(p.GivenName) || string.IsNullOrWhiteSpace(p.Surname))
                throw new ArgumentException($"Passenger '{p.PassengerId}' needs a given name and surname.");
        }
    }

    private List<IssuedDocument> IssueDocuments(Order order, DateTime now)
    {
        // Simulated accountable documents: one e-ticket per seated passenger.
        var docs = new List<IssuedDocument>();
        var serial = Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L);
        foreach (var pax in order.Passengers)
        {
            docs.Add(new IssuedDocument(
                DocumentNumber: $"999-{serial++}",
                Kind: pax.Type == Ptc.INF ? "EMD" : "ETKT",
                PassengerId: pax.PassengerId,
                OrderId: order.OrderId,
                IssuedAtUtc: now));
        }
        return docs;
    }

    private static string NewEtag() => Guid.NewGuid().ToString("N");

    private static string NewRecordLocator()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // no I/O to avoid 1/0 confusion
        return string.Create(6, alphabet, static (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[Random.Shared.Next(a.Length)];
        });
    }
}
