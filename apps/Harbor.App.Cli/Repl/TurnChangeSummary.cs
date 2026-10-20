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
    ///     Empty when the host has no oracle, or when nothing changed since
    ///     the last call.
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

            lines.Add(Describe(report));
            _lastSequence = report.Sequence;
        }

        return lines;
    }

    /// <summary>
    ///     One line for one report.
    /// </summary>
    /// <param name="report">The report to render.</param>
    internal static string Describe(TurnChangeReport report)
    {
        List<string> changed = [];
        int unchanged = 0;
        int unverified = 0;

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
                    unchanged++;
                    break;
                default:
                    unverified++;
                    break;
            }
        }

        string line = changed.Count == 0
            ? "no file changes"
            : $"changed {changed.Count} file(s): {string.Join(", ", changed)}";

        List<string> notes = [];
        if (unchanged > 0)
        {
            notes.Add($"{unchanged} declared path(s) unchanged");
        }

        if (unverified > 0)
        {
            notes.Add($"{unverified} path(s) unverified");
        }

        if (report.UnobservedTools.Count > 0)
        {
            notes.Add($"unobserved: {string.Join(", ", report.UnobservedTools)}");
        }

        return notes.Count == 0 ? line : $"{line}; {string.Join("; ", notes)}";
    }
}
