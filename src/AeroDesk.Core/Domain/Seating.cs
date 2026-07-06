namespace AeroDesk.Core.Domain;

/// <summary>One sellable seat on a segment's seat map.</summary>
public sealed record SeatOption(
    string SeatNumber,          // "12A"
    int Row,
    string Column,              // "A".."K"
    bool Available,
    bool ExitRow,
    string Zone,                // "Front" | "Standard" | "Rear"
    PriceDetail Price);

/// <summary>The cabin grid for one flight segment.</summary>
public sealed record SeatMap(
    string SegmentId,
    string Equipment,
    IReadOnlyList<string> Columns,      // includes "" for the aisle gaps, e.g. A,B,C,"",D,E,F
    IReadOnlyList<SeatOption> Seats)
{
    public IEnumerable<IGrouping<int, SeatOption>> Rows => Seats.GroupBy(s => s.Row).OrderBy(g => g.Key);
}

/// <summary>A purchasable extra offered for a segment (bag, meal, wifi…).</summary>
public sealed record AncillaryOption(
    string Code,                // "XBAG" | "MEAL" | "WIFI"
    string Name,
    string Description,
    PriceDetail Price);
