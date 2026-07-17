using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Settings;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// Retailing over the AeroBus backbone (github.com/aerotoysio/aerobus):
/// `/offer/shop` + `/order/create|retrieve|change`. Auth is the shared
/// <see cref="KeycloakAuthClient"/> agent session — the SAME login that drives
/// departure control — with a fresh bearer attached per request (refresh comes
/// free from the auth client). The owner (the connection) signs in once;
/// this service never authenticates on its own.
///
/// Mapping notes (AeroBus semantics differ from the in-house model):
/// - AeroBus books at create time (orders are born Confirmed, payment attached
///   to the create). AeroDesk's hold-then-pay flow therefore uses a DEFERRED
///   create: <see cref="CreateOrderAsync"/> stages a local draft, and
///   <see cref="PayOrderAsync"/> executes `/order/create` (with payment) then
///   `Fulfil` — landing the order Ticketed in one payment step.
/// - Fare families are AeroBus bundles: LITE / FLEX / FLEXPLUS.
/// - The envelope ETag is the order's ConcurrencyId; staleness is detected by
///   re-retrieving and comparing (same UX as the DocumentForge backend).
/// - Capabilities: one O&amp;D per order, no seat/extras writes, no rebooking —
///   the UI hides those affordances on AeroBus connections.
/// </summary>
public sealed class AeroBusRetailingService : IRetailingService
{
    private static readonly JsonSerializerOptions Wire = JsonDefaults.Wire;

    private readonly HttpClient _http;
    private readonly DfConnectionDescriptor _descriptor;
    private readonly KeycloakAuthClient _auth;

    // Deferred-create drafts + orders seen this session (AeroBus has no list endpoint).
    private readonly object _gate = new();
    private readonly Dictionary<string, DraftOrder> _drafts = [];
    private readonly Dictionary<string, OrderEnvelope> _sessionOrders = [];
    private readonly Dictionary<string, ShoppedOffer> _shoppedOffers = [];

    private sealed record ShoppedOffer(Guid AeroBusOfferId, Guid SolutionId, Guid BundleId, Offer Offer);
    private sealed record DraftOrder(ShoppedOffer Source, IReadOnlyList<Passenger> Passengers, Order Draft);

