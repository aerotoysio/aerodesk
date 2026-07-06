using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using Xunit;

namespace AeroDesk.Core.Tests;

public sealed class ChangeFlightAndFareTests
{
    private static readonly DateOnly TravelDate = new(2026, 8, 14);

    private static ShopRequest Request(Cabin cabin = Cabin.Economy, int adults = 1, int children = 0, int infants = 0) => new()
    {
        Legs = [new JourneyLeg("JFK", "LHR", TravelDate)],
        Adults = adults,
        Children = children,
        Infants = infants,
        Cabin = cabin,
    };

    private static List<Passenger> Pax(int adults, int children = 0, int infants = 0)
    {
        var pax = new List<Passenger>();
        for (var i = 0; i < adults; i++)
            pax.Add(new Passenger { PassengerId = $"ADT{i}", Type = Ptc.ADT, GivenName = "Ada", Surname = $"A{i}" });
        for (var i = 0; i < children; i++)
            pax.Add(new Passenger { PassengerId = $"CHD{i}", Type = Ptc.CHD, GivenName = "Chris", Surname = $"C{i}" });
        for (var i = 0; i < infants; i++)
            pax.Add(new Passenger { PassengerId = $"INF{i}", Type = Ptc.INF, GivenName = "Ivy", Surname = $"I{i}" });
        return pax;
    }

    [Fact]
    public async Task Economy_Has_Three_Branded_Fares_With_Expected_Rules()
    {
        await using var svc = new InMemoryRetailingService();
        var offers = await svc.SearchOffersAsync(Request());
        var families = offers.Select(o => o.FareFamily).Distinct().ToList();

        Assert.Equal(["Basic", "Standard", "Flex"], families.Take(3).ToList());

        var basic = offers.First(o => o.FareFamily == "Basic").Items[0].Fare;
        Assert.False(basic.Changeable);
        Assert.Equal("No changes", basic.ChangePolicy);
        Assert.Equal("No inclusions", basic.PerksLine);

        var standard = offers.First(o => o.FareFamily == "Standard").Items[0].Fare;
        Assert.True(standard.Changeable);
        Assert.Equal(75m, standard.ChangeFee);
        Assert.False(standard.Refundable);

        var flex = offers.First(o => o.FareFamily == "Flex").Items[0].Fare;
        Assert.Equal(0m, flex.ChangeFee);
        Assert.True(flex.Refundable);
        Assert.True(flex.SeatSelectionIncluded);
        Assert.Equal("Seat • Meal • Priority", flex.PerksLine);
    }

    [Fact]
    public async Task Business_Sells_Premium_Brands()
    {
        await using var svc = new InMemoryRetailingService();
        var offers = await svc.SearchOffersAsync(Request(Cabin.Business));
        var families = offers.Select(o => o.FareFamily).Distinct().ToList();

        Assert.Equal(["Classic", "Flex"], families.Take(2).ToList());
        Assert.All(offers, o => Assert.True(o.Items[0].Fare.SeatSelectionIncluded));
    }

    [Fact]
    public async Task ChangeFlight_On_Flex_Is_Free_And_Releases_Seats()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(Request())).First(o => o.FareFamily == "Flex");
        var created = await svc.CreateOrderAsync(offer.OfferId, Pax(1));
        var oldSegment = created.Order.Segments[0];

        // Seat the passenger first so we can prove seats are released on rebooking.
        var seatMap = await svc.GetSeatMapAsync(oldSegment);
        var seat = seatMap.Seats.First(s => s.Available);
        var seated = await svc.ChangeOrderAsync(new OrderChange(created.Order.OrderId, [],
            [], [new SeatAssignment(oldSegment.SegmentId, "ADT0", seat.SeatNumber, seat.Price)]), created.Etag);

        var alternatives = await svc.GetAlternativeFlightsAsync(oldSegment, TravelDate.AddDays(1));
        Assert.NotEmpty(alternatives);

        var rebooked = await svc.ChangeFlightAsync(created.Order.OrderId, oldSegment.SegmentId, alternatives[0], seated.Etag);

        Assert.DoesNotContain(rebooked.Order.Segments, s => s.SegmentId == oldSegment.SegmentId);
        Assert.Contains(rebooked.Order.Segments, s => s.SegmentId == alternatives[0].SegmentId);
        Assert.Empty(rebooked.Order.Seats);                                        // released
        Assert.DoesNotContain(rebooked.Order.Ancillaries, a => a.Code == "CHG");   // Flex = free
        Assert.All(rebooked.Order.Items, i => Assert.Contains(alternatives[0].SegmentId, i.SegmentIds));
    }

    [Fact]
    public async Task ChangeFlight_On_Standard_Charges_Fee_Per_Seated_Pax()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(Request(adults: 2, children: 1, infants: 1)))
            .First(o => o.FareFamily == "Standard");
        var created = await svc.CreateOrderAsync(offer.OfferId, Pax(2, 1, 1));
        var oldSegment = created.Order.Segments[0];
        var alternatives = await svc.GetAlternativeFlightsAsync(oldSegment, TravelDate.AddDays(2));

        var rebooked = await svc.ChangeFlightAsync(created.Order.OrderId, oldSegment.SegmentId, alternatives[0], created.Etag);

        var fee = Assert.Single(rebooked.Order.Ancillaries, a => a.Code == "CHG");
        Assert.Equal(75m * 3, fee.Price.Total); // 2 ADT + 1 CHD seated; infant free
        Assert.Equal(fee.Price.Total + offer.TotalPrice.Total, rebooked.Order.TotalPrice.Total);
    }

    [Fact]
    public async Task ChangeFlight_On_Basic_Is_Rejected()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(Request())).First(o => o.FareFamily == "Basic");
        var created = await svc.CreateOrderAsync(offer.OfferId, Pax(1));
        var oldSegment = created.Order.Segments[0];
        var alternatives = await svc.GetAlternativeFlightsAsync(oldSegment, TravelDate.AddDays(1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ChangeFlightAsync(created.Order.OrderId, oldSegment.SegmentId, alternatives[0], created.Etag));
        Assert.Contains("Basic", ex.Message);
    }

    [Fact]
    public async Task Bag_Ancillaries_Follow_The_New_Flight()
    {
        await using var svc = new InMemoryRetailingService();
        var offer = (await svc.SearchOffersAsync(Request())).First(o => o.FareFamily == "Flex");
        var created = await svc.CreateOrderAsync(offer.OfferId, Pax(1));
        var oldSegment = created.Order.Segments[0];

        var withBag = await svc.ChangeOrderAsync(new OrderChange(created.Order.OrderId,
            [new Ancillary("bag1", "XBAG", "Extra bag", oldSegment.SegmentId, "ADT0", new PriceDetail(40m, 4.8m, "USD"))],
            [], []), created.Etag);

        var alternatives = await svc.GetAlternativeFlightsAsync(oldSegment, TravelDate.AddDays(1));
        var rebooked = await svc.ChangeFlightAsync(created.Order.OrderId, oldSegment.SegmentId, alternatives[0], withBag.Etag);

        var bag = Assert.Single(rebooked.Order.Ancillaries, a => a.Code == "XBAG");
        Assert.Equal(alternatives[0].SegmentId, bag.SegmentId);
    }
}
