namespace AeroDesk.Core.Connections;

public enum RetailingBackend { DocumentForge, AeroBus }

/// <summary>
/// Persisted metadata for a retailing connection (a DocumentForge node or an
/// AeroBus backbone). Secrets (API key / password) live in the DPAPI
/// <see cref="Settings.SecretStore"/>; only their id is stored here.
/// </summary>
public sealed record DfConnectionDescriptor
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";

    public RetailingBackend Backend { get; init; } = RetailingBackend.DocumentForge;

    /// <summary>Base URL — dfdb serve node (e.g. http://localhost:5001) or AeroBus (http://localhost:5080).</summary>
    public string Url { get; init; } = "";

    /// <summary>DocumentForge only: database holding the airline collections.</summary>
    public string Database { get; init; } = "airline";

    /// <summary>AeroBus only: company slug for agent login (e.g. "aerotoys").</summary>
    public string CompanySlug { get; init; } = "aerotoys";

    /// <summary>AeroBus only: agent login email.</summary>
    public string Email { get; init; } = "";

    /// <summary>Secret-store id of the DF API key / AeroBus password. Null for insecure dev nodes.</summary>
    public string? ApiKeySecretId { get; init; }

    // ---- AeroBus departure control (DCS) auth: Keycloak staff login ----
    // The operational surface authenticates the agent against the same Keycloak
    // realm AeroBus validates (direct access grant), so board/depart actions carry
    // per-agent identity. Optional — populated only for AeroBus connections that
    // use departure control.

    /// <summary>Keycloak base URL, e.g. http://localhost:8080.</summary>
    public string KeycloakAuthority { get; init; } = "";

    /// <summary>Keycloak realm, e.g. "aerotoys".</summary>
    public string KeycloakRealm { get; init; } = "aerotoys";

    /// <summary>Public Keycloak client id with direct-access-grants enabled.</summary>
    public string KeycloakClientId { get; init; } = "aeroboard";
}
