// ToolDispatcherSyncResultRule — the abandon paths stay async.
//
// THE DEFECT (#1099, sync-over-async on abandon paths)
// -----------------------------------------------------
// `ToolDispatcher.AwaitOneWithAbandonAsync` read the finished task with a
// blocking `pending.Result` behind `IsCompletedSuccessfully` (two sites: the
// boundary fast-path and the post-grace ternary), and the sibling
// `AwaitAllWithAbandonAsync` did the same with `all.Result` (boundary
// fast-path) plus `tasks[i].Result` (per-call post-grace ternary). The guards
// make the blocks a no-wait today, but the spelling is still sync-over-async:
// it throws AggregateException instead of the task's exception if a guard ever
// drifts, and it is the shape a copy-paste carries into a path where the task
// is NOT complete. The fix is the plain `await ...ConfigureAwait(false)` —
// identical on the guarded paths, honest on every other.
//
// SCOPE — BOTH ABANDON PATHS
// --------------------------
// `AwaitOneWithAbandonAsync` and `AwaitAllWithAbandonAsync` in
// `src/Harbor.Application/Agents/ToolDispatcher.cs` are graded. The AwaitAll
// sibling was a documented remainder when the AwaitOne half landed (#1409);
// this follow-up fixes it and arms the gate over it.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains both
//      graded methods. Without it, "no sync read" and "the walk found nothing"
//      are the same report.
//   2. `AwaitOne_CarriesNoSyncResult` / `AwaitAll_CarriesNoSyncResult` fail on
//      the pre-fix lines (`return pending.Result;`, `? pending.Result`,
//      `return all.Result;`, `? tasks[i].Result`) and pass on the fixed
//      shape, so a revert re-opens the hole AND reddens this gate.
//   3. `AwaitOne_PinsAsyncRead` / `AwaitAll_PinsAsyncRead` require the
//      `await` spelling inside the guarded branches, so deleting the branches
//      (or swapping in a different sink) does not silently satisfy the rule.

namespace Harbor.Architecture.Tests;

using System.Text.RegularExpressions;
using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: both abandon paths read the finished task(s)
///     with <c>await</c>, never a blocking <c>.Result</c>.
/// </summary>
public sealed class ToolDispatcherSyncResultRule
{
    private static string DispatcherSource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Application", "Agents", "ToolDispatcher.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string[] MethodWindow(string[] lines, int start, int maxLines = 30)
    {
        int end = Math.Min(lines.Length, start + maxLines);
        var window = new string[end - start];
        Array.Copy(lines, start, window, 0, end - start);
        return window;
    }

    private static string StripLineComment(string line)
    {
        int at = line.IndexOf("//", StringComparison.Ordinal);
        return at >= 0 ? line[..at] : line;
    }

