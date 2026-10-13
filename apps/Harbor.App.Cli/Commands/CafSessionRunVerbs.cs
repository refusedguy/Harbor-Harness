using ConsoleAppFramework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-A (#1286): <c>sessions</c> + <c>run</c> verbs on ConsoleAppFramework.
///     Handler methods only — the ONE <c>RunAsync</c> call site stays in
///     <see cref="CafPilotVerbs" /> (single RunAsync per assembly: the generator
///     keys its emitted file without a per-site disambiguator, so two builder
///     sites collide with CS8785 — see the pilot remarks).
///     Handlers take the raw positional tail and delegate to
///     <c>SessionsVerb</c>/<c>RunTaskVerb</c>, so output stays byte-identical;
///     <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     Known deferral: tokens starting with <c>-</c> are option candidates to
///     the framework, so a <c>--flag</c> tail the manual parsers used to accept
///     (e.g. <c>sessions search q --session id</c>, <c>run accept id --dry-run</c>)
///     now fails closed at the parser (exit 1) instead of reaching the runner.
///     Bare + error-usage paths are byte-identical (see
///     <c>CafSessionRunTests</c>); fully typed flags are the follow-up.
/// </remarks>
internal static class CafSessionCommands
{
    /// <summary>List, rename, export, import, search and rewind sessions.</summary>
    public static async Task<int> Sessions([Argument]params string[] args) =>
        await SessionsVerb.RunAsync(NullLogger.Instance, args).ConfigureAwait(false);
}

/// <summary>
///     CAF-A (#1286): <c>run</c> verb handlers (task/list/accept/reject/
///     owner-report/report/change). See the remarks on <see cref="CafSessionCommands" />
///     for the single-builder rule and the <c>--flag</c> deferral.
/// </summary>
internal static class CafRunCommands
{
    /// <summary>Run sub-agent tasks and manage isolated runs.</summary>
    public static async Task<int> Run([Argument]params string[] args) =>
        await RunTaskVerb.RunAsync(args).ConfigureAwait(false);
}
