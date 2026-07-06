using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AeroDesk.Core.Connections;

public sealed record DfQueryResult(
    bool Success,
    IReadOnlyList<string> Documents,
    long AffectedCount,
    string? Message);

/// <summary>
/// Typed client for a dfdb serve node, scoped to the descriptor's database.
/// Bearer auth; documents are raw JSON strings carrying DocumentForge's
/// <c>_id</c> and <c>_etag</c> metadata fields inline.
/// </summary>
public sealed class DfHttpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;

    public DfConnectionDescriptor Descriptor { get; }
    public bool IsConnected { get; private set; }

    private string Db => Uri.EscapeDataString(Descriptor.Database);

    public DfHttpClient(DfConnectionDescriptor descriptor, string? apiKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Url))
            throw new ArgumentException("Descriptor must have a Url.", nameof(descriptor));
        Descriptor = descriptor;

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(descriptor.Url.TrimEnd('/') + "/");
        // Generous ceiling; per-call timeouts come from the caller's CancellationToken.
        _http.Timeout = TimeSpan.FromMinutes(10);
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>/health is public; /databases exercises auth so a bad key fails
    /// here rather than on the first real call.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await GetHealthAsync(ct).ConfigureAwait(false);
        await GetDatabaseNamesAsync(ct).ConfigureAwait(false);
        IsConnected = true;
    }

    public async Task<(bool Healthy, string Status, string? Version)> GetHealthAsync(CancellationToken ct = default)
    {
        // /health intentionally returns 503 when degraded — read the body either way.
        using var response = await _http.GetAsync("health", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.ServiceUnavailable)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (
            response.IsSuccessStatusCode,
            root.TryGetProperty("status", out var s) ? s.GetString() ?? "ok" : "ok",
            root.TryGetProperty("version", out var v) ? v.GetString() : null);
    }

    public async Task<IReadOnlyList<string>> GetDatabaseNamesAsync(CancellationToken ct = default)
    {
        var body = await GetRawAsync("databases", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("databases", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var d in arr.EnumerateArray())
                if (d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    names.Add(n.GetString()!);
        return names;
    }

    public async Task CreateDatabaseAsync(string name, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "databases", new { name, createIfMissing = true }, Json, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        }
    }

    public async Task<IReadOnlyList<string>> GetCollectionNamesAsync(CancellationToken ct = default)
    {
        var body = await GetRawAsync($"db/{Db}/collections", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("collections", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String)
                    names.Add(c.GetString()!);
        return names;
    }

    /// <summary>Execute SQL against the scoped database. Returned documents are
    /// raw JSON strings (with <c>_id</c>/<c>_etag</c> preserved).</summary>
    public async Task<DfQueryResult> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"db/{Db}/query", new { sql }, Json, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var documents = new List<string>();
        if (root.TryGetProperty("documents", out var docsEl) && docsEl.ValueKind == JsonValueKind.Array)
            foreach (var d in docsEl.EnumerateArray())
                documents.Add(d.GetRawText());

        return new DfQueryResult(
            Success: !root.TryGetProperty("success", out var ok) || ok.GetBoolean(),
            Documents: documents,
            AffectedCount: root.TryGetProperty("affected", out var aff) ? aff.GetInt64() : documents.Count,
            Message: root.TryGetProperty("message", out var msg) ? msg.GetString() : null);
    }

    /// <summary>Insert raw JSON; returns DocumentForge's internal _id.</summary>
    public async Task<string> InsertDocumentAsync(string collection, string json, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"db/{Db}/collections/{Uri.EscapeDataString(collection)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
    }

    /// <summary>Fetch one document by internal _id; returns its raw JSON (contains _etag), or null if absent.</summary>
    public async Task<string?> GetDocumentAsync(string collection, string id, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(
            $"db/{Db}/collections/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        return body;
    }

    /// <summary>Replace a document with optimistic concurrency (If-Match). A 412
    /// surfaces as <see cref="EtagConflictException"/> carrying both ETags.</summary>
    public async Task<string> UpdateDocumentAsync(string collection, string id, string json, string expectedEtag, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"db/{Db}/collections/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", expectedEtag);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var (expected, actual) = ExtractEtagConflict(body);
            throw new EtagConflictException(expected, actual, ExtractError(body, response.StatusCode));
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"Document '{id}' not found in '{collection}'.");
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("etag", out var etag) ? etag.GetString() ?? "" : "";
    }

    public async Task DeleteDocumentAsync(string collection, string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(
            $"db/{Db}/collections/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"Document '{id}' not found in '{collection}'.");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        }
    }

    private async Task<string> GetRawAsync(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        return body;
    }

    /// <summary>Pulls the expected/actual ETags out of a 412 body
    /// (<c>{ "expected": …, "actual": … }</c>), tolerating their absence.</summary>
    private static (string? Expected, string? Actual) ExtractEtagConflict(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("expected", out var e) ? e.GetString() : null,
                root.TryGetProperty("actual", out var a) ? a.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string ExtractError(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in new[] { "error", "message", "detail" })
                    if (doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                        return el.GetString()!;
        }
        catch (JsonException) { }
        return $"Server returned {(int)status} {status}.";
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
