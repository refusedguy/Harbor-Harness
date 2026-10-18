using Harbor.Application.Sessions;
using Harbor.ChangeTracking;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor run report &lt;RunId&gt;</c> (epic #42, slice S5 remainder,
///     #379, driven end to end by S9, #397): render the verification report
///     from the recorded artifacts only — the frozen change set (S3) and the
///     recorded checks (S4) — persist <c>report.json</c> plus
///     <c>report.txt</c> beside the manifest, and move the manifest
///     <c>Checked -&gt; Reported</c>. Read-only apart from the report
///     artifacts and the single state advance: nothing is authored, the
///     agent has no path to add or remove entries.
///     Exit codes: 0 all declared checks passed (zero declared checks is
///     legal); 1 at least one check failed, timed out, or never ran;
///     2 bad usage (missing run id, unknown option, unknown run) or the
///     report cannot be produced (missing or corrupt artifact, illegal
///     state). Stdout carries exactly the stored plain text (80 columns,
///     no ANSI), so it is safe to pipe.
/// </summary>
internal static class RunReportVerb
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? runId = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unknown option '{a}'.");
                PrintUsage();
                return 2;
            }
            if (runId is null)
            {
                runId = a;
                continue;
            }
            Console.Error.WriteLine($"Unexpected argument '{a}': report takes a single <RunId>.");
            PrintUsage();
            return 2;
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            PrintUsage();
            return 2;
        }

        ReportResult result = await RunReporter.ReportAsync(runId).ConfigureAwait(false);
        if (result.Outcome == ReportOutcome.Reported)
        {
            Console.Write(result.Text);
            Console.Error.WriteLine(result.Message);
            return result.ExitCode;
        }
        Console.Error.WriteLine(result.Message);
        return result.ExitCode;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: harbor run report <RunId>");
        Console.Error.WriteLine("Example: harbor run report 9f2c41");
        Console.Error.WriteLine(
            "Renders the verification report (changed, verified, not verified, limits, " +
            "known risks) from the frozen change set and the recorded checks. Read-only " +
            "apart from the report artifacts and the Checked -> Reported advance.");
    }
}
