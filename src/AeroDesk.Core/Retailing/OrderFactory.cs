using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

/// <summary>
/// Turns an accepted offer + passengers into an Order, and issues simulated
/// accountable documents. Shared by the in-memory and DocumentForge services so
/// order semantics never drift between backends.
/// </summary>
public static class OrderFactory
{
    public static void ValidatePassengersMatchOffer(Offer offer, IReadOnlyList<Passenger> passengers)
    {
        foreach (var item in offer.Items)
        {
            var supplied = passengers.Count(p => p.Type == item.PassengerType);
            if (supplied != item.PassengerCount)
                throw new ArgumentException(
                    $"Offer expects {item.PassengerCount} x {item.PassengerType}, got {supplied}.");
        }
        foreach (var p in passengers)
        {
            if (string.IsNullOrWhiteSpace(p.GivenName) || string.IsNullOrWhiteSpace(p.Surname))
                throw new ArgumentException($"Passenger '{p.PassengerId}' needs a given name and surname.");
        }
    }

    public static Order Create(Offer offer, IReadOnlyList<Passenger> passengers, string orderId, DateTime nowUtc, AgentContext? agent = null)
    {
        ValidatePassengersMatchOffer(offer, passengers);
        return new Order
        {
            OrderId = orderId,
            RecordLocator = NewRecordLocator(),
            Owner = offer.Owner,
            Status = OrderStatus.PendingPayment,
            Segments = offer.Segments,
            Items = offer.Items.Select(i => new OrderItem(
                OrderItemId: Guid.NewGuid().ToString("N"),
                SourceOfferItemId: i.OfferItemId,
                PassengerType: i.PassengerType,
                PassengerIds: passengers.Where(p => p.Type == i.PassengerType).Select(p => p.PassengerId).ToList(),
                SegmentIds: i.SegmentIds,
                Fare: i.Fare,
                PricePerPassenger: i.PricePerPassenger)).ToList(),
            Passengers = passengers,
            TimeLimits = [new TimeLimit(TimeLimit.PaymentDeadline, nowUtc.AddHours(24)),
                          new TimeLimit(TimeLimit.PriceGuarantee, nowUtc.AddHours(24))],
            Currency = offer.Currency,
            CreatedAtUtc = nowUtc,
            CreatedBy = agent,
        };
    }

    /// <summary>Simulated accountable documents: an e-ticket per seated passenger, an EMD per infant.</summary>
    public static List<IssuedDocument> IssueDocuments(Order order, DateTime nowUtc)
    {
        var docs = new List<IssuedDocument>();
        var serial = Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L);
        foreach (var pax in order.Passengers)
        {
            docs.Add(new IssuedDocument(
                DocumentNumber: $"999-{serial++}",
                Kind: pax.Type == Ptc.INF ? "EMD" : "ETKT",
                PassengerId: pax.PassengerId,
                OrderId: order.OrderId,
                IssuedAtUtc: nowUtc));
        }
        return docs;
    }

    public static string NewOrderId(DateTime nowUtc) =>
        $"AT-{nowUtc:yyyyMMdd}-{Random.Shared.Next(10000, 99999)}";

    public static string NewRecordLocator()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // no I/O to avoid 1/0 confusion
        return string.Create(6, alphabet, static (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[Random.Shared.Next(a.Length)];
        });
    }

    /// <summary>
    /// The order-level change fee for a flight change: the strictest fare wins.
    /// Throws when any fare in the order forbids changes (e.g. Basic); returns
    /// the total fee (per-passenger fee × seated passengers), which may be zero.
    /// </summary>
    public static PriceDetail GetFlightChangeFee(Order order)
    {
        var fares = order.Items.Select(i => i.Fare).ToList();
        var blocking = fares.FirstOrDefault(f => !f.Changeable);
        if (blocking is not null)
            throw new InvalidOperationException(
                $"The {blocking.FareFamily} fare does not permit flight changes — cancel and rebook instead.");

        var perPax = fares.Max(f => f.ChangeFee ?? 0m);
        var seated = order.Passengers.Count(p => p.Type != Ptc.INF);
        return new PriceDetail(perPax * seated, 0m, order.Currency);
    }

    /// <summary>
    /// Swap one flown segment for another (rebook). Seats on the old flight are
    /// released (different aircraft/date); bag/meal ancillaries follow the new
    /// flight; a non-zero change fee lands as an order-level CHG service line.
    /// </summary>
    public static Order ApplyFlightChange(Order order, string oldSegmentId, FlightSegment newSegment, PriceDetail changeFee)
    {
        if (order.Status == OrderStatus.Cancelled)
            throw new InvalidOperationException($"Order {order.OrderId} is cancelled.");
        if (!order.Segments.Any(s => s.SegmentId == oldSegmentId))
            throw new ArgumentException($"Segment '{oldSegmentId}' is not part of order {order.OrderId}.");

        var ancillaries = order.Ancillaries
            .Select(a => a.SegmentId == oldSegmentId ? a with { SegmentId = newSegment.SegmentId } : a)
            .ToList();
        if (changeFee.Total > 0)
            ancillaries.Add(new Ancillary(
                Guid.NewGuid().ToString("N"), "CHG", "Flight change fee",
                newSegment.SegmentId, "ALL", changeFee));

        return order with
        {
            Segments = order.Segments.Select(s => s.SegmentId == oldSegmentId ? newSegment : s).ToList(),
            Items = order.Items.Select(i => i with
            {
                SegmentIds = i.SegmentIds.Select(id => id == oldSegmentId ? newSegment.SegmentId : id).ToList(),
            }).ToList(),
            Seats = order.Seats.Where(s => s.SegmentId != oldSegmentId).ToList(),
            Ancillaries = ancillaries,
        };
    }

    /// <summary>Apply a servicing change (adds/removals) to an order snapshot.</summary>
    public static Order ApplyChange(Order order, OrderChange change)
    {
        if (order.Status == OrderStatus.Cancelled)
            throw new InvalidOperationException($"Order {order.OrderId} is cancelled.");

        var ancillaries = order.Ancillaries
            .Where(a => !change.RemoveServiceIds.Contains(a.ServiceId))
            .Concat(change.AddAncillaries)
            .ToList();
        var seats = order.Seats
            .Where(s => !change.AddSeats.Any(n => n.SegmentId == s.SegmentId && n.PassengerId == s.PassengerId))
            .Concat(change.AddSeats)
            .ToList();

        return order with { Ancillaries = ancillaries, Seats = seats };
    }
}
