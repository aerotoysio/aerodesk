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

    private sealed record FareFamily(
        string Name, decimal Multiplier, string Baggage,
        bool Changeable, decimal? ChangeFee, bool Refundable,
        bool SeatIncluded, bool MealIncluded, bool Priority, string FareBasisSuffix);

    // Branded fares per cabin tier. Premium cabins sell service-rich brands,
    // economy tiers trade flexibility for price.
    private static readonly FareFamily[] EconomyFamilies =
    [
        new("Basic",    1.00m, "Cabin bag only", Changeable: false, ChangeFee: null, Refundable: false,
            SeatIncluded: false, MealIncluded: false, Priority: false, "BSC"),
        new("Standard", 1.20m, "1 x 23kg",       Changeable: true,  ChangeFee: 75m,  Refundable: false,
            SeatIncluded: false, MealIncluded: false, Priority: false, "STD"),
        new("Flex",     1.55m, "2 x 23kg",       Changeable: true,  ChangeFee: 0m,   Refundable: true,
            SeatIncluded: true,  MealIncluded: true,  Priority: true,  "FLX"),
    ];

    private static readonly FareFamily[] PremiumFamilies =
    [
        new("Classic", 1.00m, "2 x 32kg", Changeable: true, ChangeFee: 150m, Refundable: false,
            SeatIncluded: true, MealIncluded: true, Priority: true, "CLS"),
        new("Flex",    1.35m, "3 x 32kg", Changeable: true, ChangeFee: 0m,   Refundable: true,
            SeatIncluded: true, MealIncluded: true, Priority: true, "FLX"),
    ];

    private static FareFamily[] FamiliesFor(Cabin cabin) =>
        cabin is Cabin.Business or Cabin.First ? PremiumFamilies : EconomyFamilies;

    /// <summary>Build offers with flights sourced from the built-in schedule (in-memory service).</summary>
    public static IReadOnlyList<Offer> Build(ShopRequest request, DateTime nowUtc, Func<string> offerIdFactory)
    {
        var perLeg = request.Legs
            .Select(leg => FlightSchedule.FlightsFor(leg.Origin, leg.Destination, leg.DepartureDate, request.Cabin))
            .ToList();
        return Build(request, perLeg, nowUtc, offerIdFactory);
    }

    /// <summary>
    /// One offer per (itinerary, fare family). An itinerary is one scheduled
    /// flight per requested leg, matched by departure order across legs so the
    /// combinations stay sane (no cartesian explosion). Flight availability per
    /// leg is supplied by the caller (schedule or DocumentForge inventory).
    /// </summary>
    public static IReadOnlyList<Offer> Build(
        ShopRequest request,
        IReadOnlyList<IReadOnlyList<FlightSegment>> perLeg,
        DateTime nowUtc,
        Func<string> offerIdFactory)
    {
        if (perLeg.Any(f => f.Count == 0)) return [];

        // Deterministic itinerary pairing regardless of source ordering (SQL results are unordered).
        perLeg = perLeg.Select(f => (IReadOnlyList<FlightSegment>)f.OrderBy(s => s.DepartureUtc).ToList()).ToList();

        var itineraryCount = Math.Min(perLeg.Min(f => f.Count), MaxItinerariesPerSearch);
        var offers = new List<Offer>();

        for (var i = 0; i < itineraryCount; i++)
        {
            // i-th departure of each leg — earliest with earliest, latest with latest.
            var segments = perLeg.Select(flights => flights[Math.Min(i, flights.Count - 1)]).ToList();

            foreach (var family in FamiliesFor(request.Cabin))
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
                    Refundable: family.Refundable,
                    ChangeFee: family.ChangeFee,
                    SeatSelectionIncluded: family.SeatIncluded,
                    MealIncluded: family.MealIncluded,
                    PriorityBoarding: family.Priority),
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