    public AeroBusRetailingService(DfConnectionDescriptor descriptor, KeycloakAuthClient auth, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Url))
            throw new ArgumentException("Descriptor must have a Url.", nameof(descriptor));
        _descriptor = descriptor;
        _auth = auth;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(descriptor.Url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public string Name => $"{_descriptor.Name} (AeroBus, {_descriptor.Url})";
    public bool IsConnected { get; private set; }
    public RetailingCapabilities Capabilities => RetailingCapabilities.None; // shop/book/retrieve/pay/cancel only

    public string Currency { get; private set; } = "AED";

    // ---- Connect: reachability only — the shared Keycloak session is the login ----

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        using var health = await _http.GetAsync("health", ct).ConfigureAwait(false);
        if (!health.IsSuccessStatusCode)
            throw new DfHttpException(health.StatusCode, $"AeroBus health check failed ({(int)health.StatusCode}).");
        // Prove the session works end-to-end (and fail fast on a bad token).
        await AttachTokenAsync(ct).ConfigureAwait(false);
        IsConnected = true;
    }

    /// <summary>Put a current access token on the client before an API call. The
    /// auth client refreshes as needed, so long shifts don't 401 mid-sale.</summary>
    private async Task AttachTokenAsync(CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    // ---- Shopping ----

    public async Task<IReadOnlyList<Offer>> SearchOffersAsync(ShopRequest request, CancellationToken ct = default)
    {
        if (request.Legs.Count != 1)
            throw new NotSupportedException(
                "The AeroBus backend prices one origin & destination per order — search one-way (book each direction separately).");
        if (request.SeatedPassengers == 0) throw new ArgumentException("At least one seated passenger (ADT/CHD) is required.", nameof(request));
        if (request.Infants > request.Adults) throw new ArgumentException("Each infant must travel with an adult.", nameof(request));

        var leg = request.Legs[0];
        var paxList = new List<object>();
        void AddPax(Ptc type, int count, int age)
        {
            for (var i = 0; i < count; i++)
                paxList.Add(new { id = Guid.NewGuid(), name = $"{type} {i + 1}", type = type.ToString(), age });
        }
        AddPax(Ptc.ADT, request.Adults, 30);
        AddPax(Ptc.CHD, request.Children, 8);
        AddPax(Ptc.INF, request.Infants, 1);

        var payload = new
        {
            searchContext = new { channel = "desk", pointOfSale = "AU", currency = Currency, locale = "en" },
            passengers = paxList,
            searchCriteria = new
            {
                tripType = "ONE_WAY",
                originDestinations = new[]
                {
                    new
                    {
                        odRef = "OD1",
                        origin = leg.Origin.ToUpperInvariant(),
                        destination = leg.Destination.ToUpperInvariant(),
                        departureDate = leg.DepartureDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    },
                },
                cabinPreferences = new[] { CabinCode(request.Cabin) },
                maxConnections = 1,
                maxResultsPerOD = 20,
            },
        };

        await AttachTokenAsync(ct).ConfigureAwait(false);
        using var response = await _http.PostAsJsonAsync("offer/shop", payload, Wire, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, $"offer/shop failed: {Snip(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var aeroBusOfferId = root.GetProperty("offerId").GetGuid();

        var offers = new List<Offer>();
        if (root.TryGetProperty("originDestinations", out var ods) && ods.ValueKind == JsonValueKind.Array)
        {
            foreach (var od in ods.EnumerateArray())
            {
                if (!od.TryGetProperty("flightSolutions", out var solutions) || solutions.ValueKind != JsonValueKind.Array) continue;
                foreach (var solution in solutions.EnumerateArray())
                {
                    var segments = MapSegments(solution, request.Cabin);
                    if (segments.Count == 0 || !solution.TryGetProperty("bundles", out var bundles)) continue;
                    var solutionId = solution.GetProperty("id").GetGuid();

                    foreach (var bundle in bundles.EnumerateArray())
                    {
                        var offer = MapBundleToOffer(aeroBusOfferId, solutionId, bundle, segments, request);
                        if (offer is null) continue;
                        lock (_gate)
                        {
                            _shoppedOffers[offer.Offer.OfferId] = offer;
                        }
                        offers.Add(offer.Offer);
                    }
                }
            }
        }

        if (offers.Count == 0 && root.TryGetProperty("warnings", out var warnings) &&
            warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() > 0)
            throw new InvalidOperationException(
                "AeroBus returned flights but no priced bundles: " +
                string.Join("; ", warnings.EnumerateArray().Select(w => w.GetString())));

        return offers;
    }

    public Task<Offer?> RepriceOfferAsync(string offerId, CancellationToken ct = default)
    {
        // AeroBus offers hold server-side; the shopped snapshot is the price.
        lock (_gate)
        {
            return Task.FromResult(_shoppedOffers.TryGetValue(offerId, out var shopped) ? shopped.Offer : null);
        }
    }

    // ---- Seats & extras: read-only seat maps aren't wired yet (capability off) ----

    public Task<SeatMap> GetSeatMapAsync(FlightSegment segment, CancellationToken ct = default) =>
        throw new NotSupportedException("Seat selection is not available on the AeroBus backend yet.");

    public Task<IReadOnlyList<AncillaryOption>> GetAncillaryCatalogAsync(FlightSegment segment, CancellationToken ct = default) =>
        throw new NotSupportedException("Ancillaries are not available on the AeroBus backend yet.");

    // ---- Ordering: deferred create (AeroBus books at payment time) ----

    public Task<OrderEnvelope> CreateOrderAsync(string offerId, IReadOnlyList<Passenger> passengers, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_shoppedOffers.TryGetValue(offerId, out var shopped))
                throw new KeyNotFoundException($"Offer '{offerId}' not found — search again.");

            OrderFactory.ValidatePassengersMatchOffer(shopped.Offer, passengers);
            var now = DateTime.UtcNow;
            var draft = OrderFactory.Create(shopped.Offer, passengers, $"DRAFT-{Guid.NewGuid():N}"[..14], now) with
            {
                TimeLimits = [new TimeLimit(TimeLimit.OfferExpiry, shopped.Offer.OfferExpiry.ExpiresAtUtc)],
            };
            _drafts[draft.OrderId] = new DraftOrder(shopped, passengers, draft);
            var envelope = new OrderEnvelope(draft, "draft");
            _sessionOrders[draft.OrderId] = envelope;
            return Task.FromResult(envelope);
        }
    }

    public async Task<OrderEnvelope> PayOrderAsync(string orderId, PaymentToken payment, string expectedEtag, CancellationToken ct = default)
    {
        DraftOrder? draft;
        lock (_gate) { _drafts.TryGetValue(orderId, out draft); }
        if (draft is null)
            throw new InvalidOperationException($"Order {orderId} is not awaiting payment.");

        var payload = new
        {
            channel = "desk",
            offerId = draft.Source.AeroBusOfferId,
            solutionId = draft.Source.SolutionId,
            bundleId = draft.Source.BundleId,
            passengers = draft.Passengers.Select(p => new
            {
                paxType = p.Type.ToString(),
                title = p.Title,
                firstName = p.GivenName,
                lastName = p.Surname,
                birthDate = p.DateOfBirth?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            }),
            payment = new
            {
                provider = "Manual",
                method = "Card",
                currency = draft.Draft.Currency,
                amount = draft.Draft.TotalPrice.Total,
            },
        };

        await AttachTokenAsync(ct).ConfigureAwait(false);
        using var response = await _http.PostAsJsonAsync("order/create", payload, Wire, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException($"AeroBus rejected the booking: {Snip(body)}");
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, $"order/create failed: {Snip(body)}");

        using var doc = JsonDocument.Parse(body);
        var orderEl = doc.RootElement.GetProperty("order");
        var orderGuid = orderEl.GetProperty("id").GetGuid();
        var publicId = orderEl.GetProperty("orderId").GetString() ?? orderGuid.ToString();

        // Confirmed (booked + payment authorized) → Fulfil (ticketed).
        await ChangeAsync(orderGuid, "Fulfil", "AeroDesk payment taken", ct).ConfigureAwait(false);

        var envelope = await RetrieveAsync(publicId, draft.Passengers[0].Surname, draft.Draft, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Order {publicId} vanished after creation.");
        lock (_gate)
        {
            _drafts.Remove(orderId);
            _sessionOrders.Remove(orderId);
            _sessionOrders[envelope.Order.OrderId] = envelope;
        }
        return envelope;
    }

    public async Task<OrderEnvelope?> GetOrderAsync(string orderIdOrLocator, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_drafts.TryGetValue(orderIdOrLocator, out var draft))
                return new OrderEnvelope(draft.Draft, "draft");
        }
        return await RetrieveAsync(orderIdOrLocator, lastName: null, template: null, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<OrderEnvelope>> ListOrdersAsync(int limit = 25, CancellationToken ct = default)
    {
        // AeroBus has no order-list endpoint; show what this session touched.
        lock (_gate)
        {
            IReadOnlyList<OrderEnvelope> result = _sessionOrders.Values
                .OrderByDescending(o => o.Order.CreatedAtUtc)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    // ---- Servicing ----

    public Task<OrderEnvelope> ChangeOrderAsync(OrderChange change, string expectedEtag, CancellationToken ct = default) =>
        throw new NotSupportedException("Seat/extras changes are not available on the AeroBus backend yet.");

    public Task<IReadOnlyList<FlightSegment>> GetAlternativeFlightsAsync(FlightSegment segment, DateOnly newDate, CancellationToken ct = default) =>
        throw new NotSupportedException("Flight changes are not available on the AeroBus backend yet.");

    public Task<OrderEnvelope> ChangeFlightAsync(string orderId, string oldSegmentId, FlightSegment newSegment, string expectedEtag, CancellationToken ct = default) =>
        throw new NotSupportedException("Flight changes are not available on the AeroBus backend yet.");

    public async Task<OrderEnvelope> CancelOrderAsync(string orderId, string expectedEtag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Cancelling a draft just discards it — nothing was booked.
            if (_drafts.TryGetValue(orderId, out var draft))
            {
                _drafts.Remove(orderId);
                _sessionOrders.Remove(orderId);
                return new OrderEnvelope(draft.Draft with { Status = OrderStatus.Cancelled }, "draft");
            }
        }

        var current = await RetrieveAsync(orderId, null, null, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Order '{orderId}' not found.");
        if (!string.Equals(current.Etag, expectedEtag, StringComparison.Ordinal))
            throw new EtagConflictException(expectedEtag, current.Etag,
                "The order was modified concurrently — reload it and retry.");

        await ChangeAsync(FindGuid(current), "Cancel", "Cancelled from AeroDesk", ct).ConfigureAwait(false);
        var refreshed = await RetrieveAsync(orderId, null, current.Order, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Order '{orderId}' not found after cancel.");
        lock (_gate) { _sessionOrders[refreshed.Order.OrderId] = refreshed; }
        return refreshed;
    }

    public Task SeedInventoryAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "AeroBus inventory is managed by the airline (schedules + flight builder). Seed it via the AeroBus admin API.");

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    // ---- wire plumbing ----

    private async Task ChangeAsync(Guid orderGuid, string action, string reason, CancellationToken ct)
    {
        await AttachTokenAsync(ct).ConfigureAwait(false);
        using var response = await _http.PostAsJsonAsync("order/change",
            new { orderId = orderGuid, action, reason }, Wire, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException($"AeroBus rejected '{action}': {Snip(body)}");
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, $"order/change {action} failed: {Snip(body)}");
    }

    /// <summary>Retrieve by public OrderId and map to the AeroDesk order shape.
    /// A prior snapshot (draft/known order) supplies flight times AeroBus doesn't embed.</summary>
    private async Task<OrderEnvelope?> RetrieveAsync(string orderId, string? lastName, Order? template, CancellationToken ct)
    {
        await AttachTokenAsync(ct).ConfigureAwait(false);
        using var response = await _http.PostAsJsonAsync("order/retrieve",
            new { orderId, lastName }, Wire, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("orders", out var orders) ||
            orders.ValueKind != JsonValueKind.Array || orders.GetArrayLength() == 0)
            return null;

        var view = orders[0];
        var orderEl = view.GetProperty("order");
        template ??= FindSessionTemplate(orderEl);
        return MapOrder(orderEl, view, template);
    }

    private Order? FindSessionTemplate(JsonElement orderEl)
    {
        var publicId = orderEl.TryGetProperty("orderId", out var pid) ? pid.GetString() : null;
        if (publicId is null) return null;
        lock (_gate)
        {
            return _sessionOrders.GetValueOrDefault(publicId)?.Order;
        }
    }

    private Guid FindGuid(OrderEnvelope envelope) =>
        Guid.TryParse(envelope.Order.RecordLocator, out var g) ? g
            : throw new InvalidOperationException("Missing AeroBus order id.");

    private OrderEnvelope MapOrder(JsonElement orderEl, JsonElement view, Order? template)
    {
        var publicId = orderEl.GetProperty("orderId").GetString() ?? "";
        var guid = orderEl.GetProperty("id").GetGuid();
        var status = orderEl.TryGetProperty("status", out var st) ? st.GetString() ?? "Pending" : "Pending";
        var etag = orderEl.TryGetProperty("concurrencyId", out var cc) ? cc.GetString() ?? "" : "";
        var created = orderEl.TryGetProperty("created", out var cr) && cr.ValueKind == JsonValueKind.String
            ? cr.GetDateTime() : DateTime.UtcNow;

        var passengers = new List<Passenger>();
        if (view.TryGetProperty("passengers", out var paxArr) && paxArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in paxArr.EnumerateArray())
            {
                passengers.Add(new Passenger
                {
                    PassengerId = p.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Type = Enum.TryParse<Ptc>(p.TryGetProperty("paxType", out var pt) ? pt.GetString() : "ADT", out var ptc) ? ptc : Ptc.ADT,
                    GivenName = p.TryGetProperty("firstName", out var fn) ? fn.GetString() ?? "" : "",
                    Surname = p.TryGetProperty("lastName", out var ln) ? ln.GetString() ?? "" : "",
                    Title = p.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                });
            }
        }

        // Total from item charges when present, else the template's price.
        decimal amount = 0;
        var currency = template?.Currency ?? Currency;
        if (orderEl.TryGetProperty("orderItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number)
                    amount += amt.GetDecimal();
                if (item.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String)
                    currency = cur.GetString() ?? currency;
            }
        }

        var order = new Order
        {
            OrderId = publicId,
            RecordLocator = guid.ToString(),      // AeroBus's internal id, needed for /order/change
            Owner = "AT",
            Status = MapStatus(status),
            Segments = template?.Segments ?? [],
            Items = template?.Items ?? [],
            Passengers = passengers.Count > 0 ? passengers : template?.Passengers ?? [],
            Ancillaries = template?.Ancillaries ?? [],
            Seats = template?.Seats ?? [],
            Documents = template?.Documents ?? [],
            TimeLimits = [],
            Currency = currency,
            CreatedAtUtc = created,
            CreatedBy = null,
        };

        // Prefer the server's charge total when the template price disagrees.
        if (amount > 0 && template is not null && Math.Abs(template.TotalPrice.Total - amount) > 0.01m)
            order = order with { Items = [], Ancillaries = [
                new Ancillary(Guid.NewGuid().ToString("N"), "FARE", "Fare (AeroBus)", "", "ALL",
                    new PriceDetail(amount, 0m, currency))] };

        return new OrderEnvelope(order, etag);
    }

    private static OrderStatus MapStatus(string aeroBusStatus) => aeroBusStatus switch
    {
        "Pending" => OrderStatus.PendingPayment,
        "Confirmed" => OrderStatus.Paid,
        "Fulfilled" or "CheckedIn" or "Boarded" or "Flown" or "Closed" => OrderStatus.Ticketed,
        "Cancelled" or "Refunded" => OrderStatus.Cancelled,
        _ => OrderStatus.Draft,
    };

    private ShoppedOffer? MapBundleToOffer(Guid aeroBusOfferId, Guid solutionId, JsonElement bundle,
        IReadOnlyList<FlightSegment> segments, ShopRequest request)
    {
        if (!bundle.TryGetProperty("price", out var price)) return null;
        var bundleId = bundle.GetProperty("id").GetGuid();
        var code = bundle.TryGetProperty("bundleCode", out var bc) ? bc.GetString() ?? "LITE" : "LITE";
        var name = bundle.TryGetProperty("name", out var bn) ? bn.GetString() ?? code : code;

        decimal total = price.TryGetProperty("total", out var t) ? t.GetDecimal() : 0m;
        decimal baseAmount = price.TryGetProperty("base", out var b) ? b.GetDecimal() : total;
        var currency = price.TryGetProperty("currency", out var cur) ? cur.GetString() ?? Currency : Currency;
        Currency = currency;

        var fare = BundleFare(code, name);
        var perPax = new PriceDetail(baseAmount, total - baseAmount, currency);

        var items = new List<OfferItem>();
        void Add(Ptc ptc, int count, decimal factor)
        {
            if (count <= 0) return;
            items.Add(new OfferItem(
                Guid.NewGuid().ToString("N"), ptc, count, segments.Select(s => s.SegmentId).ToList(), fare,
                new PriceDetail(Math.Round(perPax.BaseAmount * factor, 2), Math.Round(perPax.Taxes * factor, 2), currency)));
        }
        Add(Ptc.ADT, request.Adults, 1m);
        Add(Ptc.CHD, request.Children, 1m);   // AeroBus prices per-pax uniformly today
        Add(Ptc.INF, request.Infants, 0.1m);

        var offer = new Offer
        {
            OfferId = $"AB-{Guid.NewGuid():N}",
            Owner = segments[0].Carrier,
            Segments = segments,
            Items = items,
            OfferExpiry = new TimeLimit(TimeLimit.OfferExpiry, DateTime.UtcNow.AddMinutes(30)),
            Currency = currency,
        };
        return new ShoppedOffer(aeroBusOfferId, solutionId, bundleId, offer);
    }

    /// <summary>The AeroBus brand ladder → fare rules shown on the cards.</summary>
    private static FareComponent BundleFare(string code, string name) => code switch
    {
        "FLEXPLUS" => new FareComponent(code, name, "2 x 23kg", Changeable: true, Refundable: true,
            ChangeFee: 0m, SeatSelectionIncluded: true, MealIncluded: true, PriorityBoarding: true),
        "FLEX" => new FareComponent(code, name, "1 x 23kg", Changeable: true, Refundable: false, ChangeFee: 0m),
        _ => new FareComponent(code, name, "Cabin bag only", Changeable: false, Refundable: false),
    };

    private static List<FlightSegment> MapSegments(JsonElement solution, Cabin cabin)
    {
        var segments = new List<FlightSegment>();
        if (!solution.TryGetProperty("flights", out var flights) || flights.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var f in flights.EnumerateArray())
        {
            var dep = f.GetProperty("departure");
            var arr = f.GetProperty("arrival");
            segments.Add(new FlightSegment(
                SegmentId: f.TryGetProperty("flightRef", out var fr) ? fr.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString(),
                Carrier: f.TryGetProperty("marketingCarrier", out var mc) ? mc.GetString() ?? "" : "",
                FlightNumber: (f.TryGetProperty("marketingCarrier", out var c2) ? c2.GetString() : "") +
                              (f.TryGetProperty("marketingFlightNumber", out var fn) ? fn.GetString() : ""),
                Origin: dep.TryGetProperty("airport", out var da) ? da.GetString() ?? "" : "",
                Destination: arr.TryGetProperty("airport", out var aa) ? aa.GetString() ?? "" : "",
                DepartureUtc: dep.TryGetProperty("scheduledTimeLocal", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetDateTime() : default,
                ArrivalUtc: arr.TryGetProperty("scheduledTimeLocal", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTime() : default,
                Equipment: f.TryGetProperty("equipmentCode", out var eq) ? eq.GetString() ?? "" : "",
                Cabin: cabin,
                BookingClass: f.TryGetProperty("bookingClass", out var bk) ? bk.GetString() ?? "Y" : "Y"));
        }
        return segments;
    }

    private static string CabinCode(Cabin cabin) => cabin switch
    {
        Cabin.First => "F",
        Cabin.Business => "J",
        Cabin.PremiumEconomy => "W",
        _ => "Y",
    };

    private static string Snip(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var prop in new[] { "error", "reason", "message" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString()!;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}
