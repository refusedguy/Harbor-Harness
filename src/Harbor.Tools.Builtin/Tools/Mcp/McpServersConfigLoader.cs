using System.Text.Json;

namespace Harbor.Tools.Mcp;

/// <summary>
///     One enabled server resolved from config: either how to spawn it
///     (<see cref="StartInfo" />) or where to reach it
///     (<see cref="Remote" />) — never both, never neither.
/// </summary>
public sealed record McpServerEntry(string Name, McpServerStartInfo? StartInfo, McpRemoteConfig? Remote);

/// <summary>
///     Remote MCP server coordinates from an mcp.json entry
///     (<c>url</c> + Harbor extensions <c>transport</c>/<c>headers</c>/<c>auth</c>).
/// </summary>
public sealed record McpRemoteConfig(
    string Url,
    string Transport,
    IReadOnlyDictionary<string, string>? Headers,
    McpOAuthConfig? OAuth);

/// <summary>
///     Loads MCP server definitions from standard <c>mcp.json</c> files (industry schema),
///     overlays them in order (later wins), expands <c>${...}</c> macros, and skips
///     <c>disabled</c> servers. No Harbor-specific manifest format is introduced.
/// </summary>
public sealed class McpServersConfigLoader
{
    public string ProjectRoot { get; }
    public string Home { get; }
    public string HarborHome { get; }

    public McpServersConfigLoader(string projectRoot, string? home = null, string? harborHome = null)
    {
        ProjectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
        Home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        HarborHome = harborHome ?? Path.Combine(Home, ".harbor");
    }

    /// <summary>
    ///     Load and overlay config from the given files, in order (later overrides earlier for the
    ///     same server name). Missing files are treated as empty config, not an error.
    /// </summary>
    public IReadOnlyList<McpServerEntry> Load(params string[] paths)
    {
        var merged = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                continue;

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var (name, cfg) in ParseToMap(json))
                merged[name] = cfg;
        }

        return ToEntries(merged);
    }

    /// <summary>
    ///     Parse a single JSON document (the same schema as a file) into resolved entries.
    ///     Used directly in tests and by the in-process host.
    /// </summary>
    public IReadOnlyList<McpServerEntry> LoadFromJson(string json)
        => ToEntries(ParseToMap(json));

    public string Expand(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        return value
            .Replace("${projectRoot}", ProjectRoot, StringComparison.Ordinal)
            .Replace("${home}", Home, StringComparison.Ordinal)
            .Replace("${harborHome}", HarborHome, StringComparison.Ordinal);
    }

    private IReadOnlyList<McpServerEntry> ToEntries(Dictionary<string, McpServerConfig> map)
    {
        var entries = new List<McpServerEntry>(map.Count);
        foreach (var (name, cfg) in map)
        {
            if (cfg is null) continue;
            if (cfg.Disabled == true) continue;

            if (!string.IsNullOrWhiteSpace(cfg.Url))
            {
                var headers = cfg.Headers is null
                    ? null
                    : cfg.Headers.ToDictionary(kv => kv.Key, kv => Expand(kv.Value), StringComparer.Ordinal);
                entries.Add(new McpServerEntry(
                    name,
                    null,
                    new McpRemoteConfig(
                        Expand(cfg.Url),
                        string.IsNullOrWhiteSpace(cfg.Transport) ? "http" : cfg.Transport,
                        headers,
                        cfg.OAuth)));
                continue;
            }

            if (string.IsNullOrWhiteSpace(cfg.Command)) continue;

            var args = cfg.Args is null
                ? Array.Empty<string>()
                : cfg.Args.Select(Expand).ToArray();

            var env = cfg.Env is null
                ? null
                : cfg.Env.ToDictionary(kv => kv.Key, kv => Expand(kv.Value), StringComparer.Ordinal);

            entries.Add(new McpServerEntry(name, new McpServerStartInfo
            {
                Command = Expand(cfg.Command),
                Args = args,
                WorkingDirectory = string.IsNullOrWhiteSpace(cfg.Cwd) ? null : Expand(cfg.Cwd),
                Environment = env
            }, null));
        }

        return entries;
    }

    private Dictionary<string, McpServerConfig> ParseToMap(string json)
    {
        var map = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return map;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return map;

        if (!root.TryGetProperty("mcpServers", out var servers)
            || servers.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var server in servers.EnumerateObject())
        {
            if (server.Value.ValueKind != JsonValueKind.Object)
                continue;

            var cfg = new McpServerConfig();
            if (server.Value.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                cfg.Command = cmd.GetString();
            if (server.Value.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                cfg.Args = argsEl.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetString()!)
                    .ToArray();
            if (server.Value.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                cfg.Cwd = cwd.GetString();
            if (server.Value.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
            {
                cfg.Env = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in envEl.EnumerateObject())
                    if (e.Value.ValueKind == JsonValueKind.String)
                        cfg.Env[e.Name] = e.Value.GetString()!;
            }
            if (server.Value.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True)
                cfg.Disabled = true;
            if (server.Value.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                cfg.Url = url.GetString();
            if (server.Value.TryGetProperty("transport", out var transport) && transport.ValueKind == JsonValueKind.String)
                cfg.Transport = transport.GetString();
            if (server.Value.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
            {
                cfg.Headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var h in headersEl.EnumerateObject())
                    if (h.Value.ValueKind == JsonValueKind.String)
                        cfg.Headers[h.Name] = h.Value.GetString()!;
            }
            cfg.OAuth = McpOAuthConfig.Parse(server.Value);

            map[server.Name] = cfg;
        }

        return map;
    }
}
