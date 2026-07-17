using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AeroDesk.Core.Connections;
using AeroDesk.Core.Settings;

namespace AeroDesk.Core.Operations;

/// <summary>
/// Departure control over the AeroBus <c>/operations</c> surface. Auth is the
/// shared <see cref="KeycloakAuthClient"/> agent session — the SAME login that
/// drives retailing; the owner (the connection) signs in once and this service
/// fetches a fresh access token per request (refresh comes free), so
/// long-running gate sessions don't 401 mid-shift. Maps the AeroBus flight +
/// check-in JSON onto the app's <see cref="DepartureFlight"/> /
/// <see cref="ManifestPassenger"/> records.
/// </summary>
public sealed class AeroBusOperationsService : IOperationsService
{
    private static readonly JsonSerializerOptions Wire = JsonDefaults.Wire;

    private readonly HttpClient _http;
    private readonly KeycloakAuthClient _auth;
    private readonly DfConnectionDescriptor _descriptor;

    public AeroBusOperationsService(DfConnectionDescriptor descriptor, KeycloakAuthClient auth, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Url))
            throw new ArgumentException("Descriptor must have a Url.", nameof(descriptor));
        _descriptor = descriptor;
        _auth = auth;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(descriptor.Url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public string Name => $"{_descriptor.Name} (AeroBus DCS, {_descriptor.Url})";
    public bool IsConnected { get; private set; }
    public string DefaultStation => "DXB";

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        using var health = await _http.GetAsync("health", ct).ConfigureAwait(false);
        if (!health.IsSuccessStatusCode)
            throw new InvalidOperationException($"AeroBus health check failed ({(int)health.StatusCode}).");
        // Prove the shared session yields a token (sign-in happened at the connection).
        _ = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        IsConnected = true;
    }

    public async Task<IReadOnlyList<DepartureFlight>> ListDeparturesAsync(string departureStation, DateOnly date, CancellationToken ct = default)
    {
        var url = $"operations/departures?departureStation={Uri.EscapeDataString(departureStation)}&date={date:yyyy-MM-dd}";
        using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        return doc.RootElement.EnumerateArray().Select(ReadFlight).ToList();
    }

    public async Task<DepartureFlight?> GetFlightAsync(Guid flightId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"operations/flights/{flightId}", null, ct, allowNotFound: true).ConfigureAwait(false);
        return doc is null ? null : ReadFlight(doc.RootElement);
    }

    public async Task<DepartureFlight> ChangeStatusAsync(Guid flightId, string action, CancellationToken ct = default)
    {
        var url = action == FlightOpAction.Depart
            ? $"operations/flights/{flightId}/depart"
            : $"operations/flights/{flightId}/status";
        var body = action == FlightOpAction.Depart ? null : (object?)new { action };
        using var doc = await SendAsync(HttpMethod.Post, url, body, ct).ConfigureAwait(false);
        return ReadFlight(doc!.RootElement);
    }

    public async Task<IReadOnlyList<ManifestPassenger>> GetManifestAsync(Guid flightId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"operations/flights/{flightId}/manifest", null, ct).ConfigureAwait(false);
        return doc!.RootElement.EnumerateArray().Select(ReadPax).ToList();
    }

    public async Task<ManifestPassenger> CheckInAsync(Guid flightId, Guid passengerId, int? seatRow, string? seatColumn, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, "operations/checkin",
            new { flightId, passengerId, seatRow, seatColumn }, ct).ConfigureAwait(false);
        return ReadPax(doc!.RootElement);
    }

    public async Task<ManifestPassenger> BoardAsync(Guid flightId, Guid passengerId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, "operations/board", new { flightId, passengerId }, ct).ConfigureAwait(false);
        return ReadPax(doc!.RootElement);
    }

    public async Task<int> BoardAllAsync(Guid flightId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"operations/flights/{flightId}/board-all", null, ct).ConfigureAwait(false);
        return doc!.RootElement.TryGetProperty("boarded", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
    }

    public ValueTask DisposeAsync()
    {
        // The auth client is owned by the connection (shared with retailing) —
        // only dispose what this service created.
        _http.Dispose();
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    // ---- transport ----

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        var token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Wire), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AeroBus operations call failed ({(int)response.StatusCode}): {ExtractError(payload)}");
        return string.IsNullOrWhiteSpace(payload) ? JsonDocument.Parse("{}") : JsonDocument.Parse(payload);
    }

    // ---- mapping (AeroBus JSON → app records) ----

    private static DepartureFlight ReadFlight(JsonElement e)
    {
        int? Counter(string name) =>
            e.TryGetProperty("counters", out var c) && c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        return new DepartureFlight(
            Id: Gid(e, "id"),
            FlightNumber: Str(e, "flightNumber"),
            DepartureStation: Str(e, "departureStation") ?? "",
            ArrivalStation: Str(e, "arrivalStation") ?? "",
            DepartureLocal: Date(e, "departureDateTimeLocal"),
            ArrivalLocal: Date(e, "arrivalDateTimeLocal"),
            Status: Str(e, "status") ?? FlightOpStatus.Scheduled,
            Capacity: Counter("capacity"),
            Sold: Counter("sold"),
            Available: Counter("available"));
    }

    private static ManifestPassenger ReadPax(JsonElement e) => new(
        PassengerId: Gid(e, "passengerId"),
        FirstName: Str(e, "firstName") ?? "",
        LastName: Str(e, "lastName") ?? "",
        PaxType: Str(e, "paxType") ?? "",
        Status: Str(e, "status") ?? PaxOpStatus.Booked,
        SeatRow: Int(e, "seatRow"),
        SeatColumn: Str(e, "seatColumn"),
        BoardingSequence: Int(e, "boardingSequence"));

    private static Guid Gid(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && System.Guid.TryParse(v.GetString(), out var g) ? g : System.Guid.Empty;
    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static DateTime Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : default;

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString()!;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? "no detail" : body;
    }
}
