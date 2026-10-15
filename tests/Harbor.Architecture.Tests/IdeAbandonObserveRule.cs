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

using System.Text.RegularExpressions;
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

    /// <summary>The bare-detach ban's sub-id, shared by the rule data and the parser.</summary>
    private const string BareDetachSubId = "BARE-PROMPTRUN-DETACH";

    // MECHANISM (#1086, step 2, conveyor batch 11)
    // --------------------------------------------
    // The ban below is a ScanRule: the bare-detach spelling as one Forbidden
    // row, six planted controls, the graded file as the MustContain floor, a
    // discovery floor. Enumeration, matching and the control/discovery verdicts
    // are ScanRunner's; this file keeps the issue prose, the patterns, the
    // behaviour-adjacent pins, and the test names.
    //
    // The verdict is ParseIdeDetach through the Func-overload, carrying the
    // original line scan verbatim — the AwaitRunAsync( filter, the declaration
    // skip, the same-statement ContinueWith chain check with its next-line
    // lookahead — via the shared FindBareDetachLines helper, with no hatch
    // and an empty baseline: zero bare detaches in this slice is the whole
    // rule. The Forbidden row documents the banned spelling; the runner does
    // not execute it (the rule grades through ParseIdeDetach, whose two-line
    // chain check a line regex cannot express).
    //
    // Scope parity is exact: Trees ["src/Harbor.Ipc.Client/Ide"] walks what
    // the old BridgeSource read — AwaitRunAsync( appears nowhere else in the
    // slice (the declaration and the detach both live in IdeSessionBridge.cs),
    // so widening the walk from the one file to its directory changes no
    // verdict. MinHits 4 is the slice's file count today: the floor fails when
    // the walk collapses, not when the slice is clean.

    /// <summary>
    ///     The banned spelling: a bare <c>_ = AwaitRunAsync(</c> detach. The
    ///     runner does not execute this row (the rule grades through
    ///     <see cref="ParseIdeDetach" />); it documents the shape the rule bans
    ///     and carries the failure text.
    /// </summary>
    private static readonly Regex BareDetachShape = new(
        @"_ =\s*AwaitRunAsync\s*\(",
        RegexOptions.Compiled);

    /// <summary>The bare-detach ban as data: one documented shape, planted controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "IdeAbandonObserve",
        Trees = ["src/Harbor.Ipc.Client/Ide"],
        Forbidden =
        [
            new ScanForbidden(
                BareDetachSubId,
                BareDetachShape,
                "chain the fault observer in the same statement "
                + "(_ = AwaitRunAsync(...).ContinueWith(..., OnlyOnFaulted, ...)) — "
                + "a bare detach parks a background prompt-run fault where only "
                + "finalization can see it. See issue #1099."),
        ],
        Controls =
        [
            new ScanControl(
                "Planted/BareDetach.cs",
                "        _ = AwaitRunAsync(run, runCts);",
                BareDetachSubId),
            new ScanControl(
                "Planted/BareDetachTrailingComment.cs",
                "        _ = AwaitRunAsync(run, runCts); // dropped observer",
                BareDetachSubId),
            new ScanControl(
                "Fixed/ChainedSameLine.cs",
                "        _ = AwaitRunAsync(run, runCts).ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);",
                null),
            new ScanControl(
                "Fixed/ChainedNextLine.cs",
                "        _ = AwaitRunAsync(run, runCts)\n"
                + "            .ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);",
                null),
            new ScanControl(
                "NearMiss/Declaration.cs",
                "    private async Task AwaitRunAsync(Task run, CancellationTokenSource runCts)",
                null),
            new ScanControl(
                "NearMiss/Unrelated.cs",
                "        await PushNotificationAsync(IdeNotifications.PromptError, note).ConfigureAwait(false);",
                null),
        ],
        // The slice holds four C# files today, including IdeSessionBridge.cs; a
        // smaller count means the walk broke rather than the slice is clean.
        MinHits = 4,
        MustContain =
        [
            "src/Harbor.Ipc.Client/Ide/IdeSessionBridge.cs",
        ],
        CustomParse = ParseIdeDetach,
    };

    /// <summary>
    ///     The custom parser: the original detach scan over one file's raw source —
    ///     the marker filter, the declaration skip and the chain check verbatim
    ///     (via <see cref="FindBareDetachLines" />), one report per line.
    /// </summary>
    private static IEnumerable<ScanHit> ParseIdeDetach(string displayPath, string rawSource)
    {
        string[] lines = rawSource.Split('\n');
        foreach (int index in FindBareDetachLines(lines))
        {
            yield return new ScanHit(BareDetachSubId, displayPath, index + 1, lines[index].Trim());
        }
    }

    /// <summary>
    ///     0-based line numbers of the bare detach sites: lines calling
    ///     <c>AwaitRunAsync(</c> that are neither the declaration nor chained to
    ///     a <c>ContinueWith</c> observer in the same statement.
    /// </summary>
    private static List<int> FindBareDetachLines(IReadOnlyList<string> lines)
    {
        var bare = new List<int>();
        for (int i = 0; i < lines.Count; i++)
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
                || (i + 1 < lines.Count && lines[i + 1].Contains("ContinueWith", StringComparison.Ordinal));
            if (!chained)
            {
                bare.Add(i);
            }
        }

        return bare;
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
        List<int> bare = FindBareDetachLines(lines);

        // The ScanRule half: every bare detach in the slice. No baseline rows
        // and no file hatch — zero bare detaches here is the whole rule (see
        // the MECHANISM note above).
        List<string> illegal = ScanRunner.Evaluate(Rule);

        await Assert.That(bare).IsEmpty()
            .Because("a bare _ = AwaitRunAsync( detach parks a background prompt-run "
                   + "fault where only finalization can see it (#1099): the detach must "
                   + "chain its OnlyOnFaulted observer in the same statement. "
                   + "Offending 1-based lines: " + string.Join(", ", bare.Select(i => i + 1)));

        await Assert.That(illegal.Count).IsEqualTo(0)
            .Because("a bare _ = AwaitRunAsync( detach parks a background prompt-run "
                   + "fault where only finalization can see it (#1099): the detach must "
                   + "chain its OnlyOnFaulted observer in the same statement. "
                   + "ScanRule reports: " + string.Join("; ", illegal));
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

    /// <summary>
    ///     The planted controls — every detach shape and every near-miss — drive
    ///     the REAL parser: bare detaches reported under their sub-id, the
    ///     chained observer and the declaration silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the six planted controls must agree with the matcher cases below through "
                + "the rule's own parser — otherwise the rule guards nothing, or forbids "
                + "the fix. " + string.Join("; ", failures));
    }

    /// <summary>
    ///     Non-vacuity: the walk must find the slice it claims to govern.
    ///     A scan rooted at a renamed directory finds zero files and then passes
    ///     everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheScopeItClaimsToGovern()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "src/Harbor.Ipc.Client/Ide holds four C# files, including IdeSessionBridge.cs; "
                + "a smaller count — or a scope that stopped seeing that file — means the "
                + "directory was renamed or the walk stopped matching, and "
                + "PromptRunDetach_IsObserved becomes vacuous. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Non-vacuity: the SAME matcher must fire on a planted bare detach, stay
    ///     silent on the chained observer and the declaration, and stay silent on
    ///     unrelated lines. This is what separates "the guard is green" from
    ///     "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnPlantedDetach_AndStaysSilentOtherwise()
    {
        await Assert.That(FindBareDetachLines(
            ["        _ = AwaitRunAsync(run, runCts);"]).Count).IsGreaterThan(0)
            .Because("a planted bare detach must be detected, or the guard is blind");

        await Assert.That(FindBareDetachLines(
            ["        _ = AwaitRunAsync(run, runCts); // dropped observer"]).Count).IsGreaterThan(0)
            .Because("a bare detach with a trailing comment is still a bare detach");

        await Assert.That(FindBareDetachLines(
            ["        _ = AwaitRunAsync(run, runCts).ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);"]).Count).IsEqualTo(0)
            .Because("the chained observer is the fix and must not trip the guard, or the guard is noise");

        await Assert.That(FindBareDetachLines(
            [
                "        _ = AwaitRunAsync(run, runCts)",
                "            .ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);",
            ]).Count).IsEqualTo(0)
            .Because("a continuation on the next line is the same statement and the same fix");

        await Assert.That(FindBareDetachLines(
            ["    private async Task AwaitRunAsync(Task run, CancellationTokenSource runCts)"]).Count).IsEqualTo(0)
            .Because("the declaration is not a detach site");

        await Assert.That(FindBareDetachLines(
            ["        await PushNotificationAsync(IdeNotifications.PromptError, note).ConfigureAwait(false);"]).Count).IsEqualTo(0)
            .Because("an unrelated line must not trip the guard, or the guard is noise");
    }

    /// <summary>
    ///     The rule carries no tolerated rows: every bare detach anywhere in this
    ///     slice is the defect #1099 reported, so there is nothing to excuse.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty()
            .Because("every baseline row must state why it is tolerated, in the row itself. "
                   + string.Join("; ", failures));
    }

    /// <summary>
    ///     No baseline row is stale: a permission for a problem that no longer
    ///     exists is a blanket permission.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a stale baseline row excuses nothing and teaches the next reader "
                   + "that permissions accumulate. " + string.Join("; ", stale));
    }
}
