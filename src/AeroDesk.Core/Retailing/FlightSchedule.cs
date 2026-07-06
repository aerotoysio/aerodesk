using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// The demo airline's published schedule: a fixed route table over 8 hubs for
/// carrier "AT" (AeroToys Air). Deterministic — the same date always yields the
/// same flights — so the in-memory service, the DocumentForge seed, and tests
/// all agree. Stands in for real inventory until a real OMS exists.
/// </summary>
public static class FlightSchedule
{
    public const string Carrier = "AT";

    public static readonly IReadOnlyList<string> Airports =
        ["JFK", "LAX", "LHR", "CDG", "FRA", "DXB", "SIN", "HND"];

    /// <summary>(flightNumber, origin, destination, departure hour UTC, duration).</summary>
    public sealed record Route(string FlightNumber, string Origin, string Destination, int DepartureHourUtc, double DurationHours, string Equipment);

    // Each direction listed explicitly; 2-3 departures/day on trunk routes.
    public static readonly IReadOnlyList<Route> Routes =
    [
        new("AT100", "JFK", "LHR", 8,  7.0, "B789"),
        new("AT102", "JFK", "LHR", 18, 7.0, "A351"),
        new("AT101", "LHR", "JFK", 11, 8.0, "B789"),
        new("AT103", "LHR", "JFK", 16, 8.0, "A351"),
        new("AT110", "JFK", "CDG", 9,  7.3, "B789"),
        new("AT111", "CDG", "JFK", 12, 8.3, "B789"),
        new("AT120", "JFK", "LAX", 7,  6.2, "A321"),
        new("AT122", "JFK", "LAX", 15, 6.2, "A321"),
        new("AT121", "LAX", "JFK", 8,  5.6, "A321"),
        new("AT123", "LAX", "JFK", 16, 5.6, "A321"),
        new("AT200", "LHR", "CDG", 7,  1.3, "A320"),
        new("AT202", "LHR", "CDG", 13, 1.3, "A320"),
        new("AT204", "LHR", "CDG", 19, 1.3, "A320"),
        new("AT201", "CDG", "LHR", 9,  1.3, "A320"),
        new("AT203", "CDG", "LHR", 15, 1.3, "A320"),
        new("AT205", "CDG", "LHR", 21, 1.3, "A320"),
        new("AT210", "LHR", "FRA", 8,  1.7, "A320"),
        new("AT211", "FRA", "LHR", 11, 1.8, "A320"),
        new("AT220", "LHR", "DXB", 21, 6.8, "A351"),
        new("AT221", "DXB", "LHR", 2,  7.6, "A351"),
        new("AT230", "FRA", "SIN", 22, 12.1, "B789"),
        new("AT231", "SIN", "FRA", 23, 13.0, "B789"),
        new("AT240", "DXB", "SIN", 10, 7.4, "B789"),
        new("AT241", "SIN", "DXB", 18, 7.5, "B789"),
        new("AT250", "SIN", "HND", 8,  7.0, "B789"),
        new("AT251", "HND", "SIN", 16, 7.5, "B789"),
        new("AT260", "LAX", "HND", 11, 11.8, "A351"),
        new("AT261", "HND", "LAX", 17, 9.8, "A351"),
    ];

    /// <summary>All scheduled flights from origin to destination departing on the given UTC date.</summary>
    public static IReadOnlyList<FlightSegment> FlightsFor(string origin, string destination, DateOnly date, Cabin cabin)
    {
        var result = new List<FlightSegment>();
        foreach (var r in Routes)
        {
            if (!r.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase) ||
                !r.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase))
                continue;

            var dep = date.ToDateTime(new TimeOnly(r.DepartureHourUtc, 0), DateTimeKind.Utc);
            result.Add(new FlightSegment(
                SegmentId: $"{r.FlightNumber}-{date:yyyyMMdd}",
                Carrier: Carrier,
                FlightNumber: r.FlightNumber,
                Origin: r.Origin.ToUpperInvariant(),
                Destination: r.Destination.ToUpperInvariant(),
                DepartureUtc: dep,
                ArrivalUtc: dep.AddHours(r.DurationHours),
                Equipment: r.Equipment,
                Cabin: cabin,
                BookingClass: BookingClassFor(cabin)));
        }
        return result;
    }

    private static string BookingClassFor(Cabin cabin) => cabin switch
    {
        Cabin.First => "F",
        Cabin.Business => "J",
        Cabin.PremiumEconomy => "W",
        _ => "Y",
    };
}
