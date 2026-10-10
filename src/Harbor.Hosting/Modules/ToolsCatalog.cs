using Harbor.Application.Agents;
using Harbor.Tools.Builtin;
using Harbor.Tools.Mcp;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Logging;
using Harbor.Registries.Agents;
using Harbor.Registries.Tools;
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Hosting;
#endif

namespace Harbor.Hosting;

internal static class ToolsCatalog
{
    internal static AgentRegistry CreateAgentRegistry(HarborCompositionContext ctx)
    {
        var registry = new AgentRegistry();
        var ab = new AgentRegistryBuilder(registry);

        string defaultModel = ctx.Options.ModelSource == HarborAgentModelSource.CommonConfig
            ? ResolveDefaultModelFromCommon(ctx.Common)
            : ctx.Harbor.EffectiveModel;
        string[] parts = defaultModel.Split('/', 2);
        string providerId = parts[0];
        string modelId = parts.Length > 1 ? parts[1] : defaultModel;
        ab.AddAgent(AgentDefinition.CodeDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.PlanDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.ExploreDefault(modelId, providerId));
        return registry;
    }

    /// <summary>HARBOR_MODEL env, else CommonConfig DefaultProvider/DefaultModel (desktop).</summary>
    internal static string ResolveDefaultModelFromCommon(Harbor.Desktop.Abstractions.Configuration.CommonConfig commonConfig)
    {
        string? env = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        string model = commonConfig.DefaultModel;
        string provider = commonConfig.DefaultProvider;
        string prefix = provider + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model
            : prefix + model;
    }

    /// <summary>Build the MCP registry and load the mcp.json overlays (or the empty desktop stand-in).</summary>
    /// <param name="ctx">Composition context (options + logger factory).</param>
    /// <param name="transports">
    ///     Remote transport strategies (#477). Null → <see cref="McpTransportResolver.Default" />.
    /// </param>
    internal static IMcpRegistry CreateMcpRegistry(
        HarborCompositionContext ctx, McpTransportResolver? transports = null)
    {
        if (!ctx.Options.IncludeMcpTools)
        {
            // Desktop subset: empty registry so view-models can resolve IMcpRegistry.
            return new InMemoryMcpRegistry(ctx.LoggerFactory.CreateLogger<InMemoryMcpRegistry>());
        }

        var mcpRegistry = new McpRegistry(ctx.LoggerFactory.CreateLogger<McpRegistry>(), transports);

        // Load MCP servers from the standard mcp.json files in overlay order
        // (later wins): an explicit HARBOR_MCP_CONFIG, then ~/.harbor/mcp.json,
        // then <project>/.harbor/mcp.json.
        string projectRoot = Directory.GetCurrentDirectory();
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborHome = Path.Combine(homeDir, ".harbor");
        var mcpLoader = new McpServersConfigLoader(projectRoot, homeDir, harborHome);

        var mcpConfigPaths = new List<string>();
        string? explicitMcp = Environment.GetEnvironmentVariable("HARBOR_MCP_CONFIG");
        if (!string.IsNullOrEmpty(explicitMcp))
            mcpConfigPaths.Add(explicitMcp);
        mcpConfigPaths.Add(Path.Combine(harborHome, "mcp.json"));
        mcpConfigPaths.Add(Path.Combine(projectRoot, ".harbor", "mcp.json"));

        foreach (var entry in mcpLoader.Load(mcpConfigPaths.ToArray()))
        {
            if (entry.Remote is not null)
            {
                var remote = entry.Remote;
                var registered = mcpRegistry.Register(entry.Name, remote.Url, remote.Transport, remote.Headers, remote.OAuth);
                if (registered.IsFailure)
                    ctx.Logger.LogWarning("Skipping MCP server '{Name}': {Error}", entry.Name, registered.Error);
            }
            else if (entry.StartInfo is not null)
            {
                mcpRegistry.Register(entry.Name, entry.StartInfo);
            }
        }

#if HARBOR_WITH_PLUGINS
        RegisterOutOfProcPluginRoute(ctx, mcpRegistry);
#endif
        return mcpRegistry;
    }

#if HARBOR_WITH_PLUGINS
    /// <summary>
    ///     MCP server name for the out-of-process CS-plugin host. Matches
    ///     <c>Harbor.Plugins.Host.McpStdioServer</c>'s
    ///     <c>ServerName</c> — the wire contract both sides spell literally.
    /// </summary>
    private const string PluginMcpServerName = "harbor-csharp-plugins";

