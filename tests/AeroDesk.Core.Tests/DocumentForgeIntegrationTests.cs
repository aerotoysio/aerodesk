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
}
