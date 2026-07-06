using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using Xunit;

namespace AeroDesk.Core.Tests;

public sealed class InMemoryRetailingServiceTests
{
    private static readonly DateOnly TravelDate = new(2026, 8, 14);

    private static ShopRequest OneWay(int adults = 1, int children = 0, int infants = 0) => new()
    {
        Legs = [new JourneyLeg("JFK", "LHR", TravelDate)],
        TripType = TripType.OneWay,
        Adults = adults,
        Children = children,
        Infants = infants,
    };

    private static List<Passenger> PassengersFor(int adults, int children = 0, int infants = 0)
    {
        var pax = new List<Passenger>();
        for (var i = 0; i < adults; i++)
            pax.Add(new Passenger { PassengerId = $"ADT{i}", Type = Ptc.ADT, GivenName = "Ada", Surname = $"Adult{i}" });
        for (var i = 0; i < children; i++)
            pax.Add(new Passenger { PassengerId = $"CHD{i}", Type = Ptc.CHD, GivenName = "Chris", Surname = $"Child{i}" });
        for (var i = 0; i < infants; i++)
            pax.Add(new Passenger { PassengerId = $"INF{i}", Type = Ptc.INF, GivenName = "Ivy", Surname = $"Infant{i}" });
        return pax;
    }

    [Fact]
    public async Task Search_Returns_Priced_Offers_Per_Fare_Family()
    {
        await using var svc = new InMemoryRetailingService();

        var offers = await svc.SearchOffersAsync(OneWay());

        Assert.NotEmpty(offers);
        Assert.Contains(offers, o => o.FareFamily == "Basic");
        Assert.Contains(offers, o => o.FareFamily == "Flex");
        Assert.All(offers, o =>
        {
            Assert.True(o.TotalPrice.Total > 0);
            Assert.Equal("USD", o.Currency);
            Assert.NotEmpty(o.Segments);
            Assert.False(o.OfferExpiry.IsExpired(DateTime.UtcNow));
        });
        // Flex must price above Basic for the same itinerary.
        var basic = offers.First(o => o.FareFamily == "Basic");
        var flex = offers.First(o => o.FareFamily == "Flex" && o.Segments[0].SegmentId == basic.Segments[0].SegmentId);
        Assert.True(flex.TotalPrice.Total > basic.TotalPrice.Total);
    }

    [Fact]
    public async Task Search_MultiCity_Builds_One_Segment_Per_Leg()
    {
        await using var svc = new InMemoryRetailingService();
        var request = new ShopRequest
        {
            Legs =
            [
                new JourneyLeg("JFK", "LHR", TravelDate),
                new JourneyLeg("LHR", "DXB", TravelDate.AddDays(4)),
                new JourneyLeg("DXB", "SIN", TravelDate.AddDays(9)),
            ],
            TripType = TripType.MultiCity,
        };

        var offers = await svc.SearchOffersAsync(request);

        Assert.NotEmpty(offers);
        Assert.All(offers, o =>
        {
            Assert.Equal(3, o.Segments.Count);
            Assert.Equal(("JFK", "LHR"), (o.Segments[0].Origin, o.Segments[0].Destination));
            Assert.Equal(("LHR", "DXB"), (o.Segments[1].Origin, o.Segments[1].Destination));
            Assert.Equal(("DXB", "SIN"), (o.Segments[2].Origin, o.Segments[2].Destination));
        });
    }

    [Fact]
    public async Task Search_Unserved_Route_Returns_Empty()
    {
        await using var svc = new InMemoryRetailingService();
        var offers = await svc.SearchOffersAsync(new ShopRequest
        {
            Legs = [new JourneyLeg("JFK", "SIN", TravelDate)],
        });
        Assert.Empty(offers);
    }

