using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     #2675 item 022: phase timings for the one-shot <c>harbor ask</c> path.
///     Phases are sequential; background work that overlaps them is reported
///     in its own section and is never added to <c>total</c>. The report goes
///     to stderr so the agent's answer on stdout stays machine-readable.
/// </summary>
internal static class TimingsReport
{
    internal readonly record struct Phase(string Name, TimeSpan Duration);

    /// <summary>Convert a pair of <see cref="Stopwatch.GetTimestamp" /> samples to a duration.</summary>
    internal static TimeSpan Elapsed(long startTimestamp, long endTimestamp)
    {
        double ticks = (endTimestamp - startTimestamp)
            * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        return TimeSpan.FromTicks((long)ticks);
    }

    internal static string Format(IReadOnlyList<Phase> phases, string overlappingWork)
    {
        var report = new StringBuilder();
        report.AppendLine("timings:");
        double totalMs = 0;
        foreach (Phase phase in phases)
        {
            report.Append("  ").Append(phase.Name).Append(": ")
                .Append(FormatMs(phase.Duration)).AppendLine();
            totalMs += phase.Duration.TotalMilliseconds;
        }

        report.Append("  total: ").Append(FormatMs(TimeSpan.FromMilliseconds(totalMs))).AppendLine();
        report.AppendLine("overlapping work (excluded from total):");
        report.Append("  ").AppendLine(overlappingWork);
        return report.ToString();
    }

    private static string FormatMs(TimeSpan duration) =>
        duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms";
}
