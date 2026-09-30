using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Legacy slash-dispatcher seam (GoF Adapter): the three CellForge call
///     sites (info commands, palette fallback, composer fallback) shared one
///     copy-pasted <c>HandleCoreAsync</c> block with five service lookups each.
///     The dispatcher arrives by ctor — the composition root resolves its nine
///     collaborators and builds it once — so this class holds no container at
///     all (#63). Per-call state (agent, session) travels as arguments because
///     sessions switch mid-run.
/// </summary>
/// <remarks>
///     <para>
///         <b>#486</b> — this class used to carry a second
///         <c>FromServices(IServiceProvider)</c> factory that built its own
///         <see cref="SlashCommandDispatcher" /> from the same nine collaborators, through
///         <c>services.GetRequiredService</c>. Nothing called it, so it was unreachable
///         product code whose body was a service locator — #470's shape, in a third file. The
///         slash layer's one construction site is the composition root, which resolves those
///         nine registered collaborators and hands the result to <see cref="ReplRunner" />.
///     </para>
///     <para>
///         The four collaborators this class still takes are the ones the dispatcher's
///         PER-CALL state is read from; the five it holds itself were closed over the
///         dispatcher's own fields rather than re-boxed per command (#756).
///     </para>
/// </remarks>
internal sealed class LegacySlashRunner
{
    private readonly SlashCommandDispatcher _dispatcher;
    private readonly IAgentRegistry _agents;
    private readonly IConfigStore _config;
    private readonly AuthStore _auth;
    private readonly IProviderRegistry _providers;

    /// <summary>Shared dispatcher (also serves the palette command list).</summary>
    public SlashCommandDispatcher Dispatcher => _dispatcher;

    public LegacySlashRunner(
        SlashCommandDispatcher dispatcher,
        IAgentRegistry agents,
        IConfigStore config,
        AuthStore auth,
        IProviderRegistry providers)
    {
        _dispatcher = dispatcher;
        _agents = agents;
        _config = config;
        _auth = auth;
        _providers = providers;
    }

    public Task<SlashCommandOutcome> RunAsync(
        string text,
        Action<string> writer,
        Func<string, Task<string>> reader,
        IAgent agent,
        Session session)
    {
        return _dispatcher.HandleCoreAsync(
            text, writer, reader,
            agent, _agents, _config, _auth, _providers, session);
    }
}
