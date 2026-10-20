using Harbor.Application.Observability;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Turns the harness's per-turn change reports into one line each, for the
///     REPL's chat and for the one-shot <c>harbor ask</c> output (issue #1745).
/// </summary>
/// <remarks>
///     <para>
///         <b>What it draws and what it decides.</b> Nothing. The oracle
///         decided what changed and how sure it is; this type only renders
///         that answer. A host with no oracle registered renders nothing,
///         which is honest: there is nothing to report.
///     </para>
///     <para>
///         <b>Why the sequence stamp.</b> A prompt can span several turns, and
///         the reports arrive one per turn. The stamp is what lets a reader
///         ask «which reports have I not shown yet» without comparing turn
///         indices across sessions — two sessions both have a turn 1.
///     </para>
/// </remarks>
internal sealed class TurnChangeSummary
{
    private readonly TurnChangeOracle? _oracle;
    private long _lastSequence;

    /// <summary>
    ///     Create a reader over an oracle. Null is a valid argument: a host
    ///     that registered no oracle then renders nothing.
    /// </summary>
    /// <param name="oracle">The harness-side oracle, when the host has one.</param>
    internal TurnChangeSummary(TurnChangeOracle? oracle)
    {
        _oracle = oracle;
    }

    /// <summary>
    ///     The lines for the reports sealed since the last call, oldest first.
    ///     Empty when the host has no oracle, when nothing changed since
    ///     the last call, or when the reports in between said nothing: a turn
    ///     that touched no file and ran no tool the oracle cannot see through
    ///     has nothing to report, and a line saying «no file changes» in the
    ///     chat would be noise on every turn of every session.
    /// </summary>
    internal IReadOnlyList<string> TakeNewLines()
    {
        if (_oracle is null)
        {
            return [];
        }

        List<string> lines = [];
        foreach (TurnChangeReport report in _oracle.Reports)
        {
            if (report.Sequence <= _lastSequence)
            {
                continue;
            }

            _lastSequence = report.Sequence;
            if (!HasSomethingToSay(report))
            {
                continue;
            }

            lines.Add(Describe(report));
        }

        return lines;
    }

    /// <summary>
    ///     True when the report says something: a path the oracle watched, or a
    ///     tool it could not see through. A report with neither is a turn where
    ///     nothing happened that the oracle could measure.
    /// </summary>
    private static bool HasSomethingToSay(TurnChangeReport report) =>
        report.Files.Count > 0 || report.UnobservedTools.Count > 0;

    /// <summary>
    ///     One line for one report.
    /// </summary>
    /// <param name="report">The report to render.</param>
    internal static string Describe(TurnChangeReport report)
    {
        List<string> changed = [];
        List<string> unchanged = [];
        List<string> unverified = [];

        foreach (WorkspaceChange file in report.Files)
        {
            switch (file.Kind)
            {
                case WorkspaceChangeKind.Created:
                case WorkspaceChangeKind.Modified:
                case WorkspaceChangeKind.Deleted:
                    changed.Add(file.Path);
                    break;
                case WorkspaceChangeKind.Unchanged:
                    unchanged.Add(file.Path);
                    break;
                default:
                    unverified.Add(file.Path);
                    break;
            }
        }

        string line = changed.Count == 0
            ? "no file changes"
            : $"changed {changed.Count} file(s): {string.Join(", ", changed)}";

        List<string> notes = [];
        if (unchanged.Count > 0)
        {
            notes.Add($"declared but unchanged: {Join(unchanged)}");
        }

        if (unverified.Count > 0)
        {
            notes.Add($"unverified: {Join(unverified)}");
        }

        if (report.UnobservedTools.Count > 0)
        {
            notes.Add($"unobserved: {string.Join(", ", report.UnobservedTools)}");
        }

        return notes.Count == 0 ? line : $"{line}; {string.Join("; ", notes)}";
    }

    /// <summary>
    ///     How many paths one note names before the rest is counted instead.
    ///     A turn that wrote forty files must not turn the chat into a list.
    /// </summary>
    private const int MaxNamedPaths = 5;

    /// <summary>
    ///     The paths of one note, bounded.
    /// </summary>
    private static string Join(List<string> paths)
    {
        if (paths.Count <= MaxNamedPaths)
        {
            return string.Join(", ", paths);
        }

        return $"{string.Join(", ", paths.Take(MaxNamedPaths))}, +{paths.Count - MaxNamedPaths} more";
    }
}
