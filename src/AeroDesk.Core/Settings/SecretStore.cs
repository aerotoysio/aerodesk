using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AeroDesk.Core.Settings;

/// <summary>
/// DPAPI-backed secret store (per Windows user). Secrets are encrypted at rest in
/// secrets.json; corrupt or foreign blobs decrypt to null rather than throwing.
/// </summary>
public sealed class SecretStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _blobs; // id -> base64(DPAPI blob)

    public SecretStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "secrets.json");
        _blobs = Load(_path);
    }

    public string Set(string? id, string plaintext)
    {
        id ??= Guid.NewGuid().ToString("N");
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        _blobs[id] = Convert.ToBase64String(blob);
        Save();
        return id;
    }

    public string? TryGet(string id)
    {
        if (!_blobs.TryGetValue(id, out var b64)) return null;
        try
        {
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            // Copied from another user/machine, or corrupted — treat as absent.
            return null;
        }
    }

    public bool Delete(string id)
    {
        var removed = _blobs.Remove(id);
        if (removed) Save();
        return removed;
    }

    /// <summary>Decrypt everything — only for the explicit settings-bundle export.</summary>
    public Dictionary<string, string> ExportPlaintext()
    {
        var result = new Dictionary<string, string>();
        foreach (var id in _blobs.Keys)
        {
            if (TryGet(id) is { } value) result[id] = value;
        }
        return result;
    }

    public void ImportPlaintext(IReadOnlyDictionary<string, string> secrets, bool replaceAll)
    {
        if (replaceAll) _blobs.Clear();
        foreach (var (id, value) in secrets)
        {
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            _blobs[id] = Convert.ToBase64String(blob);
        }
        Save();
    }

    private void Save() => AtomicFile.Write(_path, JsonSerializer.Serialize(_blobs, JsonDefaults.Indented));

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch { /* unreadable store — start fresh rather than block startup */ }
        return [];
    }
}
