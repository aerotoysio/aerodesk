using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// Deterministic seat maps and ancillary catalogues per segment — the same
/// segment always renders the same availability, so the in-memory service, the
/// DocumentForge-backed service, and tests agree. Availability is a stable hash
/// of (segmentId, seat); roughly 70% of seats are free.
/// </summary>
public static class SeatMapFactory
{
    private sealed record Layout(int Rows, string[] Columns);

    // Columns include "" for aisle gaps so the UI can render the cabin shape directly.
    private static Layout LayoutFor(string equipment) => equipment switch
    {
        "A320" or "A321" => new Layout(28, ["A", "B", "C", "", "D", "E", "F"]),
        "B789" => new Layout(34, ["A", "B", "C", "", "D", "E", "F", "", "G", "H", "K"]),
        "A351" => new Layout(36, ["A", "B", "C", "", "D", "E", "F", "", "G", "H", "K"]),
        _ => new Layout(26, ["A", "B", "C", "", "D", "E", "F"]),
    };

    public static SeatMap Build(FlightSegment segment)
    {
        var layout = LayoutFor(segment.Equipment);
        var seats = new List<SeatOption>();
        var frontRows = Math.Max(3, layout.Rows / 6);
        var exitRow = layout.Rows / 2;

        for (var row = 1; row <= layout.Rows; row++)
        {
            foreach (var col in layout.Columns)
            {
                if (col.Length == 0) continue; // aisle
                var zone = row <= frontRows ? "Front" : row >= layout.Rows - 3 ? "Rear" : "Standard";
                var isExit = row == exitRow;
                seats.Add(new SeatOption(
                    SeatNumber: $"{row}{col}",
                    Row: row,
                    Column: col,
                    Available: IsAvailable(segment.SegmentId, row, col),
                    ExitRow: isExit,
                    Zone: zone,
                    Price: PriceFor(zone, isExit, segment)));
            }
        }
        return new SeatMap(segment.SegmentId, segment.Equipment, layout.Columns, seats);
    }

    public static IReadOnlyList<AncillaryOption> Catalog(FlightSegment segment)
    {
        var longHaul = (segment.ArrivalUtc - segment.DepartureUtc).TotalHours >= 5;
        var list = new List<AncillaryOption>
        {
            new("XBAG", "Extra bag (23kg)", "One additional checked bag up to 23kg.",
                new PriceDetail(longHaul ? 60m : 40m, longHaul ? 7.2m : 4.8m, FareModel.Currency)),
            new("MEAL", "Premium meal", "Chef-selected hot meal with drink.",
                new PriceDetail(longHaul ? 24m : 15m, 0m, FareModel.Currency)),
        };
        if (longHaul)
            list.Add(new("WIFI", "Wi-Fi pass", "Full-flight streaming-grade Wi-Fi.",
                new PriceDetail(18m, 0m, FareModel.Currency)));
        return list;
    }

    /// <summary>Seat price by zone: front +, exit rows ++, rear discounted, standard cheap.</summary>
    private static PriceDetail PriceFor(string zone, bool exitRow, FlightSegment segment)
    {
        var longHaul = (segment.ArrivalUtc - segment.DepartureUtc).TotalHours >= 5;
        var baseAmount = zone switch
        {
            "Front" => longHaul ? 45m : 25m,
            "Rear" => longHaul ? 12m : 8m,
            _ => longHaul ? 20m : 12m,
        };
        if (exitRow) baseAmount += longHaul ? 30m : 15m;
        return new PriceDetail(baseAmount, Math.Round(baseAmount * 0.12m, 2), FareModel.Currency);
    }

    /// <summary>Stable pseudo-random availability — FNV-1a over (segmentId,row,col).</summary>
    private static bool IsAvailable(string segmentId, int row, string col)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in segmentId) { hash ^= c; hash *= 16777619; }
            hash ^= (uint)row; hash *= 16777619;
            foreach (var c in col) { hash ^= c; hash *= 16777619; }
            return hash % 10 >= 3; // ~70% free
        }
    }
}
