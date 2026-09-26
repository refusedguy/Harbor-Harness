using System.Text.Json;

namespace Harbor.Tools.Mcp;

/// <summary>
///     OAuth2 settings for one remote MCP server (the <c>auth</c> block of an
///     <c>mcp.json</c> remote entry). All fields optional: when endpoints are
///     absent they are discovered via RFC8414 metadata; when no client id is
///     configured, dynamic client registration (RFC7591) is attempted.
/// </summary>
/// <example>
///     <code>
///     {
///       "mcpServers": {
///         "cloud": {
///           "url": "https://mcp.example.com/mcp",
///           "transport": "http",
///           "auth": {
///             "clientId": "harbor-cli",
///             "scopes": ["mcp:read", "mcp:tools"],
///             "authorizationEndpoint": "https://example.com/oauth/authorize",
///             "tokenEndpoint": "https://example.com/oauth/token"
///           }
///         }
///       }
///     }
///     </code>
/// </example>
public sealed record McpOAuthConfig
{
    /// <summary>OAuth client id. Absent → dynamic client registration is attempted.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth client secret. Absent for public/loopback clients (PKCE only).</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Requested scopes. Empty → server defaults.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>Override for the authorization endpoint (else RFC8414 discovery).</summary>
    public string? AuthorizationEndpoint { get; init; }

    /// <summary>Override for the token endpoint (else RFC8414 discovery).</summary>
    public string? TokenEndpoint { get; init; }

    /// <summary>Override for the dynamic-registration endpoint (else RFC8414 discovery).</summary>
    public string? RegistrationEndpoint { get; init; }

    /// <summary>Loopback port for the browser-redirect listener. 0 → any free port.</summary>
    public int RedirectPort { get; init; }

    /// <summary>Parse the <c>auth</c> block of an mcp.json remote entry. Unknown fields ignored.</summary>
    public static McpOAuthConfig? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (!element.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object)
            return null;

        static string? Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var scopes = new List<string>();
        if (auth.TryGetProperty("scopes", out var scopesEl) && scopesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scopesEl.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    scopes.Add(s.GetString()!);
            }
        }

        int port = 0;
        if (auth.TryGetProperty("redirectPort", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            && portEl.TryGetInt32(out int p) && p is > 0 and < 65536)
            port = p;

        return new McpOAuthConfig
        {
            ClientId = Str(auth, "clientId"),
            ClientSecret = Str(auth, "clientSecret"),
            Scopes = scopes,
            AuthorizationEndpoint = Str(auth, "authorizationEndpoint"),
            TokenEndpoint = Str(auth, "tokenEndpoint"),
            RegistrationEndpoint = Str(auth, "registrationEndpoint"),
            RedirectPort = port,
        };
    }
}