    [Fact]
    public async Task Search_Rejects_Infants_Exceeding_Adults()
    {
        await using var svc = new InMemoryRetailingService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SearchOffersAsync(OneWay(adults: 1, infants: 2)));
    }

    [Fact]
    public async Task CreateOrder_Produces_PendingPayment_With_TimeLimits_And_Locator()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(OneWay(adults: 2, children: 1))).First();

        var envelope = await svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 2, children: 1));
        var order = envelope.Order;

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Matches("^[A-Z]{6}$", order.RecordLocator);
        Assert.Contains(order.TimeLimits, t => t.Kind == TimeLimit.PaymentDeadline);
        Assert.Equal(3, order.Passengers.Count);
        Assert.Equal(offer.TotalPrice.Total, order.TotalPrice.Total);
        Assert.NotEmpty(envelope.Etag);
    }

    [Fact]
    public async Task CreateOrder_Rejects_Passenger_Mismatch()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(OneWay(adults: 2))).First();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1)));
    }

    [Fact]
    public async Task CreateOrder_Rejects_Expired_Offer()
    {
        var now = DateTime.UtcNow;
        await using var svc = new InMemoryRetailingService(clock: () => now);
        var offer = (await svc.SearchOffersAsync(OneWay())).First();

        now = now.AddHours(2); // stride past the 30-minute offer lifetime

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1)));
    }

    [Fact]
    public async Task GetOrder_Finds_By_Id_And_By_Locator()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(OneWay())).First();
        var created = await svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1));

        var byId = await svc.GetOrderAsync(created.Order.OrderId);
        var byLocator = await svc.GetOrderAsync(created.Order.RecordLocator.ToLowerInvariant());

        Assert.Equal(created.Order.OrderId, byId!.Order.OrderId);
        Assert.Equal(created.Order.OrderId, byLocator!.Order.OrderId);
        Assert.Null(await svc.GetOrderAsync("NOPE99"));
    }

    [Fact]
    public async Task Pay_Tickets_The_Order_And_Issues_Documents()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(OneWay(adults: 1, infants: 1))).First();
        var created = await svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1, infants: 1));

        var paid = await svc.PayOrderAsync(created.Order.OrderId,
            new PaymentToken("tok_demo_visa", "4242"), created.Etag);

        Assert.Equal(OrderStatus.Ticketed, paid.Order.Status);
        Assert.Single(paid.Order.PaymentIds);
        Assert.Equal(2, paid.Order.Documents.Count);
        Assert.Contains(paid.Order.Documents, d => d.Kind == "ETKT");
        Assert.Contains(paid.Order.Documents, d => d.Kind == "EMD"); // infant
        Assert.DoesNotContain(paid.Order.TimeLimits, t => t.Kind == TimeLimit.PaymentDeadline);
        Assert.NotEqual(created.Etag, paid.Etag);
    }

    [Fact]
    public async Task Stale_Etag_Throws_Conflict()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(OneWay())).First();
        var created = await svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1));

        // A first mutation rotates the etag…
        await svc.PayOrderAsync(created.Order.OrderId, new PaymentToken("tok", "1111"), created.Etag);

        // …so the original etag is now stale.
        var ex = await Assert.ThrowsAsync<EtagConflictException>(() =>
            svc.CancelOrderAsync(created.Order.OrderId, created.Etag));
        Assert.Equal(created.Etag, ex.ExpectedEtag);
        Assert.NotNull(ex.ActualEtag);
    }

    [Fact]
    public async Task ChangeOrder_Adds_And_Removes_Ancillaries_And_Reseats()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(OneWay())).First();
        var created = await svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1));
        var segmentId = created.Order.Segments[0].SegmentId;
        var price = new PriceDetail(40m, 4.8m, "USD");

        var withBag = await svc.ChangeOrderAsync(new OrderChange(
            created.Order.OrderId,
            AddAncillaries: [new Ancillary("svc1", "XBAG", "Extra bag 23kg", segmentId, "ADT0", price)],
            RemoveServiceIds: [],
            AddSeats: [new SeatAssignment(segmentId, "ADT0", "12A", new PriceDetail(25m, 3m, "USD"))]),
            created.Etag);

        Assert.Single(withBag.Order.Ancillaries);
        Assert.Equal("12A", Assert.Single(withBag.Order.Seats).SeatNumber);

        // Reseat replaces the previous assignment for the same pax+segment; the bag is removable.
        var reseated = await svc.ChangeOrderAsync(new OrderChange(
            created.Order.OrderId,
            AddAncillaries: [],
            RemoveServiceIds: ["svc1"],
            AddSeats: [new SeatAssignment(segmentId, "ADT0", "14C", new PriceDetail(25m, 3m, "USD"))]),
            withBag.Etag);

        Assert.Empty(reseated.Order.Ancillaries);
        Assert.Equal("14C", Assert.Single(reseated.Order.Seats).SeatNumber);
    }

    [Fact]
    public async Task Cancel_Sets_Cancelled_And_Blocks_Further_Changes()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(OneWay())).First();
        var created = await svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1));

        var cancelled = await svc.CancelOrderAsync(created.Order.OrderId, created.Etag);

        Assert.Equal(OrderStatus.Cancelled, cancelled.Order.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ChangeOrderAsync(
            new OrderChange(created.Order.OrderId, [], [], []), cancelled.Etag));
    }

    [Fact]
    public async Task Declined_Payment_Leaves_Order_PendingPayment()
    {
        await using var svc = new InMemoryRetailingService(gateway: new DecliningGateway());
        var offer = (await svc.SearchOffersAsync(OneWay())).First();
        var created = await svc.CreateOrderAsync(offer.OfferId, PassengersFor(adults: 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PayOrderAsync(created.Order.OrderId, new PaymentToken("tok", "0002"), created.Etag));

        var after = await svc.GetOrderAsync(created.Order.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, after!.Order.Status);
    }

    private sealed class DecliningGateway : IPaymentGateway
    {
        public Task<PaymentAuthorization> AuthorizeAsync(PaymentToken token, PriceDetail amount, CancellationToken ct = default) =>
            Task.FromResult(new PaymentAuthorization(false, "", "Card declined (test)."));
    }
}
