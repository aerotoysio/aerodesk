namespace AeroDesk.Core.Settings;

/// <summary>User-level app settings persisted as settings.json under %AppData%\AeroDesk.</summary>
public sealed class AeroDeskSettings
{
    /// <summary>Default currency for the demo fare model.</summary>
    public string DefaultCurrency { get; set; } = "USD";

    /// <summary>Reconnect the last DocumentForge connection on startup.</summary>
    public bool ReconnectOnStartup { get; set; } = true;

    /// <summary>Query/HTTP timeout for DocumentForge calls.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Local agent profile (demo — no real identity provider).</summary>
    public string AgentName { get; set; } = "";
    public string AgencyName { get; set; } = "";
    public string AgencyIata { get; set; } = "";
}
