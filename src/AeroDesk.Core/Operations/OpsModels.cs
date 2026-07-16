namespace AeroDesk.Core.Operations;

/// <summary>Flight operational status values (mirror AeroBus FlightStateMachine).</summary>
public static class FlightOpStatus
{
    public const string Scheduled = "Scheduled";
    public const string Boarding = "Boarding";
    public const string Departed = "Departed";
    public const string Cancelled = "Cancelled";
}

/// <summary>Flight operational actions (mirror AeroBus FlightStateMachine).</summary>
public static class FlightOpAction
{
    public const string StartBoarding = "StartBoarding";
    public const string Depart = "Depart";
    public const string Cancel = "Cancel";
}

/// <summary>Per-passenger operational status (mirror AeroBus CheckInStatus).</summary>
public static class PaxOpStatus
{
    public const string Booked = "Booked";
    public const string CheckedIn = "CheckedIn";
    public const string Boarded = "Boarded";
}

/// <summary>A flight on the departures board.</summary>
public sealed record DepartureFlight(
    Guid Id,
    string? FlightNumber,
    string DepartureStation,
    string ArrivalStation,
    DateTime DepartureLocal,
    DateTime ArrivalLocal,
    string Status,
    int? Capacity,
    int? Sold,
    int? Available);

/// <summary>One passenger on a flight's manifest.</summary>
public sealed record ManifestPassenger(
    Guid PassengerId,
    string FirstName,
    string LastName,
    string PaxType,
    string Status,
    int? SeatRow,
    string? SeatColumn,
    int? BoardingSequence)
{
    public string FullName => $"{LastName}, {FirstName}".Trim(' ', ',');
    public string? Seat => SeatRow is { } r ? $"{r}{SeatColumn}" : null;
}
