using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Tools;

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
                // Same DOM as JsonDocument.Parse, but through the source-generated
                // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
                JsonElement root = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), McpJsonSerializerContext.Default.JsonElement);
                return new McpOAuthEndpoints(
                    config.AuthorizationEndpoint ?? JsonArgs.GetString(root, "authorization_endpoint"),
                    config.TokenEndpoint ?? JsonArgs.GetString(root, "token_endpoint"),
                    config.RegistrationEndpoint ?? JsonArgs.GetString(root, "registration_endpoint"));
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
    ///     Compat wrapper over <see cref="RegisterClientResultAsync" />: transport
    ///     problems map to null (callers fall back to the default client id).
    /// </summary>
    public static async Task<string?> RegisterClientAsync(
        HttpClient http,
        string registrationEndpoint,
        string redirectUri,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken = default)
    {
        Result<Maybe<string>> registered = await RegisterClientResultAsync(
            http, registrationEndpoint, redirectUri, scopes, cancellationToken).ConfigureAwait(false);
        return registered.Match(static id => id.HasValue ? id.Value : null, _ => null);
    }

    /// <summary>
    ///     Result railway for dynamic client registration (#201 A6): a server
    ///     rejection stays <c>Success(Maybe.None)</c> (callers fall back to the default
    ///     client id — same as before); unreachable/timeout/malformed responses
    ///     are typed <c>Failure</c>s (<c>Unreachable/Timeout/MalformedRegistrationResponse/…</c>).
    /// </summary>
    public static async Task<Result<Maybe<string>>> RegisterClientResultAsync(
        HttpClient http,
        string registrationEndpoint,
        string redirectUri,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // #180: writer-built body (no Serialize-inside-interpolation).
            // (Cold path; fixed on touch per the issue.)
            JsonElement body = McpJsonRpc.BuildRegisterClientBody(redirectUri, string.Join(" ", scopes));
            using var request = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
            {
                Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result.Success(Maybe<string>.None);
            // Same DOM as JsonDocument.Parse, but through the source-generated
            // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
            JsonElement regRoot = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), McpJsonSerializerContext.Default.JsonElement);
            string? clientId = JsonArgs.GetString(regRoot, "client_id");
            return Result.Success(Maybe<string>.From(clientId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Unreachable server — no client to register.
            return Result.Failure<Maybe<string>>($"Unreachable: client registration failed: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            // Registration timed out.
            return Result.Failure<Maybe<string>>($"Timeout: client registration timed out: {ex.Message}");
        }
        catch (JsonException ex)
        {
            // Malformed registration response.
            return Result.Failure<Maybe<string>>($"MalformedRegistrationResponse: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // Unusable registration URL.
            return Result.Failure<Maybe<string>>($"InvalidRegistrationEndpoint: {ex.Message}");
        }
        catch (IOException ex)
        {
            // Transport failure.
            return Result.Failure<Maybe<string>>($"Transport: client registration failed: {ex.Message}");
        }
    }

    /// <summary>Exchange an authorization code for tokens (RFC6749 §4.1.3).</summary>
    /// <param name="http">Client the token endpoint is called over.</param>
    /// <param name="tokenEndpoint">RFC6749 token endpoint.</param>
    /// <param name="clientId">OAuth client id.</param>
    /// <param name="clientSecret">OAuth client secret (absent for public clients).</param>
    /// <param name="code">Authorization code.</param>
    /// <param name="redirectUri">Redirect URI the code was issued for.</param>
    /// <param name="verifier">PKCE code verifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="timeProvider">
    ///     Clock the token's expiry is stamped from (#1932). Production passes
    ///     nothing and reads <see cref="TimeProvider.System" />.
    /// </param>
    public static async Task<Result<McpOAuthTokens>> ExchangeCodeAsync(
        HttpClient http,
        string tokenEndpoint,
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string verifier,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
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
        return await RequestTokensAsync(http, tokenEndpoint, fields, cancellationToken, timeProvider).ConfigureAwait(false);
    }

    /// <summary>Refresh an access token (RFC6749 §6).</summary>
    /// <param name="http">Client the token endpoint is called over.</param>
    /// <param name="tokenEndpoint">RFC6749 token endpoint.</param>
    /// <param name="clientId">OAuth client id.</param>
    /// <param name="clientSecret">OAuth client secret (absent for public clients).</param>
    /// <param name="refreshToken">Refresh token to spend.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="timeProvider">
    ///     Clock the token's expiry is stamped from (#1932). Production passes
    ///     nothing and reads <see cref="TimeProvider.System" />.
    /// </param>
    public static async Task<Result<McpOAuthTokens>> RefreshAsync(
        HttpClient http,
        string tokenEndpoint,
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId),
        };
        if (!string.IsNullOrEmpty(clientSecret))
            fields.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        return await RequestTokensAsync(http, tokenEndpoint, fields, cancellationToken, timeProvider).ConfigureAwait(false);
    }

    private static async Task<Result<McpOAuthTokens>> RequestTokensAsync(
        HttpClient http,
        string tokenEndpoint,
        List<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider)
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
                return Result.Failure<McpOAuthTokens>($"Token endpoint returned {(int)response.StatusCode}: {payload.Truncate(200)}");

            // Same DOM as JsonDocument.Parse, but through the source-generated
            // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
            JsonElement root = JsonSerializer.Deserialize(payload, McpJsonSerializerContext.Default.JsonElement);
            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
                return Result.Failure<McpOAuthTokens>("Token endpoint response has no access_token.");
            string? refresh = JsonArgs.GetString(root, "refresh_token");
            long expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                && ei.TryGetInt64(out long s) ? s : 3600;

            // #1932: the expiry is stamped from the injected clock — the same
            // clock the reader compares against, so an entry written here
            // expires for that reader exactly `expires_in` later.
            DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            return Result.Success(new McpOAuthTokens(
                at.GetString()!, refresh, now.AddSeconds(Math.Max(0, expiresIn))));
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

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
