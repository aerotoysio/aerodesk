using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// Builds priced Offers from flight availability + a simple fare model — the
/// stand-in for the airline's offer engine. Fare model: hourly rate by cabin,
/// branded families (Basic / Flex), 75% child / 10% infant fares, 12% tax + a
/// fixed fee per passenger-segment.
/// </summary>
public static class OfferFactory
{
    private const int MaxItinerariesPerSearch = 4;
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(30);

    private sealed record FareFamily(string Name, decimal Multiplier, string Baggage, bool Changeable, bool Refundable, string FareBasisSuffix);

    private static readonly FareFamily[] Families =
    [
        new("Basic", 1.00m, "1 x 23kg", Changeable: false, Refundable: false, "BSC"),
        new("Flex",  1.45m, "2 x 23kg", Changeable: true,  Refundable: true,  "FLX"),
    ];

    /// <summary>
    /// One offer per (itinerary, fare family). An itinerary is one scheduled
    /// flight per requested leg, matched by departure order across legs so the
    /// combinations stay sane (no cartesian explosion).
    /// </summary>
    public static IReadOnlyList<Offer> Build(ShopRequest request, DateTime nowUtc, Func<string> offerIdFactory)
    {
        var perLeg = request.Legs
            .Select(leg => FlightSchedule.FlightsFor(leg.Origin, leg.Destination, leg.DepartureDate, request.Cabin))
            .ToList();
        if (perLeg.Any(f => f.Count == 0)) return [];

        var itineraryCount = Math.Min(perLeg.Min(f => f.Count), MaxItinerariesPerSearch);
        var offers = new List<Offer>();

        for (var i = 0; i < itineraryCount; i++)
        {
            // i-th departure of each leg — earliest with earliest, latest with latest.
            var segments = perLeg.Select(flights => flights[Math.Min(i, flights.Count - 1)]).ToList();

            foreach (var family in Families)
            {
                var items = BuildItems(request, segments, family);
                if (items.Count == 0) continue;
                offers.Add(new Offer
                {
                    OfferId = offerIdFactory(),
                    Owner = FlightSchedule.Carrier,
                    Segments = segments,
                    Items = items,
                    OfferExpiry = new TimeLimit(TimeLimit.OfferExpiry, nowUtc + OfferLifetime),
                    Currency = FareModel.Currency,
                });
            }
        }
        return offers;
    }

    private static List<OfferItem> BuildItems(ShopRequest request, IReadOnlyList<FlightSegment> segments, FareFamily family)
    {
        var segmentIds = segments.Select(s => s.SegmentId).ToList();
        var items = new List<OfferItem>();

        void Add(Ptc ptc, int count)
        {
            if (count <= 0) return;
            items.Add(new OfferItem(
                OfferItemId: Guid.NewGuid().ToString("N"),
                PassengerType: ptc,
                PassengerCount: count,
                SegmentIds: segmentIds,
                Fare: new FareComponent(
                    FareBasisCode: $"{segments[0].BookingClass}{family.FareBasisSuffix}{(ptc == Ptc.ADT ? "" : ptc.ToString())}",
                    FareFamily: family.Name,
                    BaggageAllowance: ptc == Ptc.INF ? "1 x 10kg" : family.Baggage,
                    Changeable: family.Changeable,
                    Refundable: family.Refundable),
                PricePerPassenger: FareModel.Price(segments, family.Multiplier, ptc)));
        }

        Add(Ptc.ADT, request.Adults);
        Add(Ptc.CHD, request.Children);
        Add(Ptc.INF, request.Infants);
        return items;
    }
}

/// <summary>The demo fare model. Deliberately simple; RuleForge integration can replace it later.</summary>
public static class FareModel
{
    public const string Currency = "USD";

    private const decimal TaxRate = 0.12m;
    private const decimal FeePerPassengerSegment = 22m;

    private static decimal HourlyRate(Cabin cabin) => cabin switch
    {
        Cabin.First => 420m,
        Cabin.Business => 260m,
        Cabin.PremiumEconomy => 140m,
        _ => 90m,
    };

    private static decimal PtcFactor(Ptc ptc) => ptc switch
    {
        Ptc.CHD => 0.75m,
        Ptc.INF => 0.10m,
        _ => 1.00m,
    };

    public static PriceDetail Price(IReadOnlyList<FlightSegment> segments, decimal familyMultiplier, Ptc ptc)
    {
        decimal baseAmount = 0m;
        foreach (var s in segments)
        {
            var hours = (decimal)(s.ArrivalUtc - s.DepartureUtc).TotalHours;
            baseAmount += Math.Round(hours * HourlyRate(s.Cabin) * familyMultiplier * PtcFactor(ptc), 2);
        }
        var taxes = Math.Round(baseAmount * TaxRate, 2) + FeePerPassengerSegment * segments.Count;
        return new PriceDetail(baseAmount, taxes, Currency);
    }
}
