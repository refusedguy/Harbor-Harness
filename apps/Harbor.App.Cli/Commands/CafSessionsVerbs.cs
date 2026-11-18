using System.Globalization;
using ConsoleAppFramework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-F (#1288): <c>sessions</c> verb on ConsoleAppFramework. Handler
///     method only — the ONE <c>RunAsync</c> call site stays in
///     <see cref="CafPilotVerbs" /> (single RunAsync per assembly: the
///     generator keys its emitted file without a per-site disambiguator, so
///     two builder sites collide with CS8785 — see the pilot remarks).
///     Moved out of <c>CafSessionRunVerbs</c> (CAF-A, #1286) so the issue
///     owning the verb owns the file; <c>run</c> stays where it was.
///     The handler delegates to <c>SessionsVerb</c>, so output stays
///     byte-identical; <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     Unlike CAF-A (raw <c>[Argument]params</c> tail with a <c>--flag</c>
///     fail-closed deferral), the <c>search</c> subcommand IS flags — the
///     manual parser accepts <c>--session</c>/<c>-s</c> anywhere in the
///     sub-args, and a raw tail fails the primary
///     <c>sessions search q --session id</c> path at the parser (a dash-led
///     token is an option candidate, never positional). So the flag is typed
///     here (with the <c>-s</c> alias the manual parser accepts) and rebuilt
///     into the manual tail: for <c>search</c> the value rides after the
///     query words (the manual scan is order-insensitive, so any flag
///     position lands on the same query); for any other subcommand — or no
///     subcommand — the flag is re-attached at the front, where the manual
///     dispatch reads it as an unknown sub and prints the static usage
///     (exit 2), exactly as the manual verb does for the same input.
///     Documented divergences (same family as CAF-A/B/D/E): a verb-flag-first
///     <c>sessions --session id search q</c> runs the search instead of
///     printing the manual unknown-sub usage (exit 2), and tokens the
///     framework cannot bind (an unknown <c>--flag</c>, a dash-led
///     <c>--session</c> value) fail closed at the parser (exit 1) instead of
///     reaching the runner. Bare + error-usage + unknown-positional paths
///     are byte-identical (see <c>CafSessionRunTests</c>).
/// </remarks>
internal static class CafSessionCommands
{
    /// <summary>List, rename, export, import, search, rewind and maintain sessions.</summary>
    /// <param name="session">-s, Restrict 'search' to the session whose id contains this value.</param>
    /// <param name="olderThanDays">For 'maintenance', delete sessions inactive for more than this many days.</param>
    /// <param name="keepRecent">For 'maintenance', always keep this many recent sessions.</param>
    /// <param name="apply">For 'maintenance', actually delete instead of only printing the plan.</param>
    /// <param name="args">Positional tail (subcommand and its arguments).</param>
    public static async Task<int> Sessions(
        string? session = null,
        int? olderThanDays = null,
        int? keepRecent = null,
        bool apply = false,
        [Argument]params string[] args)
    {
        string[] tail = session is null ? args
            : args.Length > 0 && args[0].Equals("search", StringComparison.OrdinalIgnoreCase)
                ? ["search", ..args.Skip(1), "--session", session]
                : ["--session", session, ..args];

        // The maintenance flags are typed for CAF so a dash-led token never
        // fails at the parser. Rebuild them into the manual flag tail only for
        // maintenance; every other subcommand keeps its existing byte behavior.
        if (args.Length > 0 && args[0].Equals("maintenance", StringComparison.OrdinalIgnoreCase))
        {
            var rebuilt = new List<string>(tail);
            if (olderThanDays is not null)
            {
                rebuilt.Add("--older-than-days");
                rebuilt.Add(olderThanDays.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (keepRecent is not null)
            {
                rebuilt.Add("--keep-recent");
                rebuilt.Add(keepRecent.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (apply) rebuilt.Add("--apply");
            tail = [.. rebuilt];
        }

        return await SessionsVerb.RunAsync(NullLogger.Instance, tail).ConfigureAwait(false);
    }
}
