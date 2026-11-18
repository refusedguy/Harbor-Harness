using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger? _logger;

    /// <summary>
    ///     Construct over an explicit directory (tests) or the harbor home.
    ///     The optional logger receives a warning with the exception whenever a
    ///     write or delete fails — the caller gets the same failure as a
    ///     <see cref="Result" />, so the error is never silently dropped.
    /// </summary>
    public McpOAuthTokenCache(string? directory = null, ILogger? logger = null)
    {
        _directory = directory ?? DefaultDirectory();
        _logger = logger;
    }

    /// <summary>
    ///     Resolve the default cache directory out of <see cref="Harbor.Abstractions.Filesystem.HarborPaths" />
    ///     (HARBOR_HOME wins) — the single home resolution shared with the rest of
    ///     the harness (#1570), previously a verbatim copy.
    /// </summary>
    public static string DefaultDirectory()
    {
        string home = Harbor.Abstractions.Filesystem.HarborPaths.GetHarborHome();
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
        Result<McpOAuthTokens> loaded = LoadResult(server);
        return loaded.IsSuccess ? loaded.Value : null;
    }

    /// <summary>
    ///     Result railway for the token cache (#201 A6): a missing file is
    ///     <c>Failure("CacheMiss: ...")</c>, an unreadable file is
    ///     <c>Failure("CacheUnreadable: ...")</c>, a present-but-unusable file is
    ///     <c>Failure("CacheCorrupt: ...")</c> — callers that care (transports,
    ///     login hints) can tell "never logged in" from "cache went bad".
    /// </summary>
    public Result<McpOAuthTokens> LoadResult(string server)
    {
        string path = PathFor(server);
        if (!File.Exists(path))
            return Result.Failure<McpOAuthTokens>($"CacheMiss: no cached OAuth tokens for MCP server '{server}'.");
        try
        {
            // #1098 (single slice): source-gen read — the same
            // McpOAuthJsonContext the Save path already writes with.
            // Cold leg by design (one file read per OAuth flow, never
            // per-turn/per-frame); no Utf8JsonReader rework needed here.
            var persisted = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                McpOAuthJsonContext.Default.McpPersistedTokens);
            if (persisted is null || string.IsNullOrEmpty(persisted.AccessToken))
                return Result.Failure<McpOAuthTokens>($"CacheCorrupt: cached OAuth tokens for MCP server '{server}' have no access_token.");
            if (persisted.ExpiresAtUtc == default)
                return Result.Failure<McpOAuthTokens>($"CacheCorrupt: cached OAuth tokens for MCP server '{server}' have no usable expiry.");
            return Result.Success(new McpOAuthTokens(persisted.AccessToken, persisted.RefreshToken, persisted.ExpiresAtUtc));
        }
        catch (JsonException ex)
        {
            // Corrupt cache file reads as empty.
            return Result.Failure<McpOAuthTokens>($"CacheCorrupt: cached OAuth tokens for MCP server '{server}' are not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            // Best-effort cache — failures fall back to interactive login.
            return Result.Failure<McpOAuthTokens>($"CacheUnreadable: cannot read cached OAuth tokens for MCP server '{server}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Best-effort cache — failures fall back to interactive login.
            return Result.Failure<McpOAuthTokens>($"CacheUnreadable: cannot read cached OAuth tokens for MCP server '{server}': {ex.Message}");
        }
    }

    /// <summary>
    ///     Persist tokens. A failed write is a <see cref="Result" /> failure, not
    ///     a swallowed exception: the caller must not report "token cached" when
    ///     nothing reached disk. The failure is also logged with the exception
    ///     when a logger was supplied.
    /// </summary>
    public Result Save(string server, McpOAuthTokens tokens)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string payload = JsonSerializer.Serialize(
                new McpPersistedTokens(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAtUtc),
                McpOAuthJsonContext.Default.McpPersistedTokens);
            File.WriteAllText(PathFor(server), payload);
            return Result.Success();
        }
        catch (IOException ex)
        {
            if (_logger is not null) McpOAuthTokenCacheLog.SaveFailed(_logger, ex, server);
            return Result.Failure($"Cannot save cached OAuth tokens for MCP server '{server}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger is not null) McpOAuthTokenCacheLog.SaveFailed(_logger, ex, server);
            return Result.Failure($"Cannot save cached OAuth tokens for MCP server '{server}': {ex.Message}");
        }
    }

    /// <summary>
    ///     Drop cached tokens (logout). A failed delete is a
    ///     <see cref="Result" /> failure so the caller cannot claim the cache was
    ///     cleared when it was not; the exception is logged when available.
    /// </summary>
    public Result Clear(string server)
    {
        try
        {
            string path = PathFor(server);
            if (File.Exists(path))
                File.Delete(path);
            return Result.Success();
        }
        catch (IOException ex)
        {
            if (_logger is not null) McpOAuthTokenCacheLog.ClearFailed(_logger, ex, server);
            return Result.Failure($"Cannot clear cached OAuth tokens for MCP server '{server}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger is not null) McpOAuthTokenCacheLog.ClearFailed(_logger, ex, server);
            return Result.Failure($"Cannot clear cached OAuth tokens for MCP server '{server}': {ex.Message}");
        }
    }
}

/// <summary>
///     BCL <c>[LoggerMessage]</c> delegates for <see cref="McpOAuthTokenCache" />.
///     Each delegate carries the exception so the write/delete failure is not
///     reduced to a bare message when it reaches the log.
/// </summary>
internal static partial class McpOAuthTokenCacheLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Could not save cached OAuth tokens for MCP server '{Server}'")]
    public static partial void SaveFailed(ILogger logger, Exception ex, string server);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not clear cached OAuth tokens for MCP server '{Server}'")]
    public static partial void ClearFailed(ILogger logger, Exception ex, string server);
}
