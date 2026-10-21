using Harbor.Application.Agents;
using Harbor.Application.Configuration;
using Harbor.Tools.Builtin;
using Harbor.Tools.Mcp;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Logging;
using Harbor.Registries.Agents;
using Harbor.Registries.Tools;
// The plugin hosting assembly is read OUTSIDE the plugin feature flag: the
// runtime catalog names the file the plugin host publishes its live
// composition into, and that name is one constant (#1722).
using Harbor.Plugins.Hosting;

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

        // #1118: the home config's `maxSteps` is the step budget of the runs
        // this configuration starts. It is projected HERE — where the agents
        // are declared — so every consumer of the registry (the REPL, `ask`,
        // `run task`, the `/agent` and `/model` switches, the desktop host)
        // inherits the same budget, and the loop's existing
        // `MaxStepsBehavior.IsExhausted` comparison enforces it. Without this
        // the configured value reached no agent at all and the REPL ran to the
        // agent's own default (50 turns for `code`) whatever the config said.
        ab.AddAgent(AgentStepBudget.Apply(ctx.Harbor, AgentDefinition.CodeDefault(modelId, providerId)));
        ab.AddAgent(AgentStepBudget.Apply(ctx.Harbor, AgentDefinition.PlanDefault(modelId, providerId)));
        ab.AddAgent(AgentStepBudget.Apply(ctx.Harbor, AgentDefinition.ExploreDefault(modelId, providerId)));
        ab.AddAgent(AgentStepBudget.Apply(ctx.Harbor, AgentDefinition.AskDefault(modelId, providerId)));
        ab.AddAgent(AgentStepBudget.Apply(ctx.Harbor, AgentDefinition.ArchitectDefault(modelId, providerId)));
        ab.AddAgent(AgentStepBudget.Apply(ctx.Harbor, AgentDefinition.DebugDefault(modelId, providerId)));
        ab.AddAgent(AgentStepBudget.Apply(ctx.Harbor, AgentDefinition.OrchestratorDefault(modelId, providerId)));
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
    ///     Remote transport strategies (#477), composed at the composition root
    ///     and injected (#1576) — the registry no longer defaults to
    ///     <see cref="McpTransportResolver.Default" /> on its own.
    /// </param>
    internal static IMcpRegistry CreateMcpRegistry(
        HarborCompositionContext ctx, McpTransportResolver transports)
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
                    ToolsCatalogLog.SkippingMcpServer(ctx.Logger, entry.Name, registered.Error);
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
            ToolsCatalogLog.SkippingPluginRoute(ctx.Logger, PluginMcpServerName, registered.Error);
        else
            ToolsCatalogLog.PluginRouteRegistered(ctx.Logger, PluginMcpServerName, hostPath);
    }
