// ToolDispatcherSyncResultRule — the AwaitOne abandon path stays async.
//
// THE DEFECT (#1099, sync-over-async on abandon paths)
// -----------------------------------------------------
// `ToolDispatcher.AwaitOneWithAbandonAsync` read the finished task with a
// blocking `pending.Result` behind `IsCompletedSuccessfully` (two sites: the
// boundary fast-path and the post-grace ternary). The guard makes the block a
// no-wait today, but the spelling is still sync-over-async: it throws
// AggregateException instead of the task's exception if the guard ever drifts,
// and it is the shape a copy-paste carries into a path where the task is NOT
// complete. The fix is the plain `await pending.ConfigureAwait(false)` —
// identical on the guarded path, honest on every other.
//
// SCOPE — ONE METHOD, ON PURPOSE
// ------------------------------
// Only `AwaitOneWithAbandonAsync` in
// `src/Harbor.Application/Agents/ToolDispatcher.cs` is graded. The sibling
// `AwaitAllWithAbandonAsync` carries the same smell (`all.Result`,
// `tasks[i].Result`) and is a real remainder of the same audit, but widening
// this rule to it without fixing it would ship a red gate. It is left for the
// follow-up that fixes it, not graded here.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains the
//      graded method. Without it, "no sync read" and "the walk found nothing"
//      are the same report.
//   2. `AwaitOne_CarriesNoSyncResult` fails on the pre-fix lines
//      (`return pending.Result;`, `? pending.Result`) and passes on the fixed
//      shape, so a revert re-opens the hole AND reddens this gate.
//   3. `AwaitOne_PinsAsyncRead` requires the `await pending` spelling inside
//      the guarded branches, so deleting the branches (or swapping in a
//      different sink) does not silently satisfy the rule.

namespace Harbor.Architecture.Tests;

using System.Text.RegularExpressions;
using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: the AwaitOne abandon path reads the finished task
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

    /// <summary>The blocking-take ban's sub-id, shared by the rule data and the parser.</summary>
    private const string SyncResultSubId = "SYNC-RESULT-READ";

    // MECHANISM (#1086, step 2, conveyor batch 11)
    // --------------------------------------------
    // The ban below is a ScanRule: the blocking-take spelling as one Forbidden
    // row, five planted controls, the graded file as the MustContain floor, a
    // discovery floor. Enumeration, matching and the control/discovery verdicts
    // are ScanRunner's; this file keeps the issue prose, the method-window
    // helpers, the async-read pin, and the test names.
    //
    // The verdict is ParseSyncResultReads through the Func-overload, carrying
    // the original window scan verbatim — FindMethod plus the 30-line
    // MethodWindow with its //-comment strip, via the shared
    // FindSyncResultLines helper — with no hatch and an empty baseline: zero
    // blocking takes in the abandon path is the whole rule. The Forbidden row
    // documents the banned spelling; the runner does not execute it (the rule
    // grades through ParseSyncResultReads, whose method-window scoping a line
    // regex cannot express).
    //
    // Scope parity is exact: Trees ["src/Harbor.Application/Agents"] walks what
    // the old DispatcherSource read, and FindMethod fires only on the
    // AwaitOneWithAbandonAsync declaration line — the call site, the AwaitAll
    // sibling (a documented remainder with its own takes) and every other
    // file's takes match nothing, so widening the walk from the one file to
    // its directory changes no verdict. MinHits 20 is the slice's file count
    // today (Pipeline/ included): the floor fails when the walk collapses,
    // not when the slice is clean.

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
                + "        resolved[i] = tasks[i].Result;\n"
                + "    }",
                null),
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
    ///     The custom parser: the original window scan over one file's raw source —
    ///     the method find plus the 30-line window with its comment strip verbatim
    ///     (via <see cref="FindSyncResultLines" />), one report per line. Files
    ///     without the abandon path match nothing.
    /// </summary>
    private static IEnumerable<ScanHit> ParseSyncResultReads(string displayPath, string rawSource)
    {
        string[] lines = rawSource.Split('\n');
        int start = FindMethod(lines);
        if (start < 0)
        {
            yield break;
        }

        foreach (int line in FindSyncResultLines(lines, start))
        {
            yield return new ScanHit(SyncResultSubId, displayPath, line, lines[line - 1].Trim());
        }
    }

    /// <summary>
    ///     1-based line numbers of the blocking takes inside the abandon path's
    ///     method window: <c>.Result</c> reads after the <c>//</c>-comment strip.
    /// </summary>
    private static List<int> FindSyncResultLines(string[] lines, int start)
    {
        var offenders = new List<int>();
        string[] window = MethodWindow(lines, start);
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
    ///     The graded file is readable and contains the graded method. A gate
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
            .Because("AwaitOneWithAbandonAsync is the one abandon path this rule grades; "
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
    ///     The planted controls — both pre-fix takes and the legal neighbours —
    ///     drive the REAL parser: blocking takes reported under their sub-id,
    ///     the async read, the comment and the sibling method silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the five planted controls must agree with the matcher cases below through "
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
    ///     Non-vacuity: the SAME matcher must fire on both planted pre-fix takes,
    ///     stay silent on the async read and on comment prose, and never reach the
    ///     sibling method's takes. This is what separates "the guard is green"
    ///     from "the guard is looking at nothing".
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
            "        resolved[i] = tasks[i].Result;",
            "    }",
        ];
        await Assert.That(FindMethod(siblingMethod)).IsEqualTo(-1)
            .Because("the AwaitAll sibling is a documented remainder graded elsewhere — "
                   + "this rule's window must not reach it");
    }

    /// <summary>
    ///     The rule carries no tolerated rows: every blocking take in this abandon
    ///     path is the defect #1099 reported, so there is nothing to excuse.
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
