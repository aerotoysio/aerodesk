using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using Xunit;

namespace AeroDesk.Core.Tests;

/// <summary>
/// End-to-end against a REAL dfdb serve node — the dogfood test for
/// DocumentForge as an airline order store. Skipped unless AERODESK_DF_URL is
/// set (e.g. http://localhost:5001 from
/// <c>dfdb serve --data-dir … --port 5001 --insecure-dev-mode</c>).
/// </summary>
public sealed class DocumentForgeIntegrationTests
{
    private static string? DfUrl => Environment.GetEnvironmentVariable("AERODESK_DF_URL");

    private sealed class DfFactAttribute : FactAttribute
    {
        public DfFactAttribute()
        {
            if (string.IsNullOrEmpty(DfUrl))
                Skip = "Set AERODESK_DF_URL to a running dfdb serve node to run DF integration tests.";
        }
    }

    private static DocumentForgeRetailingService Service(string database) => new(
        new DfConnectionDescriptor { Name = "it", Url = DfUrl!, Database = database },
        apiKey: null);

    [DfFact]
    public async Task Full_Booking_Lifecycle_On_DocumentForge()
    {
        // Fresh database per run so the test is self-contained.
        var database = $"airline_it_{Guid.NewGuid():N}";
        await using var svc = Service(database);

        await svc.ConnectAsync();
        await svc.SeedInventoryAsync();

        // Idempotent reseed must not duplicate the schedule.
        await svc.SeedInventoryAsync();

        // --- Shop (return trip) ---
        var travel = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21));
        var offers = await svc.SearchOffersAsync(new ShopRequest
        {
            Legs =
            [
                new JourneyLeg("JFK", "LHR", travel),
                new JourneyLeg("LHR", "JFK", travel.AddDays(7)),
            ],
            TripType = TripType.Return,
            Adults = 1,
        });
        Assert.NotEmpty(offers);
        var offer = offers.First(o => o.FareFamily == "Flex");
        Assert.Equal(2, offer.Segments.Count);

        // --- Reprice ---
        var repriced = await svc.RepriceOfferAsync(offer.OfferId);
        Assert.NotNull(repriced);
        Assert.Equal(offer.TotalPrice.Total, repriced!.TotalPrice.Total);

        // --- Order ---
        var created = await svc.CreateOrderAsync(offer.OfferId,
        [
            new Passenger { PassengerId = "ADT1", Type = Ptc.ADT, GivenName = "Amelia", Surname = "Earhart", Email = "a@example.com" },
        ]);
        Assert.Equal(OrderStatus.PendingPayment, created.Order.Status);
        Assert.NotEmpty(created.Etag);

        // --- Retrieve by id and locator ---
        var byId = await svc.GetOrderAsync(created.Order.OrderId);
        var byLocator = await svc.GetOrderAsync(created.Order.RecordLocator);
        Assert.Equal(created.Order.OrderId, byId!.Order.OrderId);
        Assert.Equal(created.Order.OrderId, byLocator!.Order.OrderId);

        // --- Servicing: seat on the outbound ---
        var outbound = created.Order.Segments[0];
        var seatMap = await svc.GetSeatMapAsync(outbound);
        var seat = seatMap.Seats.First(s => s.Available);
        var seated = await svc.ChangeOrderAsync(new OrderChange(
            created.Order.OrderId,
            AddAncillaries: [],
            RemoveServiceIds: [],
            AddSeats: [new SeatAssignment(outbound.SegmentId, "ADT1", seat.SeatNumber, seat.Price)]),
            created.Etag);
        Assert.Single(seated.Order.Seats);
        Assert.NotEqual(created.Etag, seated.Etag);

        // --- Stale etag must 412 into EtagConflictException ---
        await Assert.ThrowsAsync<EtagConflictException>(() =>
            svc.CancelOrderAsync(created.Order.OrderId, created.Etag));

        // --- Pay & ticket ---
        var paid = await svc.PayOrderAsync(created.Order.OrderId,
            new PaymentToken("tok_it_visa", "4242"), seated.Etag);
        Assert.Equal(OrderStatus.Ticketed, paid.Order.Status);
        Assert.NotEmpty(paid.Order.Documents);
        Assert.Contains(paid.Order.Documents, d => d.Kind == "ETKT");

        // --- Listing sees it ---
        var list = await svc.ListOrdersAsync();
        Assert.Contains(list, o => o.Order.OrderId == created.Order.OrderId);

        // --- Cancel with the fresh etag ---
        var cancelled = await svc.CancelOrderAsync(created.Order.OrderId, paid.Etag);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Order.Status);
    }

    /// <summary>
    /// The call-centre servicing story on a real node: a family booking goes on
    /// hold, is retrieved later by record locator, the outbound flight is moved
    /// a day (Standard fare → change fee), and the held order is then paid —
    /// with every step persisted in DocumentForge.
    /// </summary>
    [DfFact]
    public async Task Held_MultiPax_Order_Retrieve_ChangeFlight_Then_Pay()
    {
        var database = $"airline_it_{Guid.NewGuid():N}";
        await using var svc = Service(database);
        await svc.ConnectAsync();
        await svc.SeedInventoryAsync();

        // --- Family shopping: 2 adults, 1 child, 1 infant, Standard fare ---
        var travel = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var offers = await svc.SearchOffersAsync(new ShopRequest
        {
            Legs = [new JourneyLeg("LHR", "DXB", travel)],
            Adults = 2, Children = 1, Infants = 1,
        });
        var offer = offers.First(o => o.FareFamily == "Standard");

        var held = await svc.CreateOrderAsync(offer.OfferId,
        [
            new Passenger { PassengerId = "ADT1", Type = Ptc.ADT, GivenName = "Ada", Surname = "Family", Email = "ada@example.com" },
            new Passenger { PassengerId = "ADT2", Type = Ptc.ADT, GivenName = "Alan", Surname = "Family" },
            new Passenger { PassengerId = "CHD1", Type = Ptc.CHD, GivenName = "Chris", Surname = "Family", DateOfBirth = new DateOnly(2019, 3, 2) },
            new Passenger { PassengerId = "INF1", Type = Ptc.INF, GivenName = "Ivy", Surname = "Family", DateOfBirth = new DateOnly(2025, 11, 20) },
        ]);
        Assert.Equal(OrderStatus.PendingPayment, held.Order.Status); // on hold

        // --- Later: agent retrieves the held order by its record locator ---
        var retrieved = await svc.GetOrderAsync(held.Order.RecordLocator);
        Assert.NotNull(retrieved);
        Assert.Equal(4, retrieved!.Order.Passengers.Count);

        // --- Change the flight to the next day (Standard: 75 USD x 3 seated pax) ---
        var oldSegment = retrieved.Order.Segments[0];
        var alternatives = await svc.GetAlternativeFlightsAsync(oldSegment, travel.AddDays(1));
        Assert.NotEmpty(alternatives);

        var rebooked = await svc.ChangeFlightAsync(
            retrieved.Order.OrderId, oldSegment.SegmentId, alternatives[0], retrieved.Etag);
        Assert.Contains(rebooked.Order.Segments, s => s.SegmentId == alternatives[0].SegmentId);
        var changeFee = Assert.Single(rebooked.Order.Ancillaries, a => a.Code == "CHG");
        Assert.Equal(75m * 3, changeFee.Price.Total);

        // --- Pay the held order (total now includes the change fee) ---
        var paid = await svc.PayOrderAsync(rebooked.Order.OrderId,
            new PaymentToken("tok_family_visa", "9010"), rebooked.Etag);
        Assert.Equal(OrderStatus.Ticketed, paid.Order.Status);
        Assert.Equal(4, paid.Order.Documents.Count);
        Assert.Equal(3, paid.Order.Documents.Count(d => d.Kind == "ETKT"));
        Assert.Equal(1, paid.Order.Documents.Count(d => d.Kind == "EMD")); // infant

        // --- The truth lives in DocumentForge: re-fetch and verify persistence ---
        var final = await svc.GetOrderAsync(paid.Order.OrderId);
        Assert.Equal(OrderStatus.Ticketed, final!.Order.Status);
        Assert.Contains(final.Order.Segments, s => s.SegmentId == alternatives[0].SegmentId);
        Assert.Contains(final.Order.Ancillaries, a => a.Code == "CHG");
        Assert.Equal(paid.Order.TotalPrice.Total, final.Order.TotalPrice.Total);
        Assert.Equal(4, final.Order.Documents.Count);
    }
}
