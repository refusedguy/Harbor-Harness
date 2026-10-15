// OpenAiCompatiblePumpObserveRule — the OpenAI-compatible SSE pump stays observed.
//
// THE DEFECT (#1099, the #948 class — async void swallows everything after await)
// --------------------------------------------------------------------------------
// `OpenAiCompatibleLlmClient.StreamAsync` parks the SSE pump on a detached
// `Task.Run`. The delegate is catch-all inside, but the catch-path
// `writer.WriteAsync(..., cancellationToken)` can still fault (a cancelled
// reader throws the error report itself), and a bare `_ = Task.Run(...`
// discard parks that fault where only `TaskScheduler.UnobservedTaskException`
// at finalization could ever see it: no log, no surface. (#1104 already
// removed the `cancellationToken` from `Task.Run` so a pre-cancelled token
// cannot skip the delegate and orphan `writer.TryComplete()`; this rule pins
// the remaining half — the fault observer.)
//
// The fix chains an `OnlyOnFaulted` continuation that logs via the existing
// `OpenAiCompatibleLlmClientLog.StreamTaskFailed` sink (same shape as
// `ToolDispatcher.AbandonLeftoverAsync`, blessed by docs/ANTIPATTERNS.md §9).
// This rule pins that shape.
//
// SCOPE — ONE METHOD, ON PURPOSE
// -------------------------------
// Only `StreamAsync` in
// `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs` is
// graded. The sibling fire-and-forget / sync-over-async sites from the same
// audit (OnboardingVM x2, `ToolDispatcher.AwaitAllWithAbandonAsync` `.Result`
// x2) are real remainders, but widening this rule to them without fixing them
// would ship a red gate. They are left for the follow-ups that fix them, not
// graded here.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains the
//      graded method. Without it, "no bare discard" and "the walk found
//      nothing" are the same report.
//   2. `PumpDetach_IsObserved` fails on the pre-fix line
//      (`_ = Task.Run(async () =>` with no `OnlyOnFaulted` in the method)
//      and passes on the fixed shape, so a revert re-opens the hole AND
//      reddens this gate.
//   3. `PumpDetach_PinsOnlyOnFaulted` requires the faulted-only
//      continuation, so swapping in a different sink (or a bare await that
//      would serialize the pump) does not silently satisfy the rule.

namespace Harbor.Architecture.Tests;

