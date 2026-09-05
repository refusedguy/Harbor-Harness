using Harbor.Hosting;
using Harbor.Tools.Mcp;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Core of <c>harbor mcp login|logout|list</c>: OAuth for remote MCP servers.
///     Reads the same <c>mcp.json</c> overlay as the tool runtime
///     (<c>HARBOR_MCP_CONFIG</c>, then <c>~/.harbor/mcp.json</c>, then
///     <c>&lt;cwd&gt;/.harbor/mcp.json</c> — later wins). Login runs the
///     browser authorization-code flow and caches tokens; logout drops them.
/// </summary>
public static class McpLoginRunner
{
    public static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        string[] args,
        CancellationToken ct = default)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        return sub switch
        {
            "list" => await ListAsync(output).ConfigureAwait(false),
            "login" => await LoginAsync(output, error, args.Skip(1).ToArray(), ct).ConfigureAwait(false),
            "logout" => Logout(output, error, args.Skip(1).ToArray()),
            _ => Usage(error),
        };
    }

    private static Task<int> ListAsync(TextWriter output)
    {
        var remotes = LoadRemoteEntries();
        if (remotes.Count == 0)
        {
            output.WriteLine("(no remote MCP servers configured)");
            return Task.FromResult(0);
        }

        var cache = new McpOAuthTokenCache();
        foreach (var (name, url, oauth) in remotes)
        {
            string auth = oauth is null ? "no-auth" : "oauth";
            string state = "no-token";
            var cached = cache.Load(name);
            if (cached is not null)
                state = cached.IsExpired(DateTimeOffset.UtcNow) ? "expired" : "ok";
            output.WriteLine($"{name} [{auth}/{state}] {url}");
        }

        return Task.FromResult(0);
    }

    private static async Task<int> LoginAsync(
        TextWriter output, TextWriter error, string[] args, CancellationToken ct)
    {
        string? name = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(name))
        {
            await error.WriteLineAsync("Usage: harbor mcp login <server>").ConfigureAwait(false);
            return 2;
        }

        var remote = LoadRemoteEntries().FirstOrDefault(r =>
            r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (remote.Name is null)
        {
            await error.WriteLineAsync($"No remote MCP server named '{name}'. See 'harbor mcp list'.").ConfigureAwait(false);
            return 1;
        }

        if (remote.OAuth is null)
        {
            await error.WriteLineAsync(
                $"Server '{name}' has no auth block in mcp.json — add one or export HARBOR_MCP_OAUTH_TOKEN.").ConfigureAwait(false);
            return 1;
        }

        var handler = new McpOAuthHandler(name, new Uri(remote.Url, UriKind.Absolute), remote.OAuth);
        output.WriteLine($"Opening browser for '{name}' OAuth login…");
        var result = await handler.LoginAsync(cancellationToken: ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            await error.WriteLineAsync(result.Error).ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(result.Value).ConfigureAwait(false);
        return 0;
    }

    private static int Logout(TextWriter output, TextWriter error, string[] args)
    {
        string? name = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(name))
        {
            error.WriteLine("Usage: harbor mcp logout <server>");
            return 2;
        }

        new McpOAuthTokenCache().Clear(name);
        output.WriteLine($"Logged out of MCP server '{name}' (cached tokens cleared).");
        return 0;
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("""
                        Usage: harbor mcp [list|login|logout]
                          mcp list                    show remote servers + token state
                          mcp login <server>          browser OAuth login (needs auth block in mcp.json)
                          mcp logout <server>         drop cached OAuth tokens
                        """);
        return 2;
    }

    private static IReadOnlyList<(string Name, string Url, McpOAuthConfig? OAuth)> LoadRemoteEntries()
    {
        string harborHome = HarborPaths.GetHarborHome();
        string projectRoot = Directory.GetCurrentDirectory();
        var paths = new List<string>();
        if (Environment.GetEnvironmentVariable("HARBOR_MCP_CONFIG") is { Length: > 0 } explicitConfig)
            paths.Add(explicitConfig);
        paths.Add(Path.Combine(harborHome, "mcp.json"));
        paths.Add(Path.Combine(projectRoot, ".harbor", "mcp.json"));

        var merged = new Dictionary<string, (string Url, McpOAuthConfig? OAuth)>(StringComparer.Ordinal);
        var loader = new McpServersConfigLoader(projectRoot);
        foreach (string path in paths)
        {
            foreach (var entry in loader.Load(path))
            {
                if (entry.Remote is null)
                    continue;
                merged[entry.Name] = (entry.Remote.Url, entry.Remote.OAuth);
            }
        }

        return merged
            .Select(kv => (kv.Key, kv.Value.Url, kv.Value.OAuth))
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .ToArray();
    }
}
