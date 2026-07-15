namespace AeroDesk.Core.Operations;

/// <summary>
/// The departure-control (DCS) seam the app binds to — the operational analogue of
/// <see cref="Retailing.IRetailingService"/>. Backed by AeroBus's <c>/operations</c>
/// surface (Keycloak-authenticated) or an in-memory demo for offline use.
///
/// A connection can offer retailing, departure control, or both; the workbench
/// shows the nav sections a backend actually supports.
/// </summary>
public interface IOperationsService : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }

    /// <summary>A sensible default departure station to pre-fill the board with.</summary>
    string DefaultStation { get; }

    Task ConnectAsync(CancellationToken ct = default);

    // ---- Departures board ----
    Task<IReadOnlyList<DepartureFlight>> ListDeparturesAsync(string departureStation, DateOnly date, CancellationToken ct = default);
    Task<DepartureFlight?> GetFlightAsync(Guid flightId, CancellationToken ct = default);

    // ---- Flight status ----
    /// <summary>Advance a flight through the status lifecycle (StartBoarding / Depart / Cancel).</summary>
    Task<DepartureFlight> ChangeStatusAsync(Guid flightId, string action, CancellationToken ct = default);

    // ---- Manifest + boarding ----
    Task<IReadOnlyList<ManifestPassenger>> GetManifestAsync(Guid flightId, CancellationToken ct = default);
    Task<ManifestPassenger> CheckInAsync(Guid flightId, Guid passengerId, int? seatRow, string? seatColumn, CancellationToken ct = default);
    Task<ManifestPassenger> BoardAsync(Guid flightId, Guid passengerId, CancellationToken ct = default);
    Task<int> BoardAllAsync(Guid flightId, CancellationToken ct = default);
}
