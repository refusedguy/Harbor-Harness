using System.Globalization;
using ConsoleAppFramework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-E (#1290): <c>setup</c> + <c>tui</c> + <c>demo</c> verbs on
///     ConsoleAppFramework. Handler methods only — the ONE <c>RunAsync</c>
///     call site stays in <see cref="CafPilotVerbs" /> (single RunAsync per
///     assembly: the generator keys its emitted file without a per-site
///     disambiguator, so two builder sites collide with CS8785 — see the
///     pilot remarks). Handlers delegate to <c>SetupVerb</c>/
///     <c>TuiAttachVerb</c>/<c>DemoCommand</c>, so output stays byte-identical;
///     <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     <para>
///         Unlike CAF-A/CAF-B/CAF-D (raw <c>[Argument]params</c> tails with a
///         <c>--flag</c> fail-closed deferral), <c>tui</c> and <c>demo</c> ARE
///         flags, so the flags are typed here: <c>tui</c> takes
///         <c>--session</c>/<c>--since</c>, <c>demo</c> takes
///         <c>--scene</c>/<c>--tui</c>/<c>--chunk-delay</c>, and the values
///         are rebuilt into the manual tail in manual precedence order;
///         anything else rides the positional tail untouched.
///     </para>
///     <para>
///         Known deferral (same family as CAF-A/CAF-B): tokens the framework
///         cannot bind fail closed at the parser (exit 1) instead of reaching
///         the runner — an unparsable <c>--since</c>/<c>--chunk-delay</c>
///         value, or an unknown <c>--flag</c>, no longer prints the manual
///         error text (exit 2). String-valued flags (<c>--scene</c>,
///         <c>--tui</c>, <c>--session</c>) always bind, so their bad-value
///         errors stay byte-identical (see
///         <c>CafSetupTuiDemoTests</c>). <c>setup</c> takes no flags: the tail
///         is ignored exactly like the manual dispatch, and only the
///         framework <c>--help</c> check is pinned (the wizard builds the
///         host, so it has no host-free parity path — same as <c>serve</c> in
///         CAF-D). The old dispatcher stays until the follow-up removes it
///         (remaining manual verbs: ask/ide/headless/events/logs/plugins/skills
///         — see #1290).
///     </para>
/// </remarks>
internal static class CafSetupCommands
{
    /// <summary>Run the onboarding wizard.</summary>
    public static async Task<int> Setup([Argument]params string[] args) =>
        await SetupVerb.RunAsync(NullLogger.Instance).ConfigureAwait(false);
}

/// <summary>
///     CAF-E (#1290): <c>tui</c> verb handler (daemon attach). See the remarks
///     on <see cref="CafSetupCommands" /> for the single-builder rule and the
///     <c>--flag</c> deferral.
/// </summary>
internal static class CafTuiCommands
{
    /// <summary>Attach to a live daemon and stream its events.</summary>
    /// <param name="session">-s, Bind the agent loop to this session before streaming.</param>
    /// <param name="since">Replay envelopes newer than this server sequence before going live.</param>
    /// <param name="args">Positional tail (unknown subcommands).</param>
    public static async Task<int> Tui(
        string? session = null,
        ulong? since = null,
        [Argument]params string[] args)
    {
        string[] tail = session is not null ? ["--session", session, ..args] : args;
        tail = since is not null ? ["--since", since.Value.ToString(CultureInfo.InvariantCulture), ..tail] : tail;
        return await TuiAttachVerb.RunAsync(tail).ConfigureAwait(false);
    }
}

/// <summary>
///     CAF-E (#1290): <c>demo</c> verb handler (scripted showcase). See the
///     remarks on <see cref="CafSetupCommands" /> for the single-builder rule
///     and the <c>--flag</c> deferral.
/// </summary>
internal static class CafDemoCommands
{
    /// <summary>Play the scripted demo (mock LLM, no API keys).</summary>
    /// <param name="scene">-s, Scene to play: hero, markdown, approval or all.</param>
    /// <param name="tui">-t, Renderer to drive: ansi, plain or cellforge.</param>
    /// <param name="chunkDelay">Delay between mock-LLM chunks, in milliseconds.</param>
    /// <param name="args">Positional tail (unknown subcommands).</param>
    public static async Task<int> Demo(
        string scene = "all",
        string tui = "ansi",
        int chunkDelay = 30,
        [Argument]params string[] args)
    {
        string[] tail = ["--scene", scene, "--tui", tui, "--chunk-delay", chunkDelay.ToString(CultureInfo.InvariantCulture), ..args];
        return await new DemoCommand(Console.Out, Console.Error).ExecuteAsync(tail).ConfigureAwait(false);
    }
}
