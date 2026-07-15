namespace AeroDesk.Core.Operations;

/// <summary>
/// Offline departure-control demo: a seeded station's departures with manifests,
/// with the full flight-status and check-in/boarding lifecycle worked in memory.
/// Lets the DCS loop (departures → flight → check in → board → depart) run with no
/// backend, exactly as <see cref="Retailing.InMemoryRetailingService"/> does for
/// retailing.
/// </summary>
public sealed class InMemoryOperationsService : IOperationsService
{
    private sealed class Pax
    {
        public Guid Id;
        public string First = "", Last = "", Type = "ADT", Status = PaxOpStatus.Booked;
        public int? SeatRow;
        public string? SeatColumn;
        public int? Sequence;
    }

    private sealed class Flt
    {
        public Guid Id = Guid.NewGuid();
        public string Number = "", Dep = "", Arr = "";
        public DateTime DepLocal, ArrLocal;
        public string Status = FlightOpStatus.Scheduled;
        public int Capacity;
        public readonly List<Pax> Manifest = [];
        public int Sold => Manifest.Count;
    }

    private readonly object _gate = new();
    private readonly List<Flt> _flights = [];

    public string Name => "Offline demo (in-memory)";
    public bool IsConnected { get; private set; }
    public string DefaultStation => "DXB";

    public Task ConnectAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_flights.Count == 0) Seed();
            IsConnected = true;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DepartureFlight>> ListDeparturesAsync(string departureStation, DateOnly date, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<DepartureFlight> list = _flights
                .Where(f => string.Equals(f.Dep, departureStation, StringComparison.OrdinalIgnoreCase)
                            && DateOnly.FromDateTime(f.DepLocal) == date)
                .OrderBy(f => f.DepLocal)
                .Select(Project)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<DepartureFlight?> GetFlightAsync(Guid flightId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Find(flightId) is { } f ? Project(f) : null);
    }

    public Task<DepartureFlight> ChangeStatusAsync(Guid flightId, string action, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var f = Find(flightId) ?? throw new InvalidOperationException("Flight not found.");
            var to = Transition(f.Status, action)
                     ?? throw new InvalidOperationException($"Cannot '{action}' a flight in '{f.Status}' status.");
            f.Status = to;
            return Task.FromResult(Project(f));
        }
    }

    public Task<IReadOnlyList<ManifestPassenger>> GetManifestAsync(Guid flightId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var f = Find(flightId);
            IReadOnlyList<ManifestPassenger> list = f is null
                ? []
                : f.Manifest.OrderBy(p => p.Last).ThenBy(p => p.First).Select(ProjectPax).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<ManifestPassenger> CheckInAsync(Guid flightId, Guid passengerId, int? seatRow, string? seatColumn, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var p = FindPax(flightId, passengerId) ?? throw new InvalidOperationException("Passenger not on this flight.");
            if (p.Status == PaxOpStatus.Boarded) throw new InvalidOperationException("Passenger has already boarded.");
            p.Status = PaxOpStatus.CheckedIn;
            if (seatRow is not null) p.SeatRow = seatRow;
            if (!string.IsNullOrWhiteSpace(seatColumn)) p.SeatColumn = seatColumn;
            return Task.FromResult(ProjectPax(p));
        }
    }

    public Task<ManifestPassenger> BoardAsync(Guid flightId, Guid passengerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var f = Find(flightId) ?? throw new InvalidOperationException("Flight not found.");
            var p = f.Manifest.FirstOrDefault(x => x.Id == passengerId) ?? throw new InvalidOperationException("Passenger not on this flight.");
            if (p.Status == PaxOpStatus.Booked) throw new InvalidOperationException("Passenger must check in before boarding.");
            if (p.Status != PaxOpStatus.Boarded)
            {
                p.Status = PaxOpStatus.Boarded;
                p.Sequence = f.Manifest.Count(x => x.Sequence is not null) + 1;
            }
            return Task.FromResult(ProjectPax(p));
        }
    }

    public Task<int> BoardAllAsync(Guid flightId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var f = Find(flightId);
            if (f is null) return Task.FromResult(0);
            var boarded = 0;
            foreach (var p in f.Manifest.Where(x => x.Status == PaxOpStatus.CheckedIn))
            {
                p.Status = PaxOpStatus.Boarded;
                p.Sequence = f.Manifest.Count(x => x.Sequence is not null) + 1;
                boarded++;
            }
            return Task.FromResult(boarded);
        }
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    // ---- helpers ----

    private Flt? Find(Guid id) => _flights.FirstOrDefault(f => f.Id == id);
    private Pax? FindPax(Guid flightId, Guid paxId) => Find(flightId)?.Manifest.FirstOrDefault(p => p.Id == paxId);

    private static string? Transition(string status, string action) => (status, action) switch
    {
        (FlightOpStatus.Scheduled, FlightOpAction.StartBoarding) => FlightOpStatus.Boarding,
        (FlightOpStatus.Boarding, FlightOpAction.Depart) => FlightOpStatus.Departed,
        (FlightOpStatus.Scheduled, FlightOpAction.Cancel) => FlightOpStatus.Cancelled,
        (FlightOpStatus.Boarding, FlightOpAction.Cancel) => FlightOpStatus.Cancelled,
        _ => null,
    };

    private static DepartureFlight Project(Flt f) => new(
        f.Id, f.Number, f.Dep, f.Arr, f.DepLocal, f.ArrLocal, f.Status,
        f.Capacity, f.Sold, f.Capacity - f.Sold);

    private static ManifestPassenger ProjectPax(Pax p) => new(
        p.Id, p.First, p.Last, p.Type, p.Status, p.SeatRow, p.SeatColumn, p.Sequence);

    private void Seed()
    {
        var today = DateTime.Today;
        Flt Make(string number, string arr, int hour, int min, int flightMinutes, int cap,
            params (string first, string last, string type)[] pax)
        {
            var dep = today.AddHours(hour).AddMinutes(min);
            var f = new Flt
            {
                Number = number, Dep = "DXB", Arr = arr,
                DepLocal = dep, ArrLocal = dep.AddMinutes(flightMinutes), Capacity = cap,
            };
            foreach (var (first, last, type) in pax)
                f.Manifest.Add(new Pax { Id = Guid.NewGuid(), First = first, Last = last, Type = type });
            return f;
        }

        _flights.Add(Make("EK12", "LHR", 8, 30, 470, 300,
            ("Ada", "Adams", "ADT"), ("Ben", "Baker", "ADT"), ("Cara", "Cole", "CHD"), ("Dan", "Doyle", "ADT")));
        _flights.Add(Make("EK21", "JFK", 10, 15, 840, 320,
            ("Erin", "Evans", "ADT"), ("Finn", "Foley", "ADT"), ("Gia", "Gray", "INF")));
        _flights.Add(Make("EK73", "SIN", 14, 45, 440, 280,
            ("Hana", "Hall", "ADT"), ("Ian", "Irwin", "ADT"), ("Joy", "Jones", "ADT"), ("Kit", "Kerr", "CHD"), ("Lee", "Lowe", "ADT")));
    }
}
