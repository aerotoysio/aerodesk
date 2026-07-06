namespace AeroDesk.Core.Connections;

/// <summary>
/// Persisted metadata for a DocumentForge connection. The API key itself lives in
/// the DPAPI <see cref="Settings.SecretStore"/>; only its id is stored here.
/// </summary>
public sealed record DfConnectionDescriptor
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";

    /// <summary>Base URL of the dfdb serve node, e.g. http://localhost:5001.</summary>
    public string Url { get; init; } = "";

    /// <summary>Database holding the airline collections.</summary>
    public string Database { get; init; } = "airline";

    /// <summary>Secret-store id of the API key, or null for insecure dev nodes.</summary>
    public string? ApiKeySecretId { get; init; }
}
