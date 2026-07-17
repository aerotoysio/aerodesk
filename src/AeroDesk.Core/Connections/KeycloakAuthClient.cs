using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroDesk.Core.Connections;

/// <summary>
/// Minimal Keycloak OIDC client for a desktop staff login: the resource-owner
/// password (direct access grant) flow against
/// <c>{authority}/realms/{realm}/protocol/openid-connect/token</c>. Holds the
/// access + refresh tokens for the session and refreshes the access token on
/// demand. This is the auth the AeroBus departure-control surface requires so each
/// board/depart carries the agent's identity.
///
/// (aerodesk's legacy retailing login — <c>POST /admin/users/{slug}/authenticate</c>
/// — is gone from AeroBus; new AeroBus auth is Keycloak or an ab_ API key. Retailing
/// will migrate to this client too; for now it powers departure control.)
/// </summary>
public sealed class KeycloakAuthClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _tokenEndpoint;
    private readonly string _clientId;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessExpiry = DateTimeOffset.MinValue;
    private DateTimeOffset _refreshExpiry = DateTimeOffset.MinValue;

    public KeycloakAuthClient(string authority, string realm, string clientId, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(authority)) throw new ArgumentException("Keycloak authority is required.", nameof(authority));
        if (string.IsNullOrWhiteSpace(realm)) throw new ArgumentException("Keycloak realm is required.", nameof(realm));
        _clientId = string.IsNullOrWhiteSpace(clientId) ? "aeroboard" : clientId;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _tokenEndpoint = $"{authority.TrimEnd('/')}/realms/{Uri.EscapeDataString(realm)}/protocol/openid-connect/token";
    }

    /// <summary>Sign in with agent credentials; stores the tokens for the session.</summary>
    public async Task SignInAsync(string email, string password, CancellationToken ct = default)
    {
        await RequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _clientId,
            ["username"] = email,
            ["password"] = password,
            // "organization" is a dynamic Keycloak scope: without requesting it the
            // token carries no org-membership claim, and AeroBus then cannot resolve
            // the agent's companyId (tenant routing + org-scoped permissions fail).
            ["scope"] = "openid organization",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A valid access token, refreshing it (or re-throwing) as needed. Callers
    /// put the returned value in an <c>Authorization: Bearer</c> header.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiry.AddSeconds(-30))
            return _accessToken;

        if (_refreshToken is not null && DateTimeOffset.UtcNow < _refreshExpiry.AddSeconds(-30))
        {
            await RequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _clientId,
                ["refresh_token"] = _refreshToken,
            }, ct).ConfigureAwait(false);
            return _accessToken!;
        }

        throw new InvalidOperationException("Not signed in to Keycloak (session expired — reconnect).");
    }

    private async Task RequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync(_tokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Keycloak login failed ({(int)response.StatusCode}): {Describe(body)}");

        var token = JsonSerializer.Deserialize<TokenResponse>(body, Json)
            ?? throw new InvalidOperationException("Keycloak returned an empty token response.");
        if (string.IsNullOrEmpty(token.AccessToken))
            throw new InvalidOperationException("Keycloak returned no access token.");

        var now = DateTimeOffset.UtcNow;
        _accessToken = token.AccessToken;
        _accessExpiry = now.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 60);
        if (!string.IsNullOrEmpty(token.RefreshToken))
        {
            _refreshToken = token.RefreshToken;
            _refreshExpiry = now.AddSeconds(token.RefreshExpiresIn > 0 ? token.RefreshExpiresIn : 1800);
        }
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString()!;
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString()!;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? "no detail" : body;
    }

    public void Dispose() => _http.Dispose();

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_expires_in")] int RefreshExpiresIn,
        [property: JsonPropertyName("token_type")] string? TokenType);
}
