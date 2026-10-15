using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-A (#1286): <c>run</c> verb on ConsoleAppFramework. Handler method
///     only — the ONE <c>RunAsync</c> call site stays in
///     <see cref="CafPilotVerbs" /> (single RunAsync per assembly: the generator
///     keys its emitted file without a per-site disambiguator, so two builder
///     sites collide with CS8785 — see the pilot remarks).
///     The handler takes the raw positional tail and delegates to
///     <c>RunTaskVerb</c>, so output stays byte-identical;
///     <c>-h|--help</c> comes from the framework.
///     (<c>sessions</c> used to live here too; CAF-F (#1288) moved it to
///     <c>CafSessionsVerbs</c> with a typed <c>--session</c> flag.)
/// </summary>
/// <remarks>
///     Known deferral: tokens starting with <c>-</c> are option candidates to
///     the framework, so a <c>--flag</c> tail the manual parsers used to accept
///     (e.g. <c>run accept id --dry-run</c>)
///     now fails closed at the parser (exit 1) instead of reaching the runner.
///     Bare + error-usage paths are byte-identical (see
///     <c>CafSessionRunTests</c>); fully typed flags are the follow-up.
/// </remarks>
internal static class CafRunCommands
{
    /// <summary>Run sub-agent tasks and manage isolated runs.</summary>
    public static async Task<int> Run([Argument]params string[] args) =>
        await RunTaskVerb.RunAsync(args).ConfigureAwait(false);
}
