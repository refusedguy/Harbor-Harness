using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Telemetry;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// DI014/DI016 (Excubo): the registries are eager artifacts, so a temporary
// provider is constructed deliberately to resolve logger factories for
// registry construction (documented pattern) — same story as RegistriesModule.
#pragma warning disable DI014, DI016

namespace Harbor.Hosting;

/// <summary>
///     Facade (#1592): the SINGLE build step of the eager registries. Builds
///     agents / mcp / tools / providers / panels, wires the deferred
///     forwarders for the not-yet-constructible dependencies (#165,
///     F4-decouple), freezes, and publishes everything as singletons
///     (di-design §3.5 order). The forwarders are created and attached
///     HERE — the attach-wiring is part of this step, and the graph that
///     leaves the method already contains the real <c>ISubAgentRunner</c>
///     rather than the stubs: the two-phase assembly protocol is a
///     facade-internal detail, not a public seam.
/// </summary>
internal static class RegistryFacade
{
    /// <summary>
    ///     One step: ready registries without stubs into <paramref name="services" />
    ///     and <paramref name="ctx" />.
    /// </summary>
    internal static void Compose(IServiceCollection services, HarborCompositionContext ctx)
    {
        var agentRegistry = ToolsCatalog.CreateAgentRegistry(ctx);

        // #477/#1576: remote MCP transports are a registration seam. The resolver is
        // built once here (the composition root, #1576) from the builtins +
        // whatever the host passed through HarborComposeOptions.McpTransports,
        // injected into the registry (which both validates the config name and
        // dispatches on it) and published into the container so plugins/hosts
        // can enumerate the same set.
        var mcpTransports = McpTransportResolver.Compose(ctx.Options.McpTransports);
        foreach (IMcpTransportFactory mcpTransport in mcpTransports.Factories)
            services.AddSingleton<IMcpTransportFactory>(mcpTransport);
        services.AddSingleton(mcpTransports);
        RegistriesModuleLog.McpTransportsRegistered(ctx.Logger, string.Join(", ", mcpTransports.SupportedNames));

        var mcpRegistry = ToolsCatalog.CreateMcpRegistry(ctx, mcpTransports);
        // #1592 (facade detail): the deferred pair. The tools are built EAGERLY
        // here, but their real dependencies (ISessionStore — registered later
        // in AddHarborStorage; IAgentLoop — DI-built) only exist inside the
        // container. The forwarders close that gap: the tools hold them now,
        // and the real instances attach at the single attach point below —
        // the first resolution of ISubAgentRunner in the container.
        var subAgents = new Harbor.Application.Agents.DeferredSubAgentRunner();
        // #165: peer-supervision tools hold the other forwarder of the same
        // pair until the container can build the real store (same
        // eager-registry gap — both attach points live in this step).
        var supervisedSessions = new Harbor.Tools.Builtin.DeferredSessionStore();
        var backgroundTasks = new Harbor.Application.Agents.BackgroundTaskRegistry(
            ctx.LoggerFactory.CreateLogger<Harbor.Application.Agents.BackgroundTaskRegistry>());
        services.AddSingleton<Harbor.Abstractions.Agents.IBackgroundTaskRegistry>(backgroundTasks);
        // LSP facade: one manager for the whole process — the `lsp` tool and the
        // desktop editor share the same auto-spawned language servers + cache.
        var lspService = new Harbor.Lsp.LspManager(
            ctx.LoggerFactory.CreateLogger<Harbor.Lsp.LspManager>());
        services.AddSingleton<Harbor.Abstractions.Lsp.ILspService>(lspService);

        // #1735: the skill provider and the registry over it are built HERE,
        // eagerly, for the same reason the tool registry is: the skill tool
        // is constructed in the next step and needs the registry, and the
        // registry needs its providers. Both are plain objects — neither the
        // provider (it reads the workspace roots through the shared
        // WorkspaceContextSource) nor the registry (it merges what its
        // providers hand it) needs the container.
        //
        // One instance each, published as singletons: the registry the tool
        // holds and the registry the agent loop resolves are the same object,
        // so the model-facing catalog and the model-facing call cannot drift
        // apart. The product ships the EMPTY invocation matrix here — the
        // per-skill policy in each skill file's front-matter is what narrows a
        // skill today; the matrix is the layer that narrows by request
        // context once a host has rows to give it.
        var skillProvider = new Harbor.Application.Skills.SkillProvider();
        services.AddSingleton<ISkillProvider>(skillProvider);
        var skillRegistry = new Harbor.Application.Skills.SkillRegistry(
            [skillProvider],
            Harbor.Application.Skills.SkillPolicyMatrix.Empty,
            ctx.LoggerFactory.CreateLogger<Harbor.Application.Skills.SkillRegistry>());
        services.AddSingleton<ISkillRegistry>(skillRegistry);

        // #1759: the question channel the model asks the human through. It is
        // built HERE, eagerly, for the same reason the tool registry is: the
        // ask_user_question tool needs it while the registry is being built,
        // and the registry is frozen before the container exists. The instance
        // is published so the host can attach its presenter (the surface that
        // shows the question) once it knows which UI it has. A headless run
        // never attaches one, and every question is then answered
        // "there is nobody here to ask" instead of hanging.
        var questionService = new Harbor.Application.Interaction.UserQuestionService(
            ctx.LoggerFactory.CreateLogger<Harbor.Application.Interaction.UserQuestionService>());
        services.AddSingleton(questionService);
        services.AddSingleton<Harbor.Abstractions.Interaction.IUserQuestionService>(
            sp => sp.GetRequiredService<Harbor.Application.Interaction.UserQuestionService>());

        var toolRegistry = ToolsCatalog.CreateToolRegistry(ctx, mcpRegistry, agentRegistry, questionService, subAgents, backgroundTasks, lspService, supervisedSessions, skillRegistry);
        // The single attach point of both forwarders: the container resolves
        // ISubAgentRunner only after ISessionStore exists, so the attach runs
        // exactly once and inside the facade — HostBuilder's warmup
        // resolution does exactly that on startup.
        services.AddSingleton<Harbor.Abstractions.Agents.ISubAgentRunner>(sp =>
        {
            var store = sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>();
            var real = new Harbor.Application.Agents.SubAgentRunner(
                store,
                sp.GetRequiredService<Harbor.Abstractions.Agents.IAgentLoop>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger<Harbor.Application.Agents.SubAgentRunner>());
            subAgents.Attach(real);
            supervisedSessions.Attach(store);
            return real;
        });
        var providerRegistry = ProviderFactories.CreateProviderRegistry(ctx, services);
        var panelRegistry = new PanelRegistry(ctx.LoggerFactory.CreateLogger<PanelRegistry>());

        // #562: publish the bundle before Freeze() so a module that runs
        // before this one reads ctx.Registries gets a named
        // InvalidOperationException rather than a member typed non-nullable
        // and holding null. (#1055s3: no in-process plugin pipeline mutates
        // the registries any more — plugin tools arrive over MCP, and the
        // plugin-contributed backend maps stay at their empty defaults.)
        //
        // Assigning the bundle before Freeze() is deliberate and does not weaken
        // the §3.5 invariant: Freeze() mutates the registry OBJECTS, and every
        // consumer of the four registries gets them either from the
        // instrumented views published below (after the freeze) or from a later
        // module in AddHarbor's chain.
        ctx.SetRegistries(new HarborRegistries(agentRegistry, toolRegistry, providerRegistry, panelRegistry));

        toolRegistry.Freeze();
        providerRegistry.Freeze();

        // sprint3-C C1: instrument at the DI boundary. Consumers resolving
        // the interfaces get the instrumented views, never the raw registries.
        services.AddSingleton<IToolRegistry>(new InstrumentedToolRegistry(
            toolRegistry, MeterMetrics.Instance, ActivityTracer.Instance));
        services.AddSingleton<IProviderRegistry>(new InstrumentedProviderRegistry(
            providerRegistry, MeterMetrics.Instance, ActivityTracer.Instance));
        services.AddSingleton<IAgentRegistry>(agentRegistry);
        services.AddSingleton<IMcpRegistry>(mcpRegistry);
        services.AddSingleton(panelRegistry);
        services.AddSingleton<IPanelRegistry>(panelRegistry);
    }
}