#endif

    /// <summary>
    ///     Where the plugin host publishes its live composition (#1722): the
    ///     global plugin root, the file name the product declares. The path
    ///     comes from the composition root and never from the model.
    /// </summary>
    private static string PluginInventoryPath(HarborCompositionContext ctx)
        => Path.Combine(ctx.Options.HarborDir, "plugins", PluginInventory.FileName);

    internal static ToolRegistry CreateToolRegistry(
        HarborCompositionContext ctx, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry,
        IProviderRegistry providerRegistry,
        Harbor.Abstractions.Agents.ISubAgentRunner subAgentRunner,
        Harbor.Abstractions.Agents.IBackgroundTaskRegistry? backgroundTasks = null,
        Harbor.Abstractions.Lsp.ILspService? lspService = null,
        ISessionStore? sessionStore = null,
        ISkillRegistry? skillRegistry = null,
        IInstalledPluginInventory? pluginInventory = null)
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
        // #1587: the generation-point tools (Read/Bash/Grep/WebFetch/Tree) pass
        // through the output-limit decorator — unified char cap + marker applied
        // at the composition root; each tool's own caps stay intact beneath it.
        tb.AddTool(lf => new OutputLimitToolDecorator(
            new ReadTool(lf.CreateLogger<ReadTool>(), lspService),
            lf.CreateLogger<OutputLimitToolDecorator>()));
        tb.AddTool(lf => new WriteTool(lf.CreateLogger<WriteTool>()));
        tb.AddTool(lf => new EditTool(lf.CreateLogger<EditTool>(), lspService));
        // #1590: BashTool доходит до OS-процессов только через Proxy-шов — здесь
        // production SystemProcessProxy; тесты подменяют его сценарием.
        tb.AddTool(lf => new OutputLimitToolDecorator(
            new BashTool(new SystemProcessProxy(), lf.CreateLogger<BashTool>()),
            lf.CreateLogger<OutputLimitToolDecorator>()));
        tb.AddTool(lf => new GlobTool(lf.CreateLogger<GlobTool>()));
        tb.AddTool(lf => new OutputLimitToolDecorator(
            new GrepTool(lf.CreateLogger<GrepTool>()),
            lf.CreateLogger<OutputLimitToolDecorator>()));
        tb.AddTool(lf => new LsTool(lf.CreateLogger<LsTool>()));
        // #1735: the registry is the same object the agent loop resolves, so
        // the skill the model is told about and the skill the model is allowed
        // to load are decided in ONE place. A null registry (a host that
        // composes the tool catalog without one) keeps the tool's own
        // discovery and no policy gate.
        tb.AddTool(lf => new SkillTool(sessionStore, lf.CreateLogger<SkillTool>(), null, null, skillRegistry));
        // #1732: the read-only runtime catalog. It holds the registry it
        // describes — the same object, read at call time — so the tools it
        // lists are the tools that are registered NOW, and a plugin that
        // registered one a moment ago is already in the answer. The facts no
        // registry owns (the plugin axes, the provider catalogue, the versions)
        // are copied out of the product's own declarations by the composition
        // root, so the catalog cannot describe a runtime that is not this one.
        tb.AddTool(lf => new InspectTool(
            registry,
            agentRegistry,
            providerRegistry,
            skillRegistry,
            mcpRegistry,
            PluginInventoryPath(ctx),
            RuntimeApiFactsFactory.Create(),
            lf.CreateLogger<InspectTool>()));
        if (full)
        {
            tb.AddTool(lf => new TaskTool(agentRegistry, lf.CreateLogger<TaskTool>(), subAgentRunner, backgroundTasks));
            tb.AddTool(lf => new OutputLimitToolDecorator(
                new WebFetchTool(lf.CreateLogger<WebFetchTool>()),
                lf.CreateLogger<OutputLimitToolDecorator>()));
        }
        if (full && sessionStore is not null)
        {
            // #165: peer supervision. The store arrives as a deferred forwarder
            // whose Attach lives inside RegistryFacade.Compose (#1592) — the
            // catalog only carries the seam, the two-phase assembly protocol
            // is not this file's business any more.
            tb.AddTool(lf => new SessionReadTool(sessionStore, lf.CreateLogger<SessionReadTool>()));
            tb.AddTool(lf => new SessionSteerTool(sessionStore, lf.CreateLogger<SessionSteerTool>()));
        }

        // #1761: the goal axis. The tool records the agent's verdict on the
        // session's durable goal, so it needs the store's GOAL view — a
        // backend that has none simply does not keep goals, and the tool says
        // so when it is called. It is registered whenever there is a store at
        // all, because the round driver and the tool must agree: a driver
        // that runs without the tool would tell the model to call a tool that
        // is not there.
        tb.AddTool(lf => new GoalTool(
            sessionStore as ISessionGoalStore, lf.CreateLogger<GoalTool>()));
        tb.AddTool(lf => new PatchTool(lf.CreateLogger<PatchTool>()));
        tb.AddTool(lf => new NotebookTool(lf.CreateLogger<NotebookTool>()));
        if (full)
        {
            tb.AddTool(lf => new RipGrepTool(lf.CreateLogger<RipGrepTool>()));
        }
        tb.AddTool(lf => new OutputLimitToolDecorator(
            new TreeTool(lf.CreateLogger<TreeTool>()),
            lf.CreateLogger<OutputLimitToolDecorator>()));
        if (lspService is not null)
        {
            tb.AddTool(new LspTool(lspService, ctx.LoggerFactory.CreateLogger<LspTool>()));
        }
#if HARBOR_WITH_PLUGINS
        // #1738: the agent-facing plugin management tool. It is registered
        // only where the plugin runtime is compiled in, only in the full
        // preset (the desktop subset has no plugin route), and only when an
        // inventory exists — the tool without one could neither read what is
        // installed nor change it.
        if (full && pluginInventory is not null)
        {
            tb.AddTool(lf => new PluginManagerTool(pluginInventory, lf.CreateLogger<PluginManagerTool>()));
        }
#endif
        if (full)
        {
            tb.AddTool(lf => new McpToolTool(mcpRegistry, lf.CreateLogger<McpToolTool>()));
            tb.AddTool(lf => new McpResourceTool(mcpRegistry, lf.CreateLogger<McpResourceTool>()));
            tb.AddTool(lf => new McpPromptTool(mcpRegistry, lf.CreateLogger<McpPromptTool>()));
        }

        registry.Freeze();
        ToolsCatalogLog.ToolsRegistered(ctx.Logger, registry.GetAllTools().Count);
        return registry;
    }
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="ToolsCatalog" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ToolsCatalogLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Skipping MCP server '{Name}': {Error}")]
    public static partial void SkippingMcpServer(ILogger logger, string name, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Skipping out-of-proc plugin route '{Name}': {Error}")]
    public static partial void SkippingPluginRoute(ILogger logger, string name, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "MCP server '{Name}' registered from {Path} (out-of-proc plugin route)")]
    public static partial void PluginRouteRegistered(ILogger logger, string name, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Registered {Count} tools")]
    public static partial void ToolsRegistered(ILogger logger, int count);
}
