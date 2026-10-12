using ConsoleAppFramework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-D (#1289): <c>daemon</c> + <c>status</c> (ipc) + <c>serve</c> +
///     <c>mcp</c> verbs on ConsoleAppFramework. Handler methods only — the ONE
///     <c>RunAsync</c> call site stays in <see cref="CafPilotVerbs" /> (single
///     RunAsync per assembly: the generator keys its emitted file without a
///     per-site disambiguator, so two builder sites collide with CS8785 — see
///     the pilot remarks). Handlers take the raw positional tail and delegate
///     to <c>DaemonCommand</c>/<c>StatusCommand</c>/<c>ServeVerb</c>/
///     <c>McpLoginRunner</c>, so output stays byte-identical;
///     <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     Known deferral (same as CAF-A/CAF-B): tokens starting with <c>-</c> are
///     option candidates to the framework, so a <c>--flag</c> tail the manual
///     parsers used to accept now fails closed at the parser (exit 1) instead
///     of reaching the runner. Bare + error-usage paths are byte-identical
///     (see <c>CafDaemonIpcServeMcpTests</c>); fully typed flags are the
///     follow-up. <c>serve</c> bare starts the foreground host (blocks on the
///     agent host build), so it has no host-free parity path — only the
///     framework <c>--help</c> check.
/// </remarks>
internal static class CafDaemonCommands
{
    /// <summary>Manage the background harbor daemon.</summary>
    public static async Task<int> Daemon([Argument]params string[] args) =>
        await new DaemonCommand(Console.Out, Console.Error).ExecuteAsync(args).ConfigureAwait(false);
}

#if HARBOR_WITH_DAEMON
/// <summary>
///     CAF-D (#1289): <c>status</c> verb handler (ipc discovery lite). See the
///     remarks on <see cref="CafDaemonCommands" /> for the single-builder rule
///     and the <c>--flag</c> deferral. Daemon-gated like the manual
///     registration in <c>Program</c> (Slice A, #1144): the command type leaves
///     the compile when the daemon is off, so this handler does too.
/// </summary>
internal static class CafStatusCommands
{
    /// <summary>Show daemon status (use --all to probe every host).</summary>
    public static async Task<int> Status([Argument]params string[] args) =>
        await new StatusCommand(Console.Out, Console.Error).ExecuteAsync(args).ConfigureAwait(false);
}
#endif

/// <summary>
///     CAF-D (#1289): <c>serve</c> verb handler (foreground daemon alias). See
///     the remarks on <see cref="CafDaemonCommands" /> for the single-builder
///     rule and the <c>--flag</c> deferral.
/// </summary>
internal static class CafServeCommands
{
    /// <summary>Run the Harbor daemon in the foreground.</summary>
    public static async Task<int> Serve([Argument]params string[] args) =>
        await ServeVerb.RunAsync(NullLogger.Instance, args).ConfigureAwait(false);
}

/// <summary>
///     CAF-D (#1289): <c>mcp</c> verb handler (remote MCP OAuth). See the
///     remarks on <see cref="CafDaemonCommands" /> for the single-builder rule
///     and the <c>--flag</c> deferral.
/// </summary>
internal static class CafMcpCommands
{
    /// <summary>Manage remote MCP servers (list, login, logout).</summary>
    public static async Task<int> Mcp([Argument]params string[] args) =>
        await McpLoginRunner.RunAsync(Console.Out, Console.Error, args).ConfigureAwait(false);
}
