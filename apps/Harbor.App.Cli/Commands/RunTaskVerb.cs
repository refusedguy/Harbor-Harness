using Harbor.App.Cli.Hosting;
using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor run task agent=&lt;name&gt; &lt;prompt&gt;</c> — drive the sub-agent
///     runner directly (same isolation path as the <c>task</c> tool) without a
///     parent agent turn. Extracted from <c>Program</c> (#176), 1:1 behavior.
///     <c>harbor run list</c> (S2, #376) prints the persisted run manifests.
///     <c>harbor run change</c> (S9 slice 1, #397) pins and isolates one
///     verified-change run, then stops fail-closed past <c>Isolated</c>.
///     <c>harbor run reject</c> (S7 slice 1, #385) routes the three reject
///     modes; only <c>--all-effects</c> is implemented yet.
/// </summary>
internal static class RunTaskVerb
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        if (sub == "list")
            return RunList();
        if (sub == "reject")
            return RunRejectVerb.Run(args.Skip(1).ToArray());
        if (sub == "change")
            return await RunChangeVerb.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        if (sub != "task")
        {
            Console.Error.WriteLine("""
                                    Usage: harbor run task agent=<name> <prompt>
                                      run task agent=explore "find all .cs files"
                                      harbor run change agent=<name> "<task>" [--checks <file>] [--dry-run] [--repo <path>]
                                      harbor run reject <RunId> (--patch | --worktree | --all-effects) [--force]
                                      harbor run list
                                    """);
            return sub.Length == 0 ? 2 : 1;
        }

        CliArgs.MarkApproverless(); // #52: one-shot, frame loop не крутится
        using var host = HostBuilder.Build(args);
        return await TaskRunRunner.RunAsync(Console.Out, Console.Error, host.Services, args.Skip(1).ToArray())
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Operator view over the persisted S2 run manifests:
    ///     <c>RunId / state / path / base-rev</c>, oldest first.
    /// </summary>
    private static int RunList()
    {
        var runs = WorkspaceMaterializer.ListRuns();
        foreach (var run in runs)
            Console.WriteLine($"{run.RunId} {run.State} {run.WorktreePath} {run.BaseRevision}");
        return 0;
    }
}
