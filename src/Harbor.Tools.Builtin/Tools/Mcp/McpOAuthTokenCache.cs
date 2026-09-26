using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Persisted OAuth tokens for one MCP server.
/// </summary>
public sealed record McpOAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc)
{
    /// <summary>True when the token is expired or expires within the safety margin.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc - TimeSpan.FromMinutes(1);
}

/// <summary>
///     Wire shape of the per-server token file (source-gen friendly).
/// </summary>
internal sealed record McpPersistedTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_at_utc")] DateTimeOffset ExpiresAtUtc);

/// <summary>Source-generated JSON metadata for the token cache (AOT-safe).</summary>
[JsonSerializable(typeof(McpPersistedTokens))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class McpOAuthJsonContext : JsonSerializerContext
{
}

/// <summary>
///     File-backed OAuth token cache: one JSON file per server under the harbor
///     home directory (<c>mcp-oauth/mcp-oauth-&lt;server&gt;.json</c>).
///     Existence-tolerant: missing/corrupt files read as empty, never throw.
/// </summary>
public sealed class McpOAuthTokenCache
{
    private readonly string _directory;

    /// <summary>Construct over an explicit directory (tests) or the harbor home.</summary>
    public McpOAuthTokenCache(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
    }

    /// <summary>Resolve the default cache directory (mirrors HarborPaths: HARBOR_HOME wins).</summary>
    public static string DefaultDirectory()
    {
        string home = Environment.GetEnvironmentVariable("HARBOR_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor");
        return Path.Combine(home, "mcp-oauth");
    }

    private string PathFor(string server)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            server = server.Replace(c, '_');
        return Path.Combine(_directory, $"mcp-oauth-{server}.json");
    }

    /// <summary>Load cached tokens, or null when absent/corrupt.</summary>
    public McpOAuthTokens? Load(string server)
    {
        try
        {
            string path = PathFor(server);
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
                return null;
            string? refresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()
                : null;
            if (!root.TryGetProperty("expires_at_utc", out var exp) || exp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(exp.GetString(), out var expires))
                return null;
            return new McpOAuthTokens(at.GetString()!, refresh, expires);
        }
        catch (IOException)
        {
            // Best-effort cache — failures fall back to interactive login.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cache — failures fall back to interactive login.
            return null;
        }
        catch (JsonException)
        {
            // Corrupt cache file reads as empty.
            return null;
        }
    }

    /// <summary>Persist tokens (best-effort; failures are swallowed — callers fall back to login).</summary>
    public void Save(string server, McpOAuthTokens tokens)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string payload = JsonSerializer.Serialize(
                new McpPersistedTokens(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAtUtc),
                McpOAuthJsonContext.Default.McpPersistedTokens);
            File.WriteAllText(PathFor(server), payload);
        }
        catch (IOException)
        {
            // Best-effort cache — save failures fall back to interactive login.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cache — save failures fall back to interactive login.
        }
    }

    /// <summary>Drop cached tokens (logout).</summary>
    public void Clear(string server)
    {
        try
        {
            string path = PathFor(server);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cache — clear failures are harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cache — clear failures are harmless.
        }
    }
}
