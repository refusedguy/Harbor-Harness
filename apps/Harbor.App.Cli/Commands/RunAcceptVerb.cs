using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor run accept &lt;RunId&gt; [--dry-run] [--reverify]</c>
///     (epic #42, slice S6, #382): apply the frozen <c>change.patch</c> to the
///     operator's tree behind the base-unchanged gate. The patch lands as
///     uncommitted staged changes in the operator's tree. No auto-commit, no
///     auto-branch, no <c>git stash</c>, no implicit <c>git checkout</c>.
///     Exit codes: 0 applied (or dry-run printed); 2 bad usage (missing run id,
///     unknown option, unknown run); 3 pre-flight conflict (base moved, dirty
///     tree); 4 named-stage failure (not Reported, no patch, re-verify
///     unavailable, apply failed).
/// </summary>
internal static class RunAcceptVerb
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? runId = null;
        bool dryRun = false;
        bool reverify = false;
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a is "--dry-run")
            {
                dryRun = true;
                i++;
                continue;
            }
            if (a is "--reverify")
            {
                reverify = true;
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
            Console.Error.WriteLine($"Unexpected argument '{a}': accept takes a single <RunId>.");
            PrintUsage();
            return 2;
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            PrintUsage();
            return 2;
        }

        AcceptResult result = await ChangeApplier.AcceptAsync(runId, dryRun, reverify).ConfigureAwait(false);
        if (result.ExitCode == 0)
            Console.WriteLine(result.Message);
        else
            Console.Error.WriteLine(result.Message);
        return result.ExitCode;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: harbor run accept <RunId> [--dry-run] [--reverify]");
        Console.Error.WriteLine("Example: harbor run accept 9f2c41 --dry-run");
        Console.Error.WriteLine(
            "The patch lands as uncommitted staged changes in the operator's tree. " +
            "No auto-commit, no auto-branch, no git stash, no implicit git checkout.");
    }
}
