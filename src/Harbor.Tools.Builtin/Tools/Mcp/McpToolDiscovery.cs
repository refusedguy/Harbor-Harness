using System.Text.Json;
using Harbor.Abstractions.Agents;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Turns every <c>tools/list</c> answer of a registered MCP server into a real
///     <see cref="ITool"/> in the tool registry (#2227).
/// </summary>
/// <remarks>
///     <para>
///         The adapter keeps the server NAME, not a process handle. Its lifespan is
///         therefore the server registration's lifespan: a crash surfaces as a
///         failed call and <c>McpRegistry.ServerEntry.GetProcess</c> respawns the
///         process on the next call without touching this registration.
///     </para>
///     <para>
///         Each adapter declares <see cref="ToolSafetyProfile.Opaque"/> (its args are
///         a free-form MCP payload) and receives an exact Allow rule on every
///         registered agent, so the dispatcher and the prompt treat it like a
///         builtin. A user rule with the same key overrides the grant through
///         <see cref="PermissionRuleset.Merge"/>.
///     </para>
/// </remarks>
internal static class McpToolDiscovery
{
    // #2228: a server may answer tools/list in pages (the plugin-host server
    // caps a page at 50 tools / 16 KiB). 64 pages is 3200 tools at that cap,
    // so a catalog that still has a cursor after 64 answers is truncated by
    // the same hard bound the server applies to its own chain.
    internal const int MaxPagesPerServer = 64;

    internal sealed record DiscoveredTool(string Server, string RemoteName, ToolName Name);

    /// <summary>
    ///     Ask each registered server for its tools, following <c>nextCursor</c>
    ///     until the server says the catalog is exhausted. A server that fails,
    ///     times out or answers with malformed JSON contributes the pages it did
    ///     answer and nothing more; discovery never takes the composition root down.
    /// </summary>
    internal static async Task<IReadOnlyList<DiscoveredTool>> DiscoverAsync(
        IMcpRegistry registry,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var discovered = new List<DiscoveredTool>();
        foreach (string server in registry.GetServerNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One deadline for the whole page chain: a slow server cannot buy
            // itself another 5 s per page.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            string? cursor = null;
            // A server that keeps naming a cursor it already named is answered
            // once and then abandoned: the chain is otherwise infinite.
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 0; ; page++)
            {
                if (page == MaxPagesPerServer)
                {
                    logger?.LogWarning(
                        "MCP server '{Server}' still had a tools/list cursor after {Pages} pages; its remaining tools are not registered",
                        server, MaxPagesPerServer);
                    break;
                }

                Result<string> listed;
                try
                {
                    listed = await registry.InvokeAsync(
                            server, "tools/list", McpJsonRpc.BuildToolsListParams(cursor), linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger?.LogWarning(
                        "MCP server '{Server}' timed out during tools/list page {Page}; keeping the pages already listed",
                        server, page + 1);
                    break;
                }

                JsonDocument payload;
                if (listed.IsSuccess)
                {
                    try
                    {
                        payload = JsonDocument.Parse(listed.Value);
                    }
                    catch (JsonException)
                    {
                        logger?.LogWarning(
                            "MCP server '{Server}' returned malformed tools/list JSON on page {Page}; keeping the pages already listed",
                            server, page + 1);
                        break;
                    }
                }
                else
                {
                    logger?.LogWarning(
                        "Cannot list tools of MCP server '{Server}' (page {Page}): {Error}; keeping the pages already listed",
                        server, page + 1, listed.Error);
                    break;
                }

                string? next;
                using (payload)
                {
                    if (!payload.RootElement.TryGetProperty("result", out JsonElement result))
                        break;

                    if (result.TryGetProperty("tools", out JsonElement tools)
                        && tools.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement entry in tools.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object
                                || !entry.TryGetProperty("name", out JsonElement nameElement)
                                || nameElement.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            string? remoteName = nameElement.GetString();
                            if (string.IsNullOrWhiteSpace(remoteName)) continue;

                            discovered.Add(new DiscoveredTool(server, remoteName, McpToolAdapter.BuildName(server, remoteName)));
                        }
                    }

                    next = result.TryGetProperty("nextCursor", out JsonElement cursorElement)
                        && cursorElement.ValueKind == JsonValueKind.String
                            ? cursorElement.GetString()
                            : null;
                }

                if (string.IsNullOrEmpty(next))
                    break;

                if (!seenCursors.Add(next))
                {
                    logger?.LogWarning(
                        "MCP server '{Server}' repeated tools/list cursor '{Cursor}'; stopping after page {Page}",
                        server, next, page + 1);
                    break;
                }

                cursor = next;
            }
        }

        return discovered;
    }

    /// <summary>
    ///     Discover and register in one step. Called once by the composition root,
    ///     before the tool registry is frozen.
    /// </summary>
    /// <returns>The number of adapters actually registered.</returns>
    internal static async Task<int> RegisterAsync(
        IMcpRegistry registry,
        IToolRegistry tools,
        IAgentRegistry agents,
        ILoggerFactory loggerFactory,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DiscoveredTool> discovered = await DiscoverAsync(registry, logger, cancellationToken)
            .ConfigureAwait(false);

        var grants = new List<PermissionRule>(discovered.Count);
        foreach (DiscoveredTool item in discovered)
        {
            Result registered = tools.Register(new McpToolAdapter(
                registry, item.Server, item.RemoteName, loggerFactory.CreateLogger<McpToolAdapter>()));
            if (registered.IsFailure)
            {
                logger?.LogWarning("Cannot register MCP tool '{Name}': {Error}", item.Name, registered.Error);
                continue;
            }

            grants.Add(new PermissionRule(item.Name.Value, "*", PermissionAction.Allow));
        }

        Grant(agents, grants);
        return grants.Count;
    }

    /// <summary>
    ///     Add the discovered tools to each agent's ruleset. The agents in the
    ///     registry are the ones the loop resolves, so the prompt and the dispatcher
    ///     see the same verdict. Unregister/Register is the existing mutation door;
    ///     no new contract is introduced for this (#2227).
    /// </summary>
    internal static void Grant(IAgentRegistry agents, IReadOnlyList<PermissionRule> rules)
    {
        if (rules.Count == 0) return;

        var extra = new PermissionRuleset(rules);
        foreach (AgentDefinition agent in agents.GetAllAgents())
        {
            var updated = agent with { Permission = agent.Permission.Merge(extra) };
            agents.Unregister(agent.Name);
            agents.Register(updated);
        }
    }
}
