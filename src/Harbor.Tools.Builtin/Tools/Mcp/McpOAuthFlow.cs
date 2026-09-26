using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harbor.Tools.Mcp;

/// <summary>
///     OAuth2 endpoints for one MCP server: explicit overrides win, otherwise
///     RFC8414 authorization-server metadata discovery against the server URL.
/// </summary>
public sealed record McpOAuthEndpoints(
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? RegistrationEndpoint);

/// <summary>
///     OAuth2 authorization-code flow with PKCE (RFC6749 + RFC7636 + RFC8414 +
///     RFC7591) for MCP remote servers. All JSON is parsed manually
///     (AOT-safe); HTTP goes through an injected <see cref="HttpClient" /> so
///     tests can stub the wire.
/// </summary>
public static class McpOAuthFlow
{
    /// <summary>User-agent for OAuth HTTP calls.</summary>
    public const string UserAgent = "Harbor-MCP-OAuth/1.0";

    /// <summary>
    ///     Resolve endpoints: explicit config overrides win, the rest comes
    ///     from RFC8414 metadata (<c>/.well-known/oauth-authorization-server</c>
    ///     and the issuer-root variant). Returns nulls when undiscoverable.
    /// </summary>
    public static async Task<McpOAuthEndpoints> DiscoverAsync(
        HttpClient http,
        Uri serverUrl,
        McpOAuthConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config.AuthorizationEndpoint is not null && config.TokenEndpoint is not null)
        {
            return new McpOAuthEndpoints(
                config.AuthorizationEndpoint, config.TokenEndpoint, config.RegistrationEndpoint);
        }

        foreach (string metadataUrl in MetadataCandidates(serverUrl))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var root = doc.RootElement;
                return new McpOAuthEndpoints(
                    config.AuthorizationEndpoint ?? Str(root, "authorization_endpoint"),
                    config.TokenEndpoint ?? Str(root, "token_endpoint"),
                    config.RegistrationEndpoint ?? Str(root, "registration_endpoint"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                // Per-candidate failure — try the next metadata URL.
            }
            catch (TaskCanceledException)
            {
                // Per-candidate timeout — try the next metadata URL.
            }
            catch (JsonException)
            {
                // Malformed metadata — try the next metadata URL.
            }
            catch (InvalidOperationException)
            {
                // Unusable metadata URL — try the next metadata URL.
            }
            catch (IOException)
            {
                // Transport failure — try the next metadata URL.
            }
        }

        return new McpOAuthEndpoints(config.AuthorizationEndpoint, config.TokenEndpoint, config.RegistrationEndpoint);
    }

    /// <summary>RFC8414 metadata URL candidates for a resource server URL.</summary>
    internal static IReadOnlyList<string> MetadataCandidates(Uri serverUrl)
    {
        string root = $"{serverUrl.Scheme}://{serverUrl.Authority}";
        string path = serverUrl.AbsolutePath.TrimEnd('/');
        var candidates = new List<string>(3);
        if (path.Length > 0)
            candidates.Add($"{root}/.well-known/oauth-authorization-server{path}");
        candidates.Add($"{root}/.well-known/oauth-authorization-server");
        return candidates;
    }

    /// <summary>Generate a PKCE code verifier (43-128 chars) + S256 challenge.</summary>
    public static (string Verifier, string Challenge) NewPkcePair()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string verifier = Base64Url(bytes);
        using var sha = SHA256.Create();
        string challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>Generate a random OAuth state value (CSRF protection).</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>Build the browser authorization URL (response_type=code + PKCE S256).</summary>
    public static string BuildAuthorizationUrl(
        string authorizationEndpoint,
        string clientId,
        string redirectUri,
        string challenge,
        string state,
        IReadOnlyList<string> scopes)
    {
        var sb = new StringBuilder(authorizationEndpoint);
        sb.Append(authorizationEndpoint.Contains('?') ? '&' : '?');
        sb.Append("response_type=code");
        sb.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&code_challenge=").Append(Uri.EscapeDataString(challenge));
        sb.Append("&code_challenge_method=S256");
        sb.Append("&state=").Append(Uri.EscapeDataString(state));
        if (scopes.Count > 0)
            sb.Append("&scope=").Append(Uri.EscapeDataString(string.Join(" ", scopes)));
        return sb.ToString();
    }

    /// <summary>
    ///     Dynamic client registration (RFC7591). Returns the assigned client
    ///     id, or null when the server has no registration endpoint or rejects.
    /// </summary>
    public static async Task<string?> RegisterClientAsync(
        HttpClient http,
        string registrationEndpoint,
        string redirectUri,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var body = JsonDocument.Parse($"{{\"redirect_uris\":[{JsonSerializer.Serialize(redirectUri)}],\"grant_types\":[\"authorization_code\",\"refresh_token\"],\"scope\":{JsonSerializer.Serialize(string.Join(" ", scopes))}}}");
            using var request = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
            {
                Content = new StringContent(body.RootElement.GetRawText(), Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("client_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Unreachable server — no client to register.
            return null;
        }
        catch (TaskCanceledException)
        {
            // Registration timed out.
            return null;
        }
        catch (JsonException)
        {
            // Malformed registration response.
            return null;
        }
        catch (InvalidOperationException)
        {
            // Unusable registration URL.
            return null;
        }
        catch (IOException)
        {
            // Transport failure.
            return null;
        }
    }

    /// <summary>Exchange an authorization code for tokens (RFC6749 §4.1.3).</summary>
    public static async Task<Result<McpOAuthTokens>> ExchangeCodeAsync(
        HttpClient http,
        string tokenEndpoint,
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string verifier,
        CancellationToken cancellationToken = default)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("code_verifier", verifier),
            new("client_id", clientId),
        };
        if (!string.IsNullOrEmpty(clientSecret))
            fields.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        return await RequestTokensAsync(http, tokenEndpoint, fields, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refresh an access token (RFC6749 §6).</summary>
    public static async Task<Result<McpOAuthTokens>> RefreshAsync(
        HttpClient http,
        string tokenEndpoint,
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId),
        };
        if (!string.IsNullOrEmpty(clientSecret))
            fields.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        return await RequestTokensAsync(http, tokenEndpoint, fields, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<McpOAuthTokens>> RequestTokensAsync(
        HttpClient http,
        string tokenEndpoint,
        List<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(fields)
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result.Failure<McpOAuthTokens>($"Token endpoint returned {(int)response.StatusCode}: {Truncate(payload, 200)}");

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
                return Result.Failure<McpOAuthTokens>("Token endpoint response has no access_token.");
            string? refresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()
                : null;
            long expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                && ei.TryGetInt64(out long s) ? s : 3600;
            return Result.Success(new McpOAuthTokens(
                at.GetString()!, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expiresIn))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure<McpOAuthTokens>($"Token request failed: {ex.Message}");
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
