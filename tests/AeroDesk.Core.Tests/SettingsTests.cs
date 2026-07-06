using AeroDesk.Core.Connections;
using AeroDesk.Core.Settings;
using Xunit;

namespace AeroDesk.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aerodesk-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SecretStore_RoundTrips_And_Deletes()
    {
        var store = new SecretStore(_dir);
        var id = store.Set(null, "super-secret-key");

        Assert.Equal("super-secret-key", store.TryGet(id));

        // Encrypted at rest — plaintext must not appear in the file.
        var onDisk = File.ReadAllText(Path.Combine(_dir, "secrets.json"));
        Assert.DoesNotContain("super-secret-key", onDisk);

        Assert.True(store.Delete(id));
        Assert.Null(store.TryGet(id));
    }

    [Fact]
    public void SecretStore_Corrupt_Blob_Reads_As_Null()
    {
        var store = new SecretStore(_dir);
        var id = store.Set(null, "value");
        File.WriteAllText(Path.Combine(_dir, "secrets.json"), $$"""{"{{id}}":"bm90LWEtZHBhcGktYmxvYg=="}""");

        var reloaded = new SecretStore(_dir);
        Assert.Null(reloaded.TryGet(id));
    }

    [Fact]
    public void Workspace_Persists_Settings_And_Connections()
    {
        var workspace = new AeroDeskWorkspace(_dir);
        workspace.Settings.AgentName = "Sam Carter";
        workspace.Settings.DefaultCurrency = "GBP";
        workspace.SaveSettings();
        var saved = workspace.UpsertConnection(
            new DfConnectionDescriptor { Name = "Dev", Url = "http://localhost:5001", Database = "airline" },
            apiKey: "key-123");

        var reloaded = new AeroDeskWorkspace(_dir);
        Assert.Equal("Sam Carter", reloaded.Settings.AgentName);
        Assert.Equal("GBP", reloaded.Settings.DefaultCurrency);
        var conn = Assert.Single(reloaded.Connections);
        Assert.Equal("Dev", conn.Name);
        Assert.Equal("airline", conn.Database);

        // The API key lives in the secret store, not connections.json.
        var onDisk = File.ReadAllText(Path.Combine(_dir, "connections.json"));
        Assert.DoesNotContain("key-123", onDisk);
        Assert.Equal("key-123", reloaded.ResolveApiKey(conn));
        Assert.Equal(saved.ApiKeySecretId, conn.ApiKeySecretId);
    }

    [Fact]
    public void Workspace_RemoveConnection_Also_Deletes_Its_Secret()
    {
        var workspace = new AeroDeskWorkspace(_dir);
        var conn = workspace.UpsertConnection(
            new DfConnectionDescriptor { Name = "Dev", Url = "http://localhost:5001" }, apiKey: "key-xyz");

        workspace.RemoveConnection(conn.Id);

        Assert.Empty(workspace.Connections);
        Assert.Null(workspace.Secrets.TryGet(conn.ApiKeySecretId!));
    }

    [Fact]
    public void Bundle_Export_Import_RoundTrips_Including_Secrets()
    {
        var source = new AeroDeskWorkspace(Path.Combine(_dir, "source"));
        source.Settings.AgencyName = "AeroToys Travel";
        source.SaveSettings();
        source.UpsertConnection(new DfConnectionDescriptor { Name = "Dev", Url = "http://localhost:5001" }, apiKey: "bundle-key");
        var bundlePath = Path.Combine(_dir, "bundle.json");
        source.ExportBundle(bundlePath);

        // Export is an explicitly-plaintext portable bundle.
        Assert.Contains("bundle-key", File.ReadAllText(bundlePath));

        var target = new AeroDeskWorkspace(Path.Combine(_dir, "target"));
        target.ImportBundle(bundlePath, replace: true);

        Assert.Equal("AeroToys Travel", target.Settings.AgencyName);
        var conn = Assert.Single(target.Connections);
        Assert.Equal("bundle-key", target.ResolveApiKey(conn));
    }
}
