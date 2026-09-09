using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Legacy slash-dispatcher seam (GoF Adapter): the three CellForge call
///     sites (info commands, palette fallback, composer fallback) shared one
///     copy-pasted <c>HandleCoreAsync</c> block with five service lookups each.
///     All dependencies resolve ONCE via <see cref="FromServices"/>; per-call
///     state (agent, session) travels as arguments because sessions switch
///     mid-run. The <c>IServiceProvider</c> field is the contained legacy seam —
///     <c>HandleCoreAsync</c> demands it for <c>IToolRegistry</c>.
/// </summary>
internal sealed class LegacySlashRunner
{
    private readonly SlashCommandDispatcher _dispatcher;
    private readonly IServiceProvider _services;
    private readonly IAgentRegistry _agents;
    private readonly IConfigStore _config;
    private readonly AuthStore _auth;
    private readonly IProviderRegistry _providers;

    /// <summary>Shared dispatcher (also serves the palette command list).</summary>
    public SlashCommandDispatcher Dispatcher => _dispatcher;

    public LegacySlashRunner(
        SlashCommandDispatcher dispatcher,
        IServiceProvider services,
        IAgentRegistry agents,
        IConfigStore config,
        AuthStore auth,
        IProviderRegistry providers)
    {
        _dispatcher = dispatcher;
        _services = services;
        _agents = agents;
        _config = config;
        _auth = auth;
        _providers = providers;
    }

    /// <summary>Composition-root factory: single resolution point.</summary>
    public static LegacySlashRunner FromServices(IServiceProvider services) => new(
        new SlashCommandDispatcher(services.GetRequiredService<ILogger<SlashCommandDispatcher>>()),
        services,
        services.GetRequiredService<IAgentRegistry>(),
        services.GetRequiredService<IConfigStore>(),
        services.GetRequiredService<AuthStore>(),
        services.GetRequiredService<IProviderRegistry>());

    public Task<SlashCommandOutcome> RunAsync(
        string text,
        Action<string> writer,
        Func<string, Task<string>> reader,
        IAgent agent,
        Session session)
    {
        return _dispatcher.HandleCoreAsync(
            text, _services, writer, reader,
            agent, _agents, _config, _auth, _providers, session);
    }
}