    private static int FindMethod(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("AwaitOneWithAbandonAsync", StringComparison.Ordinal)
                && lines[i].Contains("Task<ToolResultEntry>", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindAwaitAll(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("AwaitAllWithAbandonAsync", StringComparison.Ordinal)
                && lines[i].Contains("Task<ToolResultEntry[]>", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The blocking-take ban's sub-id, shared by the rule data and the parser.</summary>
    private const string SyncResultSubId = "SYNC-RESULT-READ";

    // MECHANISM (#1086, step 2, conveyor batch 11; AwaitAll armed in the #1099 follow-up)
    // --------------------------------------------
    // The ban below is a ScanRule: the blocking-take spelling as one Forbidden
    // row, seven planted controls, the graded file as the MustContain floor, a
    // discovery floor. Enumeration, matching and the control/discovery verdicts
    // are ScanRunner's; this file keeps the issue prose, the method-window
    // helpers, the async-read pins, and the test names.
    //
    // The verdict is ParseSyncResultReads through the Func-overload, carrying
    // the original window scans verbatim — FindMethod/FindAwaitAll plus the
    // MethodWindow with its //-comment strip (30 lines for the one-call path,
    // 40 for the longer batch path), via the shared
    // FindSyncResultLines helper — with no hatch and an empty baseline: zero
    // blocking takes in either abandon path is the whole rule. The Forbidden row
    // documents the banned spelling; the runner does not execute it (the rule
    // grades through ParseSyncResultReads, whose method-window scoping a line
    // regex cannot express).
    //
    // Scope parity is exact: Trees ["src/Harbor.Application/Agents"] walks what
    // the old DispatcherSource read, and FindMethod/FindAwaitAll fire only on
    // the AwaitOneWithAbandonAsync / AwaitAllWithAbandonAsync declaration lines —
    // the call site and every other file's takes match nothing, so widening
    // the walk from the one file to its directory changes no verdict. MinHits 20
    // is the slice's file count today (Pipeline/ included): the floor fails when
    // the walk collapses, not when the slice is clean.

    /// <summary>
    ///     The banned spelling: a blocking <c>.Result</c> take. The runner does
    ///     not execute this row (the rule grades through
    ///     <see cref="ParseSyncResultReads" />); it documents the shape the rule
    ///     bans and carries the failure text.
    /// </summary>
    private static readonly Regex BlockingTakeShape = new(
        @"\.Result\b",
        RegexOptions.Compiled);

    /// <summary>The blocking-take ban as data: one documented shape, planted controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "ToolDispatcherSyncResult",
        Trees = ["src/Harbor.Application/Agents"],
        Forbidden =
        [
            new ScanForbidden(
                SyncResultSubId,
                BlockingTakeShape,
                "read the finished task with await pending.ConfigureAwait(false), never a "
                + "blocking .Result take — sync-over-async throws AggregateException instead "
                + "of the task's exception if the guard ever drifts. See issue #1099."),
        ],
        Controls =
        [
            new ScanControl(
                "Planted/BlockingTake.cs",
                "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(\n"
                + "        Task<ToolResultEntry> pending,\n"
                + "        CancellationToken ct)\n"
                + "    {\n"
                + "        if (pending.IsCompletedSuccessfully)\n"
                + "            return pending.Result;\n"
                + "    }",
                SyncResultSubId),
            new ScanControl(
                "Planted/BlockingTakeTernary.cs",
                "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(Task<ToolResultEntry> pending)\n"
                + "    {\n"
                + "        return pending.IsCompletedSuccessfully\n"
                + "            ? pending.Result\n"
                + "            : await AbandonLeftoverAsync(pending);}",
                SyncResultSubId),
            new ScanControl(
                "Fixed/AsyncRead.cs",
                "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(Task<ToolResultEntry> pending)\n"
                + "    {\n"
                + "        if (pending.IsCompletedSuccessfully)\n"
                + "            return await pending.ConfigureAwait(false);\n"
                + "    }",
                null),
            new ScanControl(
                "NearMiss/CommentedTake.cs",
                "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(Task<ToolResultEntry> pending)\n"
                + "    {\n"
                + "        // return pending.Result; // pre-fix shape, now prose\n"
                + "        return await pending.ConfigureAwait(false);\n"
                + "    }",
                null),
            new ScanControl(
                "NearMiss/SiblingMethod.cs",
                "    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(Task<ToolResultEntry>[] tasks)\n"
                + "    {\n"
                + "        resolved[i] = await tasks[i].ConfigureAwait(false);\n"
                + "    }",
                null),
            new ScanControl(
                "Planted/BlockingTakeAll.cs",
                "    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(\n"
                + "        Task<ToolResultEntry>[] tasks)\n"
                + "    {\n"
                + "        if (all.IsCompletedSuccessfully)\n"
                + "            return all.Result;\n"
                + "    }",
                SyncResultSubId),
            new ScanControl(
                "Planted/BlockingTakeAllTernary.cs",
                "    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(Task<ToolResultEntry>[] tasks)\n"
                + "    {\n"
                + "        resolved[i] = tasks[i].IsCompletedSuccessfully\n"
                + "            ? tasks[i].Result\n"
                + "            : await AbandonLeftoverAsync(tasks[i]);}",
                SyncResultSubId),
        ],
        // The slice holds twenty C# files today, including ToolDispatcher.cs; a
        // smaller count means the walk broke rather than the slice is clean.
        MinHits = 20,
        MustContain =
        [
            "src/Harbor.Application/Agents/ToolDispatcher.cs",
        ],
        CustomParse = ParseSyncResultReads,
    };

    /// <summary>
    ///     The custom parser: the original window scans over one file's raw source —
    ///     the method finds plus the 30-line windows with their comment strip verbatim
    ///     (via <see cref="FindSyncResultLines" />), one report per line. Files
    ///     without either abandon path match nothing.
    /// </summary>
    private static IEnumerable<ScanHit> ParseSyncResultReads(string displayPath, string rawSource)
    {
        string[] lines = rawSource.Split('\n');
        foreach (int line in FindAllSyncResultLines(lines))
        {
            yield return new ScanHit(SyncResultSubId, displayPath, line, lines[line - 1].Trim());
        }
    }

    /// <summary>
    ///     1-based line numbers of the blocking takes inside both abandon paths'
    ///     method windows: <c>.Result</c> reads after the <c>//</c>-comment strip.
    /// </summary>
    private static List<int> FindAllSyncResultLines(string[] lines)
    {
        var offenders = new List<int>();
        int one = FindMethod(lines);
        if (one >= 0)
        {
            offenders.AddRange(FindSyncResultLines(lines, one));
        }

        int all = FindAwaitAll(lines);
        if (all >= 0)
        {
            // The batch path is longer than the one-call path (batch fast-path
            // plus the per-call loop): a 40-line window reaches the per-call
            // ternary without bleeding into AbandonLeftoverAsync.
            offenders.AddRange(FindSyncResultLines(lines, all, 40));
        }

        return offenders;
    }

    /// <summary>
    ///     1-based line numbers of the blocking takes inside the abandon path's
    ///     method window: <c>.Result</c> reads after the <c>//</c>-comment strip.
    /// </summary>
    private static List<int> FindSyncResultLines(string[] lines, int start, int maxLines = 30)
    {
        var offenders = new List<int>();
        string[] window = MethodWindow(lines, start, maxLines);
        for (int k = 0; k < window.Length; k++)
        {
            string code = StripLineComment(window[k]);
            if (code.Contains(".Result", StringComparison.Ordinal))
            {
                offenders.Add(start + k + 1);
            }
        }

        return offenders;
    }

    /// <summary>
    ///     The graded file is readable and contains both graded methods. A gate
    ///     that cannot see its subject reports a healthy zero.
    /// </summary>
    [Test]
    public async Task GradedFile_IsReadable()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string source = DispatcherSource();
        await Assert.That(source).IsNotEmpty()
            .Because("ToolDispatcher.cs must be readable from the checkout");

        await Assert.That(source).Contains("AwaitOneWithAbandonAsync")
            .Because("AwaitOneWithAbandonAsync is one abandon path this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");

        await Assert.That(source).Contains("AwaitAllWithAbandonAsync")
            .Because("AwaitAllWithAbandonAsync is the other abandon path this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");
    }

    /// <summary>
    ///     #1099 fix, pinned: no blocking <c>.Result</c> read survives in the
    ///     AwaitOne abandon path. Every guarded branch awaits instead, so
    ///     re-introducing either pre-fix line fails here.
    /// </summary>
    [Test]
    public async Task AwaitOne_CarriesNoSyncResult()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string[] lines = DispatcherSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("AwaitOneWithAbandonAsync must exist in ToolDispatcher.cs; if it "
                   + "was renamed, this rule must be re-pointed");

        List<int> offenders = FindSyncResultLines(lines, start);

        // The ScanRule half: every blocking take in the slice. No baseline rows
        // and no file hatch — zero blocking takes here is the whole rule (see
        // the MECHANISM note above).
        List<string> illegal = ScanRunner.Evaluate(Rule);

        await Assert.That(offenders).IsEmpty()
            .Because("a blocking .Result read in AwaitOneWithAbandonAsync is sync-over-async "
                   + "(#1099): the guarded branches must await the finished task instead. "
                   + "Offending 1-based lines: " + string.Join(", ", offenders));

        await Assert.That(illegal.Count).IsEqualTo(0)
            .Because("a blocking .Result read in AwaitOneWithAbandonAsync is sync-over-async "
                   + "(#1099): the guarded branches must await the finished task instead. "
                   + "ScanRule reports: " + string.Join("; ", illegal));
    }

    /// <summary>
    ///     The guarded branches carry the async read: dropping the branches
    ///     (or swapping in a different sink) must not silently satisfy the rule.
    /// </summary>
    [Test]
    public async Task AwaitOne_PinsAsyncRead()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string[] lines = DispatcherSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("AwaitOneWithAbandonAsync must exist in ToolDispatcher.cs; if it "
                   + "was reworked, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains("await pending.ConfigureAwait(false)")
            .Because("the guarded branches must read the finished task with "
                   + "await pending.ConfigureAwait(false), not a blocking take (#1099)");
    }

    /// <summary>
    ///     #1099 fix, pinned: no blocking <c>.Result</c> read survives in the
    ///     AwaitAll abandon path. Every guarded branch awaits instead, so
    ///     re-introducing either pre-fix line fails here.
    /// </summary>
    [Test]
    public async Task AwaitAll_CarriesNoSyncResult()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string[] lines = DispatcherSource().Split('\n');
        int start = FindAwaitAll(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("AwaitAllWithAbandonAsync must exist in ToolDispatcher.cs; if it "
                   + "was renamed, this rule must be re-pointed");

        List<int> offenders = FindSyncResultLines(lines, start, 40);

        await Assert.That(offenders).IsEmpty()
            .Because("a blocking .Result read in AwaitAllWithAbandonAsync is sync-over-async "
                   + "(#1099): the guarded branches must await the finished batch instead. "
                   + "Offending 1-based lines: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The AwaitAll guarded branches carry the async read: dropping the
    ///     branches (or swapping in a different sink) must not silently
    ///     satisfy the rule.
    /// </summary>
    [Test]
    public async Task AwaitAll_PinsAsyncRead()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string[] lines = DispatcherSource().Split('\n');
        int start = FindAwaitAll(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("AwaitAllWithAbandonAsync must exist in ToolDispatcher.cs; if it "
                   + "was reworked, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start, 40).Select(StripLineComment));
        await Assert.That(window).Contains("await all.ConfigureAwait(false)")
            .Because("the boundary fast-path must read the finished batch with "
                   + "await all.ConfigureAwait(false), not a blocking take (#1099)");

        await Assert.That(window).Contains("await tasks[i].ConfigureAwait(false)")
            .Because("the per-call branch must read the finished call with "
                   + "await tasks[i].ConfigureAwait(false), not a blocking take (#1099)");
    }

    /// <summary>
    ///     The planted controls — all four pre-fix takes and the legal neighbours —
    ///     drive the REAL parser: blocking takes reported under their sub-id,
    ///     the async reads, the comment and the fixed sibling silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the seven planted controls must agree with the matcher cases below through "
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
                "src/Harbor.Application/Agents holds twenty C# files, including ToolDispatcher.cs; "
                + "a smaller count — or a scope that stopped seeing that file — means the "
                + "directory was renamed or the walk stopped matching, and "
                + "AwaitOne_CarriesNoSyncResult becomes vacuous. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Non-vacuity: the SAME matcher must fire on all four planted pre-fix takes,
    ///     stay silent on both async reads and on comment prose. This is what
    ///     separates "the guard is green" from "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnBlockingTakes_AndStaysSilentOtherwise()
    {
        string[] blockingTake =
        [
            "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(",
            "        Task<ToolResultEntry> pending,",
            "        CancellationToken ct)",
            "    {",
            "        if (pending.IsCompletedSuccessfully)",
            "            return pending.Result;",
            "    }",
        ];
        await Assert.That(FindSyncResultLines(blockingTake, FindMethod(blockingTake)).Count).IsGreaterThan(0)
            .Because("a planted blocking take must be detected, or the guard is blind");

        string[] blockingTakeTernary =
        [
            "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(Task<ToolResultEntry> pending)",
            "    {",
            "        return pending.IsCompletedSuccessfully",
            "            ? pending.Result",
            "            : await AbandonLeftoverAsync(pending);}",
        ];
        await Assert.That(FindSyncResultLines(blockingTakeTernary, FindMethod(blockingTakeTernary)).Count).IsGreaterThan(0)
            .Because("the ternary pre-fix shape must be detected too — it is the second site the fix converted");

        string[] asyncRead =
        [
            "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(Task<ToolResultEntry> pending)",
            "    {",
            "        if (pending.IsCompletedSuccessfully)",
            "            return await pending.ConfigureAwait(false);",
            "    }",
        ];
        await Assert.That(FindSyncResultLines(asyncRead, FindMethod(asyncRead)).Count).IsEqualTo(0)
            .Because("the async read is the fix and must not trip the guard, or the guard is noise");

        string[] commentedTake =
        [
            "    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(Task<ToolResultEntry> pending)",
            "    {",
            "        // return pending.Result; // pre-fix shape, now prose",
            "        return await pending.ConfigureAwait(false);",
            "    }",
        ];
        await Assert.That(FindSyncResultLines(commentedTake, FindMethod(commentedTake)).Count).IsEqualTo(0)
            .Because("comment lines are prose, not a second take");

        string[] siblingMethod =
        [
            "    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(Task<ToolResultEntry>[] tasks)",
            "    {",
            "        resolved[i] = await tasks[i].ConfigureAwait(false);",
            "    }",
        ];
        await Assert.That(FindSyncResultLines(siblingMethod, FindAwaitAll(siblingMethod)).Count).IsEqualTo(0)
            .Because("the fixed sibling shape is the fix and must not trip the guard, or the guard is noise");

        string[] blockingTakeAll =
        [
            "    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(",
            "        Task<ToolResultEntry>[] tasks)",
            "    {",
            "        if (all.IsCompletedSuccessfully)",
            "            return all.Result;",
            "    }",
        ];
        await Assert.That(FindSyncResultLines(blockingTakeAll, FindAwaitAll(blockingTakeAll)).Count).IsGreaterThan(0)
            .Because("the AwaitAll boundary pre-fix shape must be detected too — it is the third site the fix converted");

        string[] blockingTakeAllTernary =
        [
            "    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(Task<ToolResultEntry>[] tasks)",
            "    {",
            "        resolved[i] = tasks[i].IsCompletedSuccessfully",
            "            ? tasks[i].Result",
            "            : await AbandonLeftoverAsync(tasks[i]);}",
        ];
        await Assert.That(FindSyncResultLines(blockingTakeAllTernary, FindAwaitAll(blockingTakeAllTernary)).Count).IsGreaterThan(0)
            .Because("the AwaitAll per-call pre-fix shape must be detected too — it is the fourth site the fix converted");
    }

    /// <summary>
    ///     The rule carries no tolerated rows: every blocking take in these abandon
    ///     paths is the defect #1099 reported, so there is nothing to excuse.
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
