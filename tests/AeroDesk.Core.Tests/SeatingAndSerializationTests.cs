using System.Text.Json;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using AeroDesk.Core.Settings;
using Xunit;

namespace AeroDesk.Core.Tests;

public sealed class SeatingAndSerializationTests
{
    private static FlightSegment Segment(string equipment = "A320", double hours = 1.3) => new(
        "AT200-20260814", "AT", "AT200", "LHR", "CDG",
        new DateTime(2026, 8, 14, 7, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 8, 14, 7, 0, 0, DateTimeKind.Utc).AddHours(hours),
        equipment, Cabin.Economy, "Y");

    [Fact]
    public void SeatMap_Is_Deterministic_For_A_Segment()
    {
        var first = SeatMapFactory.Build(Segment());
        var second = SeatMapFactory.Build(Segment());

        Assert.Equal(
            first.Seats.Select(s => (s.SeatNumber, s.Available, s.Price.Total)),
            second.Seats.Select(s => (s.SeatNumber, s.Available, s.Price.Total)));
    }

    [Fact]
    public void SeatMap_Has_Sane_Shape_And_Availability()
    {
        var map = SeatMapFactory.Build(Segment("B789", 7));

        Assert.Equal(["A", "B", "C", "", "D", "E", "F", "", "G", "H", "K"], map.Columns);
        Assert.All(map.Seats, s => Assert.Matches("^[0-9]+[A-K]$", s.SeatNumber));

        var availableRatio = map.Seats.Count(s => s.Available) / (double)map.Seats.Count;
        Assert.InRange(availableRatio, 0.5, 0.9);

        // Exit-row seats price above their zone's base.
        var exit = map.Seats.First(s => s.ExitRow);
        var standard = map.Seats.First(s => !s.ExitRow && s.Zone == exit.Zone);
        Assert.True(exit.Price.Total > standard.Price.Total);
    }

    [Fact]
    public void Catalog_Adds_Wifi_Only_On_LongHaul()
    {
        var shortHaul = SeatMapFactory.Catalog(Segment("A320", 1.3));
        var longHaul = SeatMapFactory.Catalog(Segment("B789", 7));

        Assert.DoesNotContain(shortHaul, a => a.Code == "WIFI");
        Assert.Contains(longHaul, a => a.Code == "WIFI");
    }

    [Fact]
    public async Task Order_Survives_A_Wire_RoundTrip()
    {
        // The DocumentForge store persists orders as JSON — every field must survive.
        await using var svc = new InMemoryRetailingService();
        var offers = await svc.SearchOffersAsync(new ShopRequest
        {
            Legs = [new JourneyLeg("JFK", "LHR", new DateOnly(2026, 8, 14))],
            Adults = 1,
            Infants = 1,
        });
        var created = await svc.CreateOrderAsync(offers[0].OfferId,
        [
            new Passenger
            {
                PassengerId = "ADT0", Type = Ptc.ADT, GivenName = "Ada", Surname = "Adult", Title = "Ms",
                DateOfBirth = new DateOnly(1990, 5, 1), Email = "ada@example.com", Phone = "+1 555 0100",
                Document = new TravelDocument("PT", "P1234567", "US", new DateOnly(2031, 1, 1)),
            },
            new Passenger { PassengerId = "INF0", Type = Ptc.INF, GivenName = "Ivy", Surname = "Infant" },
        ]);
        var paid = await svc.PayOrderAsync(created.Order.OrderId, new PaymentToken("tok_x", "4242"), created.Etag);

        var json = JsonSerializer.Serialize(paid.Order, JsonDefaults.Wire);
        var back = JsonSerializer.Deserialize<Order>(json, JsonDefaults.Wire)!;

        Assert.Equal(paid.Order.OrderId, back.OrderId);
        Assert.Equal(paid.Order.RecordLocator, back.RecordLocator);
        Assert.Equal(OrderStatus.Ticketed, back.Status);
        Assert.Equal(paid.Order.Segments.Count, back.Segments.Count);
        Assert.Equal(paid.Order.Passengers.Count, back.Passengers.Count);
        Assert.Equal("P1234567", back.Passengers[0].Document!.Number);
        Assert.Equal(paid.Order.Documents.Select(d => d.DocumentNumber), back.Documents.Select(d => d.DocumentNumber));
        Assert.Equal(paid.Order.TotalPrice.Total, back.TotalPrice.Total);
        Assert.Equal(paid.Order.CreatedAtUtc, back.CreatedAtUtc);
    }

    [Fact]
    public void Offer_And_Route_Survive_Wire_RoundTrips()
    {
        var offer = OfferFactory.Build(new ShopRequest
        {
            Legs = [new JourneyLeg("JFK", "LHR", new DateOnly(2026, 8, 14))],
            Adults = 2, Children = 1,
        }, DateTime.UtcNow, () => "OF-test")[0];

        var offerBack = JsonSerializer.Deserialize<Offer>(
            JsonSerializer.Serialize(offer, JsonDefaults.Wire), JsonDefaults.Wire)!;
        Assert.Equal(offer.OfferId, offerBack.OfferId);
        Assert.Equal(offer.TotalPrice.Total, offerBack.TotalPrice.Total);
        Assert.Equal(offer.Items.Count, offerBack.Items.Count);
        Assert.Equal(offer.OfferExpiry.ExpiresAtUtc, offerBack.OfferExpiry.ExpiresAtUtc);

        var route = FlightSchedule.Routes[0];
        var routeBack = JsonSerializer.Deserialize<FlightSchedule.Route>(
            JsonSerializer.Serialize(route, JsonDefaults.Wire), JsonDefaults.Wire)!;
        Assert.Equal(route, routeBack);
    }

    [Fact]
    public async Task ListOrders_Returns_Newest_First()
    {
        var now = DateTime.UtcNow;
        await using var svc = new InMemoryRetailingService(clock: () => now);
        var offers = await svc.SearchOffersAsync(new ShopRequest
        {
            Legs = [new JourneyLeg("LHR", "CDG", new DateOnly(2026, 8, 14))],
        });
        var pax = new List<Passenger>
        {
            new() { PassengerId = "ADT0", Type = Ptc.ADT, GivenName = "Ada", Surname = "Adult" },
        };

        var first = await svc.CreateOrderAsync(offers[0].OfferId, pax);
        now = now.AddMinutes(5);
        var second = await svc.CreateOrderAsync(offers[1].OfferId, pax);

        var list = await svc.ListOrdersAsync();
        Assert.Equal([second.Order.OrderId, first.Order.OrderId], list.Select(o => o.Order.OrderId));
    }
}