    /// <summary>
    ///     #1055 slice 3: the out-of-process CS-plugin host is the default
    ///     route. When the binary ships next to the CLI and the user did not
    ///     already name this server, register it as a stdio MCP server — no
    ///     <c>mcp.json</c> entry needed. An explicit user entry wins because it
    ///     is already registered above, so this never overrides deliberate
    ///     config. Nothing is spawned here: MCP servers connect lazily on
    ///     first call.
    /// </summary>
    private static void RegisterOutOfProcPluginRoute(HarborCompositionContext ctx, McpRegistry mcpRegistry)
    {
        string? hostPath = PluginHostLocator.LocateHost();
        if (hostPath is null)
            return;
        if (mcpRegistry.GetServerNames().Contains(PluginMcpServerName, StringComparer.Ordinal))
            return;

        var registered = mcpRegistry.Register(
            PluginMcpServerName,
            new McpServerStartInfo { Command = hostPath });
        if (registered.IsFailure)
            ctx.Logger.LogWarning("Skipping out-of-proc plugin route '{Name}': {Error}", PluginMcpServerName, registered.Error);
        else
            ctx.Logger.LogInformation("MCP server '{Name}' registered from {Path} (out-of-proc plugin route)", PluginMcpServerName, hostPath);
    }
#endif

    internal static ToolRegistry CreateToolRegistry(
        HarborCompositionContext ctx, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry,
        Harbor.Abstractions.Agents.ISubAgentRunner subAgentRunner,
        Harbor.Abstractions.Agents.IBackgroundTaskRegistry? backgroundTasks = null,
        Harbor.Abstractions.Lsp.ILspService? lspService = null,
        ISessionStore? sessionStore = null)
    {
        var registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(registry, ctx.LoggerFactory);
        bool full = ctx.Options.ToolSet == HarborToolSetKind.Full14;

        // P.2: logger-aware lambdas replaced 14 IToolFactory ceremony classes.
        // #470: every tool dependency is constructor-injected here — ToolContext
        // no longer carries an IServiceProvider (both production call sites used
        // to pass `null!`, so the LSP / MCP / session-store lookups that hung off
        // it were dead). A null `lspService` / `sessionStore` now degrades the
        // optional enrichment visibly instead of resolving against a null provider.
        tb.AddTool(lf => new ReadTool(lf.CreateLogger<ReadTool>(), lspService));
        tb.AddTool(lf => new WriteTool(lf.CreateLogger<WriteTool>()));
        tb.AddTool(lf => new EditTool(lf.CreateLogger<EditTool>(), lspService));
        tb.AddTool(lf => new BashTool(lf.CreateLogger<BashTool>()));
        tb.AddTool(lf => new GlobTool(lf.CreateLogger<GlobTool>()));
        tb.AddTool(lf => new GrepTool(lf.CreateLogger<GrepTool>()));
        tb.AddTool(lf => new LsTool(lf.CreateLogger<LsTool>()));
        tb.AddTool(lf => new SkillTool(sessionStore, lf.CreateLogger<SkillTool>()));
        if (full)
        {
            tb.AddTool(lf => new TaskTool(agentRegistry, lf.CreateLogger<TaskTool>(), subAgentRunner, backgroundTasks));
            tb.AddTool(lf => new WebFetchTool(lf.CreateLogger<WebFetchTool>()));
        }
        if (full && sessionStore is not null)
        {
            // #165: peer supervision. The store arrives as a deferred forwarder
            // (built eagerly, attached once the container can build the real
            // store) — same gap as DeferredSubAgentRunner above.
            tb.AddTool(lf => new SessionReadTool(sessionStore, lf.CreateLogger<SessionReadTool>()));
            tb.AddTool(lf => new SessionSteerTool(sessionStore, lf.CreateLogger<SessionSteerTool>()));
        }
        tb.AddTool(lf => new PatchTool(lf.CreateLogger<PatchTool>()));
        tb.AddTool(lf => new NotebookTool(lf.CreateLogger<NotebookTool>()));
        if (full)
        {
            tb.AddTool(lf => new RipGrepTool(lf.CreateLogger<RipGrepTool>()));
        }
        tb.AddTool(lf => new TreeTool(lf.CreateLogger<TreeTool>()));
        if (lspService is not null)
        {
            tb.AddTool(new LspTool(lspService, ctx.LoggerFactory.CreateLogger<LspTool>()));
        }
        if (full)
        {
            tb.AddTool(lf => new McpToolTool(mcpRegistry, lf.CreateLogger<McpToolTool>()));
            tb.AddTool(lf => new McpResourceTool(mcpRegistry, lf.CreateLogger<McpResourceTool>()));
            tb.AddTool(lf => new McpPromptTool(mcpRegistry, lf.CreateLogger<McpPromptTool>()));
        }

        registry.Freeze();
        ctx.Logger.LogInformation("Registered {Count} tools", registry.GetAllTools().Count);
        return registry;
    }
}