using System.Text.RegularExpressions;
using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: the OpenAI-compatible SSE pump carries an
///     <c>OnlyOnFaulted</c> fault observer instead of a bare task discard.
/// </summary>
public sealed class OpenAiCompatiblePumpObserveRule
{
    private static string PumpSource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Providers.OpenAiCompatible", "OpenAiCompatibleLlmClient.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string[] MethodWindow(string[] lines, int start, int maxLines = 130)
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
            if (lines[i].Contains("StreamAsync", StringComparison.Ordinal)
                && lines[i].Contains("IAsyncEnumerable<LlmEvent>", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The bare-detach ban's sub-id, shared by the rule data and the parser.</summary>
    private const string BareDetachSubId = "BARE-PUMP-DETACH";

    // MECHANISM (#1086, step 2, conveyor batch-16)
    // --------------------------------------------
    // The ban below is a ScanRule: the bare-detach spelling as one Forbidden
    // row, six planted controls, the graded slice as the MustContain floor, a
    // discovery floor. Enumeration, matching and the control/discovery verdicts
    // are ScanRunner's; this file keeps the issue prose, the patterns, the
    // behaviour-adjacent pins, and the test names.
    //
    // The verdict is ParsePumpDetach through the Func-overload, carrying the
    // original line scan verbatim — the _ = Task.Run( filter, the // comment
    // strip, the chained-observer check with its forward lookahead — via the
    // shared FindBareDetachLines helper, with no hatch and an empty baseline:
    // zero bare detaches in this slice is the whole rule. The Forbidden row
    // documents the banned spelling; the runner does not execute it (the rule
    // grades through ParsePumpDetach, whose 120-line chain check a line regex
    // cannot express).
    //
    // The lookahead is 120 lines because the fixed shape is not adjacent: the
    // detach opens at `_ = Task.Run(async () =>` and the observer chains ~80
    // lines later at `}).ContinueWith(`. A next-line check (the IdeAbandon
    // shape) would flag the fix itself as bare. The window mirrors the
    // MethodWindow maxLines (130) the oracle pins above.
    //
    // Scope parity is exact: Trees ["src/Harbor.Providers.OpenAiCompatible"]
    // walks what the old PumpSource read — Task.Run( appears nowhere else in
    // the slice (the detach lives in OpenAiCompatibleLlmClient.cs, line 63's
    // comment names the API without the `_ =` detach), so widening the walk
    // from the one file to its directory changes no verdict. MinHits 4 is the
    // slice's file count today: the floor fails when the walk collapses, not
    // when the slice is clean.

    /// <summary>
    ///     The banned spelling: a bare <c>_ = Task.Run(</c> detach. The
    ///     runner does not execute this row (the rule grades through
    ///     <see cref="ParsePumpDetach" />); it documents the shape the rule bans
    ///     and carries the failure text.
    /// </summary>
    private static readonly Regex BareDetachShape = new(
        @"_ =\s*Task\.Run\s*\(",
        RegexOptions.Compiled);

    /// <summary>The bare-detach ban as data: one documented shape, planted controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "OpenAiCompatiblePumpObserve",
        Trees = ["src/Harbor.Providers.OpenAiCompatible"],
        Forbidden =
        [
            new ScanForbidden(
                BareDetachSubId,
                BareDetachShape,
                "chain the fault observer in the same statement chain "
                + "(_ = Task.Run(...).ContinueWith(..., OnlyOnFaulted, ...)) — "
                + "a bare detach parks a pump fault where only "
                + "finalization can see it. See issue #1099."),
        ],
        Controls =
        [
            new ScanControl(
                "Planted/BareDetach.cs",
                "        _ = Task.Run(async () =>",
                BareDetachSubId),
            new ScanControl(
                "Planted/BareDetachTrailingComment.cs",
                "        _ = Task.Run(async () => // dropped observer",
                BareDetachSubId),
            new ScanControl(
                "Fixed/ChainedSameLine.cs",
                "        _ = Task.Run(async () => {}).ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);",
                null),
            new ScanControl(
                "Fixed/ChainedNextLine.cs",
                "        _ = Task.Run(async () =>\n"
                + "            .ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);",
                null),
            new ScanControl(
                "NearMiss/CommentMentionsTaskRun.cs",
                "        // #1104: no cancellationToken on Task.Run — a pre-cancelled token",
                null),
            new ScanControl(
                "NearMiss/Unrelated.cs",
                "        await writer.WriteAsync(evt, token).ConfigureAwait(false);",
                null),
        ],
        // The slice holds four C# files today, including OpenAiCompatibleLlmClient.cs; a
        // smaller count means the walk broke rather than the slice is clean.
        MinHits = 4,
        MustContain =
        [
            "src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs",
        ],
        CustomParse = ParsePumpDetach,
    };

    /// <summary>
    ///     The custom parser: the original detach scan over one file's raw source —
    ///     the marker filter, the comment strip and the chain check verbatim
    ///     (via <see cref="FindBareDetachLines" />), one report per line.
    /// </summary>
    private static IEnumerable<ScanHit> ParsePumpDetach(string displayPath, string rawSource)
    {
        string[] lines = rawSource.Split('\n');
        foreach (int index in FindBareDetachLines(lines))
        {
            yield return new ScanHit(BareDetachSubId, displayPath, index + 1, lines[index].Trim());
        }
    }

    /// <summary>
    ///     0-based line numbers of the bare detach sites: lines starting a
    ///     <c>_ = Task.Run(</c> detach with no <c>.ContinueWith(</c> observer
    ///     chained within the next 120 lines. Comment text is stripped before
    ///     matching, so the #1104 note naming the API is not a site.
    /// </summary>
    private static List<int> FindBareDetachLines(IReadOnlyList<string> lines)
    {
        var bare = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            string stripped = StripLineComment(lines[i]);
            if (!stripped.Contains("_ = Task.Run(", StringComparison.Ordinal))
            {
                continue;
            }

            // The fixed shape chains the observer in the same statement chain,
            // ~80 lines below the detach; a bare discard ends without one. The
            // window mirrors the MethodWindow maxLines the oracle uses above.
            bool chained = false;
            int end = Math.Min(lines.Count, i + 121);
            for (int j = i; j < end; j++)
            {
                if (StripLineComment(lines[j]).Contains(".ContinueWith(", StringComparison.Ordinal))
                {
                    chained = true;
                    break;
                }
            }

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
            .Because("the rule reads OpenAiCompatibleLlmClient.cs from the checkout");

        string source = PumpSource();
        await Assert.That(source).IsNotEmpty()
            .Because("OpenAiCompatibleLlmClient.cs must be readable from the checkout");

        await Assert.That(source).Contains("StreamAsync")
            .Because("StreamAsync is the one pump site this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");
    }

    /// <summary>
    ///     #1099 fix, pinned: no bare <c>_ = Task.Run(</c> discard survives
    ///     in the pump. The detach must carry its fault observer in the same
    ///     statement chain, so dropping the continuation re-introduces the
    ///     exact pre-fix shape — and fails here.
    /// </summary>
    [Test]
    public async Task PumpDetach_IsObserved()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads OpenAiCompatibleLlmClient.cs from the checkout");

        string[] lines = PumpSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("StreamAsync must exist in OpenAiCompatibleLlmClient.cs; if it "
                   + "was renamed, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains("Task.Run(")
            .Because("the pump detach site must exist in StreamAsync; if the "
                   + "scheduling was reworked, this rule must be re-pointed");

        await Assert.That(window).Contains("TaskContinuationOptions.OnlyOnFaulted")
            .Because("a bare _ = Task.Run( detach parks a pump fault where only "
                   + "finalization can see it (#1099): the detach must chain its "
                   + "OnlyOnFaulted observer in the same method");

        List<int> bare = FindBareDetachLines(lines);

        // The ScanRule half: every bare detach in the slice. No baseline rows
        // and no file hatch — zero bare detaches here is the whole rule (see
        // the MECHANISM note above).
        List<string> illegal = ScanRunner.Evaluate(Rule);

        await Assert.That(bare).IsEmpty()
            .Because("a bare _ = Task.Run( detach parks a pump fault where only "
                   + "finalization can see it (#1099): the detach must chain its "
                   + "OnlyOnFaulted observer in the same statement chain. "
                   + "Offending 1-based lines: " + string.Join(", ", bare.Select(i => i + 1)));

        await Assert.That(illegal.Count).IsEqualTo(0)
            .Because("a bare _ = Task.Run( detach parks a pump fault where only "
                   + "finalization can see it (#1099): the detach must chain its "
                   + "OnlyOnFaulted observer in the same statement chain. "
                   + "ScanRule reports: " + string.Join("; ", illegal));
    }

    /// <summary>
    ///     The chained observer is fault-only: a continuation without
    ///     <c>OnlyOnFaulted</c> would run on success too and could mask the
    ///     normal path, while no continuation at all is the pre-fix defect.
    /// </summary>
    [Test]
    public async Task PumpDetach_PinsOnlyOnFaulted()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads OpenAiCompatibleLlmClient.cs from the checkout");

        string[] lines = PumpSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("StreamAsync must exist in OpenAiCompatibleLlmClient.cs; if it "
                   + "was reworked, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains(".ContinueWith(")
            .Because("the detach observer must be a chained ContinueWith on the pump "
                   + "task, not a second completion path (#1099)");

        await Assert.That(window).Contains("StreamTaskFailed")
            .Because("the detach observer must reuse the StreamTaskFailed sink: it is "
                   + "the pump's fault surface, not a new log shape (#1099)");
    }

    /// <summary>
    ///     The planted controls — every detach shape and every near-miss — drive
    ///     the REAL parser: bare detaches reported under their sub-id, the
    ///     chained observer and the comment lookalike silent.
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
                "src/Harbor.Providers.OpenAiCompatible holds four C# files, including OpenAiCompatibleLlmClient.cs; "
                + "a smaller count — or a scope that stopped seeing that file — means the "
                + "directory was renamed or the walk stopped matching, and "
                + "PumpDetach_IsObserved becomes vacuous. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Non-vacuity: the SAME matcher must fire on a planted bare detach, stay
    ///     silent on the chained observer and the comment lookalike, and stay silent on
    ///     unrelated lines. This is what separates "the guard is green" from
    ///     "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnPlantedDetach_AndStaysSilentOtherwise()
    {
        await Assert.That(FindBareDetachLines(
            ["        _ = Task.Run(async () =>"]).Count).IsGreaterThan(0)
            .Because("a planted bare detach must be detected, or the guard is blind");

        await Assert.That(FindBareDetachLines(
            ["        _ = Task.Run(async () => // dropped observer"]).Count).IsGreaterThan(0)
            .Because("a bare detach with a trailing comment is still a bare detach");

        await Assert.That(FindBareDetachLines(
            ["        _ = Task.Run(async () => {}).ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);"]).Count).IsEqualTo(0)
            .Because("the chained observer is the fix and must not trip the guard, or the guard is noise");

        await Assert.That(FindBareDetachLines(
            [
                "        _ = Task.Run(async () =>",
                "            .ContinueWith(t => Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);",
            ]).Count).IsEqualTo(0)
            .Because("a continuation on the next line is the same statement chain and the same fix");

        await Assert.That(FindBareDetachLines(
            ["        // #1104: no cancellationToken on Task.Run — a pre-cancelled token"]).Count).IsEqualTo(0)
            .Because("the comment naming the API is not a detach site");

        await Assert.That(FindBareDetachLines(
            ["        await writer.WriteAsync(evt, token).ConfigureAwait(false);"]).Count).IsEqualTo(0)
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
