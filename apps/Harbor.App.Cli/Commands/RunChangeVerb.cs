using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor run change agent=&lt;name&gt; "&lt;task&gt;" [--checks &lt;file&gt;] [--dry-run] [--repo &lt;path&gt;]</c>
///     (epic #42, slice S9, #397): one command driving the verified-change
///     chain end to end. This slice implements pin (S1), isolate (S2),
///     freeze (S3, #377) and checks (S4, #378) and then stops fail-closed:
///     report (S5) through owner report (S8) are not wired into this command
///     yet, so the run is left honestly at <c>Checked</c> and the command
///     exits 4 naming the stage. Nothing is faked, no step is skipped to
///     "success", and <c>harbor run list</c> shows the run with its last
///     completed transition.
///     Exit codes: 0 dry-run plan printed; 2 bad usage; 3 pre-flight
///     conflict (dirty workspace, moved base, not a repo); 4 internal
///     failure at a named stage.
/// </summary>
internal static class RunChangeVerb
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? agentName = null;
        string? checksFile = null;
        bool dryRun = false;
        string repoRoot = Environment.CurrentDirectory;
        var promptTokens = new List<string>();
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a.StartsWith("agent=", StringComparison.Ordinal))
            {
                agentName = a["agent=".Length..];
                i++;
                continue;
            }
            if (a is "--dry-run")
            {
                dryRun = true;
                i++;
                continue;
            }
            if (a is "--checks" && i + 1 < args.Length)
            {
                checksFile = args[i + 1];
                i += 2;
                continue;
            }
            if (a.StartsWith("--checks=", StringComparison.Ordinal))
            {
                checksFile = a["--checks=".Length..];
                i++;
                continue;
            }
            if (a is "--repo" && i + 1 < args.Length)
            {
                repoRoot = args[i + 1];
                i += 2;
                continue;
            }
            if (a.StartsWith("--repo=", StringComparison.Ordinal))
            {
                repoRoot = a["--repo=".Length..];
                i++;
                continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unknown option '{a}'.");
                PrintUsage();
                return 2;
            }
            promptTokens.Add(a);
            i++;
        }

        string task = string.Join(' ', promptTokens).Trim();
        if (string.IsNullOrWhiteSpace(agentName) || task.Length == 0)
        {
            PrintUsage();
            return 2;
        }
        if (checksFile is not null && !File.Exists(checksFile))
        {
            Console.Error.WriteLine($"Checks file '{checksFile}' does not exist.");
            return 2;
        }

        Result<WorkspaceContract> pin =
            await WorkspaceInspector.InspectAsync(repoRoot, WorkspaceIsolation.Worktree).ConfigureAwait(false);
        if (pin.IsFailure)
        {
            Console.Error.WriteLine($"Pre-flight conflict: {pin.Error}");
            return 3;
        }
        WorkspaceContract contract = pin.Value;

        Result<RunState> gate = RunChangeTransitions.TryTransition(RunState.Pinned, RunState.Isolated);
        if (gate.IsFailure)
        {
            Console.Error.WriteLine($"Internal failure at stage 'isolate': {gate.Error}");
            return 4;
        }

        if (dryRun)
        {
            Console.WriteLine(
                $"dry-run: would isolate {contract.BaseRevision} from '{contract.RepoRoot}' " +
                $"for agent '{agentName}' (task: {task}). No worktree created, nothing written.");
            return 0;
        }

        Result<IsolatedWorkspace> ws =
            await WorkspaceMaterializer.MaterializeAsync(contract).ConfigureAwait(false);
        if (ws.IsFailure)
        {
            Console.Error.WriteLine($"Internal failure at stage 'isolate': {ws.Error}");
            return 4;
        }

        Console.WriteLine($"run {ws.Value.RunId.Value} isolated at {ws.Value.Path} (base {contract.BaseRevision}, state Isolated).");

        Result<FrozenChangeSet> frozen =
            await ChangeSetFreezer.FreezeAsync(ws.Value).ConfigureAwait(false);
        if (frozen.IsFailure)
        {
            Console.Error.WriteLine($"Internal failure at stage 'freeze': {frozen.Error}");
            return 4;
        }

        FrozenChangeSet set = frozen.Value;
        Console.WriteLine(
            $"run {set.RunId} frozen at {set.HeadRevision} " +
            $"({set.Entries.Count} paths, {(set.IsEmpty ? "empty" : "changed")}, state Changed).");

        Result<IReadOnlyList<CheckSpec>> specs = CheckSpecParser.ParseFile(checksFile);
        if (specs.IsFailure)
        {
            Console.Error.WriteLine($"Internal failure at stage 'checks': {specs.Error}");
            return 4;
        }

        Result<CheckReport> checkedRun =
            await CheckRunner.RunAsync(ws.Value, set.BaseRevision, set.HeadRevision, specs.Value).ConfigureAwait(false);
        if (checkedRun.IsFailure)
        {
            Console.Error.WriteLine($"Internal failure at stage 'checks': {checkedRun.Error}");
            return 4;
        }

        CheckReport checkReport = checkedRun.Value;
        int passed = 0;
        int failed = 0;
        int other = 0;
        for (int k = 0; k < checkReport.Checks.Count; k++)
        {
            if (checkReport.Checks[k].Outcome == CheckOutcome.Passed)
                passed++;
            else if (checkReport.Checks[k].Outcome == CheckOutcome.Failed)
                failed++;
            else
                other++;
        }
        if (!checkReport.HasChecks)
        {
            Console.WriteLine($"run {set.RunId} checked (no checks declared, state Checked).");
        }
        else
        {
            Console.WriteLine(
                $"run {set.RunId} checked " +
                $"({passed} passed, {failed} failed, {other} recorded without a pass, state Checked).");
        }

        Result<RunManifest> moved =
            WorkspaceMaterializer.TryAdvanceState(set.RunId, RunState.Checked);
        if (moved.IsFailure)
        {
            Console.Error.WriteLine($"Internal failure at stage 'checks': {moved.Error}");
            return 4;
        }

        Console.Error.WriteLine(
            "Stage 'report' (S5, #379) is not wired into this command yet: the run stays at Checked, " +
            $"`harbor run list` shows it honestly. Resume this run when the report wiring lands.");
        return 4;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: harbor run change agent=<name> \"<task>\" [--checks <file>] [--dry-run] [--repo <path>]");
        Console.Error.WriteLine("Example: harbor run change agent=code \"fix the null guard\" --dry-run");
    }
}
