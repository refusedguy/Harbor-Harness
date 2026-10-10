using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor run reject &lt;RunId&gt; (--patch | --worktree | --all-effects | --undo-apply) [--force]</c>
///     (epic #42, slice S7, #385): rejection is three separately invocable
///     operations, never one blurred action. Slice 1 routes the modes and
///     implements <c>--all-effects</c> (prints the out-of-reach inventory,
///     read-only, exit 5); the frozen-set modes fail closed naming the
///     slice that owns them. Exactly one mode flag is required — a bare
///     call prints the three modes and exits 2; there is no default.
///     Reject never touches the operator tree by default.
/// </summary>
internal static class RunRejectVerb
{
    internal static int Run(string[] args)
    {
        string? runId = null;
        string? mode = null;
        bool force = false;
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a is "--force")
            {
                force = true;
                i++;
                continue;
            }
            if (a is "--patch" or "--worktree" or "--all-effects" or "--undo-apply")
            {
                if (mode is not null)
                {
                    Console.Error.WriteLine($"Pass exactly one reject mode, not '{mode}' and '{a}'.");
                    PrintUsage();
                    return 2;
                }
                mode = a;
                i++;
                continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unknown option '{a}'.");
                PrintUsage();
                return 2;
            }
            if (runId is null)
            {
                runId = a;
                i++;
                continue;
            }
            Console.Error.WriteLine($"Unexpected argument '{a}': reject takes a single <RunId>.");
            PrintUsage();
            return 2;
        }

        RejectResult result = RunReject.Decide(runId, mode, force);
        if (result.ExitCode is 0 or 5)
            Console.WriteLine(result.Message);
        else
            Console.Error.WriteLine(result.Message);
        return result.ExitCode;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: harbor run reject <RunId> (--patch | --worktree | --all-effects | --undo-apply) [--force]");
        Console.Error.WriteLine("Example: harbor run reject 9f2c41 --all-effects");
        Console.Error.WriteLine(
            "Reject never touches the operator tree by default: --patch drops only the run's " +
            "artifacts, --worktree removes only the isolated copy, --all-effects only prints.");
    }
}
