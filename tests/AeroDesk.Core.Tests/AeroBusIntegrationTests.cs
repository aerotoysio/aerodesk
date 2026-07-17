using AeroDesk.Core.Connections;
using AeroDesk.Core.Domain;
using AeroDesk.Core.Retailing;
using Xunit;

namespace AeroDesk.Core.Tests;

/// <summary>
/// End-to-end against a REAL AeroBus backbone (github.com/aerotoysio/aerobus).
/// Requires a running stack (dfdb + RuleForge + AeroBus + Keycloak) with a
/// catalogue-seeded tenant. Skipped unless AERODESK_AEROBUS_URL and
/// AERODESK_KEYCLOAK_URL are set; the agent signs in with
/// AERODESK_AEROBUS_EMAIL / AERODESK_AEROBUS_PASSWORD against realm
/// AERODESK_KEYCLOAK_REALM (default aerotoys), client aeroboard — the same
/// single Keycloak login the app uses for retailing AND departure control.
/// </summary>
public sealed class AeroBusIntegrationTests
{
    private static string? AeroBusUrl => Environment.GetEnvironmentVariable("AERODESK_AEROBUS_URL");
    private static string? KeycloakUrl => Environment.GetEnvironmentVariable("AERODESK_KEYCLOAK_URL");

    private sealed class AbFactAttribute : FactAttribute
    {
        public AbFactAttribute()
        {
            if (string.IsNullOrEmpty(AeroBusUrl) || string.IsNullOrEmpty(KeycloakUrl))
                Skip = "Set AERODESK_AEROBUS_URL + AERODESK_KEYCLOAK_URL to run AeroBus integration tests.";
        }
    }

    private static async Task<(AeroBusRetailingService Service, KeycloakAuthClient Auth)> ServiceAsync()
    {
        var auth = new KeycloakAuthClient(
            KeycloakUrl!,
            Environment.GetEnvironmentVariable("AERODESK_KEYCLOAK_REALM") ?? "aerotoys",
            "aeroboard");
        await auth.SignInAsync(
            Environment.GetEnvironmentVariable("AERODESK_AEROBUS_EMAIL") ?? "demo@demo.air",
            Environment.GetEnvironmentVariable("AERODESK_AEROBUS_PASSWORD") ?? "demo1234");

        var service = new AeroBusRetailingService(
            new DfConnectionDescriptor
            {
                Name = "it",
                Backend = RetailingBackend.AeroBus,
                Url = AeroBusUrl!,
            },
            auth);
        return (service, auth);
    }

    [AbFact]
    public async Task Shop_Book_Pay_Retrieve_Cancel_On_AeroBus()
    {
        var (svc, auth) = await ServiceAsync();
        await using var _ = svc;
        using var __ = auth;
        await svc.ConnectAsync();
        Assert.True(svc.IsConnected);
        Assert.Equal(RetailingCapabilities.None, svc.Capabilities);

        // --- Shop SYD→MEL one-way: expect the LITE/FLEX/FLEXPLUS ladder ---
        var travel = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(12));
        var offers = await svc.SearchOffersAsync(new ShopRequest
        {
            Legs = [new JourneyLeg("SYD", "MEL", travel)],
            Adults = 1,
        });
        Assert.NotEmpty(offers);
        var families = offers.Select(o => o.FareFamily).Distinct().ToList();
        Assert.Contains("Lite", families);
        Assert.Contains("Flex", families);
        Assert.Contains("Flex Plus", families);
        Assert.All(offers, o =>
        {
            Assert.True(o.TotalPrice.Total > 0);
            Assert.NotEmpty(o.Segments);
            Assert.Equal("SYD", o.Segments[0].Origin);
            Assert.Equal("MEL", o.Segments[^1].Destination);
        });

        // Return/multi-city are one order per O&D on AeroBus.
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.SearchOffersAsync(new ShopRequest
        {
            Legs = [new JourneyLeg("SYD", "MEL", travel), new JourneyLeg("MEL", "SYD", travel.AddDays(3))],
            TripType = TripType.Return,
            Adults = 1,
        }));

        // --- Deferred create: a local draft on hold, nothing booked yet ---
        var flex = offers.First(o => o.FareFamily == "Flex");
        var draft = await svc.CreateOrderAsync(flex.OfferId,
        [
            new Passenger { PassengerId = "ADT1", Type = Ptc.ADT, GivenName = "Amelia", Surname = "Earhart", Title = "Ms", Email = "a@example.com" },
        ]);
        Assert.Equal(OrderStatus.PendingPayment, draft.Order.Status);
        Assert.StartsWith("DRAFT-", draft.Order.OrderId);

        // --- Pay: executes /order/create + Fulfil → Ticketed, booked in aerotoys.dfdb ---
        var paid = await svc.PayOrderAsync(draft.Order.OrderId, new PaymentToken("tok_ab_visa", "4242"), draft.Etag);
        Assert.Equal(OrderStatus.Ticketed, paid.Order.Status);
        Assert.False(paid.Order.OrderId.StartsWith("DRAFT-"));
        Assert.NotEmpty(paid.Etag); // AeroBus ConcurrencyId

        // --- Retrieve by the public AeroBus OrderId ---
        var retrieved = await svc.GetOrderAsync(paid.Order.OrderId);
        Assert.NotNull(retrieved);
        Assert.Equal(OrderStatus.Ticketed, retrieved!.Order.Status);
        Assert.Contains(retrieved.Order.Passengers, p => p.Surname == "Earhart");

        // --- Session listing shows it ---
        var list = await svc.ListOrdersAsync();
        Assert.Contains(list, o => o.Order.OrderId == paid.Order.OrderId);

        // --- Stale etag on cancel → conflict; fresh etag cancels + releases inventory ---
        await Assert.ThrowsAsync<EtagConflictException>(() =>
            svc.CancelOrderAsync(paid.Order.OrderId, "stale-etag"));
        var cancelled = await svc.CancelOrderAsync(paid.Order.OrderId, retrieved.Etag);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Order.Status);

        // --- Capability-gated servicing throws clean NotSupported ---
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            svc.GetSeatMapAsync(flex.Segments[0]));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            svc.ChangeFlightAsync(paid.Order.OrderId, flex.Segments[0].SegmentId, flex.Segments[0], cancelled.Etag));
    }
}
