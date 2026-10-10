using CSharpFunctionalExtensions;
using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor run owner-report &lt;RunId&gt;</c> (epic #42, slice S8, #392):
///     print the persisted <c>owner-report.md</c> as plain text (80 columns, no
///     ANSI; identical when redirected). Read-only: nothing is generated here,
///     and the text is checked by the structural guard on load — an unknown id
///     or a corrupt file is exit 2, never a half report.
///     Exit codes: 0 printed; 2 bad usage (missing run id, unknown option,
///     unknown run, corrupt report).
/// </summary>
internal static class RunOwnerReportVerb
{
    internal static int Run(string[] args)
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
            Console.Error.WriteLine($"Unexpected argument '{a}': owner-report takes a single <RunId>.");
            PrintUsage();
            return 2;
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            PrintUsage();
            return 2;
        }

        Result<string> loaded = OwnerReport.TryLoad(runId);
        if (loaded.IsFailure)
        {
            Console.Error.WriteLine(loaded.Error);
            return 2;
        }
        Console.WriteLine(loaded.Value);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: harbor run owner-report <RunId>");
        Console.Error.WriteLine("Example: harbor run owner-report 9f2c41");
        Console.Error.WriteLine(
            "Prints the persisted owner report (result, evidence, limits, rule changes, " +
            "recommendation + rollback) as plain text. Read-only: nothing is generated.");
    }
}
