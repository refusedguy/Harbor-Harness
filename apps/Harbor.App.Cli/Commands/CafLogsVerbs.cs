using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-C (#1288): <c>logs</c> verb on ConsoleAppFramework. Handler method
///     only — the ONE <c>RunAsync</c> call site stays in
///     <see cref="CafPilotVerbs" /> (single RunAsync per assembly: the generator
///     keys its emitted file without a per-site disambiguator, so two builder
///     sites collide with CS8785 — see the pilot remarks). The handler declares
///     the verb's flags explicitly and delegates to <c>LogsCommand</c>, so
///     output stays byte-identical; <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     Unlike CAF-A/CAF-B (raw <c>[Argument]params</c> tails with a
///     <c>--flag</c> fail-closed deferral), <c>logs</c> IS flags: bare,
///     <c>--list</c>, <c>--last</c>, <c>--follow</c> and <c>--clean</c> are the
///     whole interface, so a raw tail would fail the primary paths at the
///     parser (a non-digit <c>-</c> token is an option candidate, never
///     positional). The four flags are typed here (single-letter aliases via
///     the doc comments) and rebuilt into the manual tail in manual precedence
///     order (list, last, follow, clean); anything else rides the positional
///     tail untouched. Multi-flag combos normalize to the first set flag while
///     the manual parser reads only <c>args[0]</c> — that interleave is the
///     one documented divergence; single-flag + bare + unknown-sub paths are
///     byte-identical (see <c>CafLogsTests</c>). Fully order-preserving tails
///     are the follow-up.
/// </remarks>
internal static class CafLogsCommands
{
    /// <summary>View and manage per-run log files.</summary>
    /// <param name="list">-l, List all log files, newest first.</param>
    /// <param name="last">-n, Print the most recent log file.</param>
    /// <param name="follow">-f, Tail the most recent log file (Ctrl-C to exit).</param>
    /// <param name="clean">-c, Delete all log files (prompts for confirmation).</param>
    /// <param name="args">Positional tail (unknown subcommands).</param>
    public static async Task<int> Logs(
        bool list = false,
        bool last = false,
        bool follow = false,
        bool clean = false,
        [Argument]params string[] args)
    {
        string[] tail = list ? ["--list", ..args]
            : last ? ["--last", ..args]
            : follow ? ["--follow", ..args]
            : clean ? ["--clean", ..args]
            : args;
        return await new LogsCommand(Console.Out, Console.Error).ExecuteAsync(tail).ConfigureAwait(false);
    }
}
