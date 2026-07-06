using System.Text.Json;
using AeroDesk.Core.Connections;

namespace AeroDesk.Core.Settings;

/// <summary>
/// The on-disk workspace: settings.json + connections.json + secrets.json under a
/// root directory (default %AppData%\AeroDesk; injectable for tests).
/// </summary>
public sealed class AeroDeskWorkspace
{
    public string RootDirectory { get; }
    public AeroDeskSettings Settings { get; private set; }
    public List<DfConnectionDescriptor> Connections { get; }
    public SecretStore Secrets { get; }

    private string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    private string ConnectionsPath => Path.Combine(RootDirectory, "connections.json");

    public AeroDeskWorkspace(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AeroDesk");
        Directory.CreateDirectory(RootDirectory);

        Settings = LoadJson<AeroDeskSettings>(SettingsPath) ?? new AeroDeskSettings();
        Connections = LoadJson<List<DfConnectionDescriptor>>(ConnectionsPath) ?? [];
        Secrets = new SecretStore(RootDirectory);
    }

    public void SaveSettings() =>
        AtomicFile.Write(SettingsPath, JsonSerializer.Serialize(Settings, JsonDefaults.Indented));

    public void SaveConnections() =>
        AtomicFile.Write(ConnectionsPath, JsonSerializer.Serialize(Connections, JsonDefaults.Indented));

    /// <summary>Add or replace a connection; when an API key is supplied it goes into the secret store.</summary>
    public DfConnectionDescriptor UpsertConnection(DfConnectionDescriptor descriptor, string? apiKey = null)
    {
        if (!string.IsNullOrEmpty(apiKey))
        {
            var secretId = Secrets.Set(descriptor.ApiKeySecretId, apiKey);
            descriptor = descriptor with { ApiKeySecretId = secretId };
        }

        var index = Connections.FindIndex(c => c.Id == descriptor.Id);
        if (index >= 0) Connections[index] = descriptor;
        else Connections.Add(descriptor);
        SaveConnections();
        return descriptor;
    }

    public void RemoveConnection(string id)
    {
        var existing = Connections.FirstOrDefault(c => c.Id == id);
        if (existing is null) return;
        if (existing.ApiKeySecretId is { } secretId) Secrets.Delete(secretId);
        Connections.RemoveAll(c => c.Id == id);
        SaveConnections();
    }

    public string? ResolveApiKey(DfConnectionDescriptor descriptor) =>
        descriptor.ApiKeySecretId is { } id ? Secrets.TryGet(id) : null;

    // ---- Export / import (plaintext bundle — portable across machines) ----

    public void ExportBundle(string filePath)
    {
        var bundle = new SettingsBundle
        {
            ExportedAtUtc = DateTime.UtcNow,
            Settings = Settings,
            Connections = Connections,
            Secrets = Secrets.ExportPlaintext(),
        };
        AtomicFile.Write(filePath, JsonSerializer.Serialize(bundle, JsonDefaults.Indented));
    }

    public void ImportBundle(string filePath, bool replace)
    {
        var bundle = JsonSerializer.Deserialize<SettingsBundle>(File.ReadAllText(filePath), JsonDefaults.Indented)
            ?? throw new InvalidDataException("Not a valid AeroDesk settings bundle.");

        if (replace)
        {
            Settings = bundle.Settings ?? new AeroDeskSettings();
            Connections.Clear();
        }
        else if (bundle.Settings is not null)
        {
            Settings = bundle.Settings;
        }

        foreach (var conn in bundle.Connections ?? [])
        {
            var index = Connections.FindIndex(c => c.Id == conn.Id);
            if (index >= 0) Connections[index] = conn;
            else Connections.Add(conn);
        }

        Secrets.ImportPlaintext(bundle.Secrets ?? [], replaceAll: replace);
        SaveSettings();
        SaveConnections();
    }

    private static T? LoadJson<T>(string path) where T : class
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Indented);
        }
        catch { /* unreadable — fall back to defaults */ }
        return null;
    }
}

/// <summary>Plaintext export of the whole workspace (settings + connections + decrypted secrets).</summary>
public sealed class SettingsBundle
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; }
    public AeroDeskSettings? Settings { get; set; }
    public List<DfConnectionDescriptor>? Connections { get; set; }
    public Dictionary<string, string>? Secrets { get; set; }
}
