// IdeAbandonObserveRule — the IDE bridge background prompt run must stay observed.
//
// THE DEFECT (#1099, the #948 class — async void swallows everything after await)
// -------------------------------------------------------------------------------
// `IdeSessionBridge.SchedulePromptRun` answers `inject_prompt` immediately and
// parks the agent run on a detached task. That task used to be dropped with a
// bare `_ = AwaitRunAsync(run, runCts)` justified by a comment ("RunPromptAsync
// is catch-all — the task never faults"). The comment was wrong about the
// boundary: `RunPromptAsync` catches host failures, but the post-run editor
// notify (`PushNotificationAsync`) rides OUTSIDE that try — a broken editor
// pipe (editor closed stdio mid-run) faults the run with OCE, and a bare
// discard parks that fault where only `TaskScheduler.UnobservedTaskException`
// at finalization could ever see it: no log, no surface.
//
// The fix attaches an `OnlyOnFaulted` continuation that logs the fault
// (same shape as `ToolDispatcher.AbandonLeftoverAsync`, blessed by
// docs/ANTIPATTERNS.md §9). This rule pins that shape.
//
// SCOPE — ONE SITE, ON PURPOSE
// ----------------------------
// Only `SchedulePromptRun` in `src/Harbor.Ipc.Client/Ide/IdeSessionBridge.cs`
// is graded. The sibling bare discard in `IdeJsonRpcServer.Dispatch`
// (`_ = ObserveAbandonedAsync(handlerTask)`) is a real remainder of the same
// audit, but its observer is itself catch-all, so a literal-match rule written
// for it could not fail on a dropped fault — only on a reformat. It is left
// for the follow-up that can give it a behavioral test, not graded here.
// Widening this rule to that file without fixing it would ship a red gate;
// widening it after the fix without re-pointing would grade nothing.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains the
//      graded method. Without it, "no bare discard" and "the walk found
//      nothing" are the same report.
//   2. `PromptRunDetach_IsObserved` fails on the pre-fix line
//      (`_ = AwaitRunAsync(run, runCts);`) and passes on the fixed shape, so a
//      revert re-opens the hole AND reddens this gate.
//   3. `PromptRunDetach_PinsOnlyOnFaulted` requires the faulted-only
//      continuation, so swapping in a different sink (or a bare await that
//      cannot compile on a void member) does not silently satisfy the rule.

namespace Harbor.Architecture.Tests;

using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: the IDE bridge background prompt run carries an
///     <c>OnlyOnFaulted</c> fault observer instead of a bare task discard.
/// </summary>
public sealed class IdeAbandonObserveRule
{
    private static string BridgeSource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Ipc.Client", "Ide", "IdeSessionBridge.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>
    ///     The graded file is readable and contains the graded method. A gate
    ///     that cannot see its subject reports a healthy zero.
    /// </summary>
    [Test]
    public async Task GradedFile_IsReadable()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads IdeSessionBridge.cs from the checkout");

        string source = BridgeSource();
        await Assert.That(source).IsNotEmpty()
            .Because("IdeSessionBridge.cs must be readable from the checkout");

        await Assert.That(source).Contains("private void SchedulePromptRun")
            .Because("SchedulePromptRun is the one detach site this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");
    }

    /// <summary>
    ///     #1099 fix, pinned: no bare <c>_ = AwaitRunAsync(</c> discard survives
    ///     in the bridge. Every detach site must carry its fault observer in the
    ///     same statement (a <c>ContinueWith</c> chain), so dropping the
    ///     continuation re-introduces the exact pre-fix line — and fails here.
    /// </summary>
    [Test]
    public async Task PromptRunDetach_IsObserved()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads IdeSessionBridge.cs from the checkout");

        string[] lines = BridgeSource().Split('\n');
        var bare = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.Contains("AwaitRunAsync(", StringComparison.Ordinal)
                || line.Contains("Task AwaitRunAsync", StringComparison.Ordinal)
                || line.Contains("async Task AwaitRunAsync", StringComparison.Ordinal))
            {
                continue;
            }

            // The fixed shape chains the observer in the same statement; a bare
            // discard ends the statement at the call. A declaration line is
            // skipped above, so whatever remains here must be the detach site.
            bool chained = line.Contains("ContinueWith", StringComparison.Ordinal)
                || (i + 1 < lines.Length && lines[i + 1].Contains("ContinueWith", StringComparison.Ordinal));
            if (!chained)
            {
                bare.Add(i + 1);
            }
        }

        await Assert.That(bare).IsEmpty()
            .Because("a bare _ = AwaitRunAsync( detach parks a background prompt-run "
                   + "fault where only finalization can see it (#1099): the detach must "
                   + "chain its OnlyOnFaulted observer in the same statement. "
                   + "Offending 1-based lines: " + string.Join(", ", bare));
    }

    /// <summary>
    ///     The chained observer is fault-only: a continuation without
    ///     <c>OnlyOnFaulted</c> would run on success too and could mask the
    ///     normal path, while no continuation at all is the pre-fix defect.
    /// </summary>
    [Test]
    public async Task PromptRunDetach_PinsOnlyOnFaulted()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads IdeSessionBridge.cs from the checkout");

        string[] lines = BridgeSource().Split('\n');
        int detach = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("_ = AwaitRunAsync(", StringComparison.Ordinal))
            {
                detach = i;
                break;
            }
        }

        await Assert.That(detach).IsGreaterThanOrEqualTo(0)
            .Because("the detach site must exist in SchedulePromptRun; if the "
                   + "scheduling was reworked, this rule must be re-pointed");

        string window = string.Join("\n", lines.Skip(detach).Take(8));
        await Assert.That(window).Contains("TaskContinuationOptions.OnlyOnFaulted")
            .Because("the detach observer must be fault-only (OnlyOnFaulted): it is "
                   + "the §FP-006 backstop for a run that faults outside its own "
                   + "catch-all, not a second completion path (#1099)");
    }
}
