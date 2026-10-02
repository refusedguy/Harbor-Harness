// CompactionPolicyInvariantTests.cs — the GUARD for #472, slice 1: the history-shaping
// compaction policy.
//
// The citations below name CompactionPolicy.cs, where the policy lives after the split.
// The guard was ARMED BEFORE that split and was red against the same three methods when
// they were still on CompactionService; the line numbers moved with the code and the
// rules did not. Commit 1 of the PR carries the pre-split citations and the red CI run.
//
// WHY THIS GUARDS POLICY AND NOT SIZE
// -----------------------------------
// The obvious guard for a "god class" is a line count. That guard is worthless here,
// and this file says why rather than asserting it: every rule below is a property of
// the COMPACTION OUTCOME — what the next LLM request is built from — and every one of
// them is falsifiable with a message list and a model. A file that grows to 900 lines
// of well-factored summarization violates nothing here, and a 40-line file that returns
// an unpaired tool result violates three of the four rules. Class size is a proxy for
// the thing we actually care about; these four rules ARE the thing.
//
// The four rules, each quoted from the code that claims it
// ---------------------------------------------------------
//   1. NEITHER TRUNCATION MAY OPEN THE KEPT SLICE ON AN ORPHAN TOOL RESULT.
//      CompactionPolicy.cs:58-60 ("The cut point never lands on a ToolResultMessage:
//      orphan tool results whose assistant tool_call was dropped would be rejected by
//      providers") and :147-148 (the same claim in TruncateToFitStrict).
//   2. TruncateToFitStrict ALWAYS REDUCES STRICTLY ABOVE THE KEEP FLOOR.
//      CompactionPolicy.cs:144-146 ("Whenever total > 4 the target is strictly below
//      total, so reduction is guaranteed even when the whole history would trivially
//      fit"). 4 is a real threshold in the code, not one invented here: it is
//      `MinimumKeptMessages` at :168.
//   3. MaterializeCompactedView IS IDEMPOTENT.
//      The compacted view is recomputed from the raw append-only history on every turn
//      (TurnRunner.cs:120, CompactionBehavior.cs:78). Materializing a view that has
//      already been materialized must not shrink it again, or the session bleeds one
//      turn at a time.
//   4. MaterializeCompactedView FAILS SAFE.
//      CompactionPolicy.cs:228-230 ("when no summary exists, or the anchor id cannot be
//      resolved, the input instance is returned unchanged rather than risking silent
//      history loss"). Silent history loss is the worst outcome this subsystem has.
//
// Rule 1 is the one that is RED against the code as it stands today, and it is red on
// a shape that is entirely ordinary: an assistant turn that calls several tools in
// parallel ends in a RUN of ToolResultMessages. When the token budget is small enough
// that the whole run fits but the assistant's own message does not, the backwards
// budget walk stops INSIDE the run, the forward orphan-skip then walks off the end of
// the list, and the "always keep at least one message" clamp puts the cut back on the
// last result — a kept slice that is exactly one orphan ToolResultMessage.
//
// That slice goes straight into the next LLM request: TurnRunner.cs:136 calls
// TruncateToFitStrict and hands the result to the request builder, on the same turn
// where summarization has ALREADY failed. The provider rejects an unpaired tool result,
// so the run that was already degraded becomes a hard failure — and the fallback stays
// engaged for the rest of the session, so every later turn re-derives from the same
// malformed shape. The two promises in the doc comment ("never opens on an orphan",
// "at least one message is always kept") are mutually unsatisfiable in that case, and
// the code silently picks one without saying so.
//
// NON-VACUITY
// -----------
// A guard that cannot fail is worse than no guard, so the detector
// (FindOrphanOpenings) and the reduction comparator (KeptCount) are the SAME functions
// the real rules use, and three Control_ tests push known-bad input through them:
// an orphan is reported, a well-formed slice is not, and a non-reducing history is
// reported as non-reducing. If someone rewrites either comparator into something that
// always passes, the controls fail and the real rules are green for the wrong reason.
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO
// --------------------------------------
// It does not assert a class size, and it does not demand the refactor that #472 is
// owed. It grades the policy as it behaves today, so it can be armed BEFORE the split
// and still be true after it.

using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;

namespace Harbor.Core.Tests;

/// <summary>
///     Guards the history-shaping compaction policy in <c>CompactionPolicy</c>: what the
///     next LLM request is built from. Four rules, each one quoted from the code that
///     claims it, plus the non-vacuity controls that keep the rules load-bearing.
/// </summary>
public class CompactionPolicyInvariantTests
{
    /// <summary>
    ///     A model whose effective truncation budget is exactly
    ///     <see cref="ExpectedBudget" />.
    ///     <para>
    ///         ContextWindow 4096, MaxOutputTokens 1024, reserve 16384 gives
    ///         4096 - 16384 - 1024 = -13312, which is below the 4096 floor, so
    ///         <c>ComputeTruncationBudget</c> falls back to
    ///         <c>max(4096, ContextWindow / 2)</c> = 4096. That is a budget a message
    ///         can be sized against exactly, which is what makes the sweep below a sweep
    ///         instead of a hand-picked case.
    ///     </para>
    /// </summary>
    private static readonly ModelInfo Budgeted = new(
        "test-model",
        "test",
        "Budgeted Test Model",
        4_096,
        1_024,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    /// <summary>The budget <see cref="Budgeted" /> resolves to, restated as data.</summary>
    private const int ExpectedBudget = 4_096;

    /// <summary>Chars that put a single message over <see cref="ExpectedBudget" />.</summary>
    private const int OverBudgetChars = 20_000;

    /// <summary>
    ///     <c>MinimumKeptMessages</c> from <c>TruncateToFitStrict</c>: at or below this
    ///     many messages the strict variant is allowed to keep everything, because
    ///     guaranteeing a reduction would mean throwing away the whole conversation.
    ///     Counted as a constant here so the rule and the code quote the same number.
    /// </summary>
    private const int KeepFloor = 4;

    private static ITokenTracker Tracker => new TokenTracker();

    // =====================================================================
    // 1. The rules. Rules 1 and 2 are RED against the code as it stands.
    // =====================================================================

    /// <summary>
    ///     Rule 1, for <c>TruncateToFit</c>: no kept slice may open on a
    ///     <see cref="ToolResultMessage" /> whose assistant tool_call is not in the
    ///     slice. The doc comment promises it; this is the promise with a test.
    /// </summary>
    [Test]
    public async Task TruncateToFit_KeptSliceNeverOpensOnAnOrphanToolResult()
    {
        await Assert.That(ComputeTruncationBudgetFor(Budgeted)).IsEqualTo(ExpectedBudget)
            .Because("the sweep below is sized against this budget. If the product "
                   + "changes ComputeTruncationBudget, the messages in the sweep stop "
                   + "straddling it and this test goes green without testing the case it "
                   + "was written for — so the budget is asserted, not assumed");

        List<string> violations = [];
        foreach ((string name, AgentMessage[] history) in Sweep())
        {
            IReadOnlyList<AgentMessage> kept = CompactionPolicy.TruncateToFit(
                history, Budgeted, Tracker);

            violations.AddRange(Describe(name, "TruncateToFit", history, kept));
        }

        await Assert.That(violations).IsEmpty()
            .Because("CompactionPolicy.TruncateToFit documents at CompactionPolicy.cs:58 "
                   + "that the cut point never lands on a ToolResultMessage, because an orphan "
                   + "result whose assistant tool_call was dropped is rejected by the "
                   + "provider. These histories broke that: "
                   + string.Join(" | ", violations));
    }

    /// <summary>
    ///     Rule 1 again, for <c>TruncateToFitStrict</c> — and this is the one that
    ///     matters, because unlike its sibling this method is ON the live path:
    ///     <c>TurnRunner.cs:136</c> calls it and feeds the result straight into the next
    ///     LLM request, on a turn where summarization has already failed.
    /// </summary>
    [Test]
    public async Task TruncateToFitStrict_KeptSliceNeverOpensOnAnOrphanToolResult()
    {
        List<string> violations = [];
        foreach ((string name, AgentMessage[] history) in Sweep())
        {
            IReadOnlyList<AgentMessage> kept = CompactionPolicy.TruncateToFitStrict(
                history, Budgeted, Tracker);

            violations.AddRange(Describe(name, "TruncateToFitStrict", history, kept));
        }

        await Assert.That(violations).IsEmpty()
            .Because("CompactionPolicy.TruncateToFitStrict documents the same orphan "
                   + "guarantee as TruncateToFit, and it is the copy that runs in "
                   + "production. When the kept budget lands entirely inside a run of "
                   + "parallel tool results, the forward orphan-skip walks off the end "
                   + "and the 'always keep one message' clamp puts the cut back on the "
                   + "last result. TurnRunner then requests a conversation whose first "
                   + "message is an unpaired tool result. Violations: "
                   + string.Join(" | ", violations));
    }

    /// <summary>
    ///     Rule 1's ONE documented exception, graded rather than left in a comment. When the
    ///     entire history is tool results there is no assistant turn to step back to and no
    ///     legal slice exists; the newest message is kept, because the input was already
    ///     malformed and an empty history is strictly worse. This input is not reachable
    ///     from session history — that is append-only from a user message, and its compacted
    ///     view starts with a summary or a user turn — so it is a public-library edge case,
    ///     not a product path, and the assertion below is that we behave as documented
    ///     rather than that we somehow fix it.
    /// </summary>
    [Test]
    public async Task TruncateToFit_KeepsTheNewestMessageWhenNothingLegalExists()
    {
        AgentMessage[] allToolResults =
        [
            ToolResult("read", "first"),
            ToolResult("write", "second"),
            ToolResult("bash", "third"),
        ];

        IReadOnlyList<AgentMessage> kept =
            CompactionPolicy.TruncateToFit(allToolResults, Budgeted, Tracker);

        await Assert.That(kept.Count).IsEqualTo(1)
            .Because("an empty kept history would discard the conversation outright, which is "
                   + "the worse of the two bad options; one message keeps it recoverable");

        await Assert.That(ReferenceEquals(kept[0], allToolResults[^1])).IsTrue()
            .Because("the newest message is the one to keep, and it is kept by identity — a "
                   + "rebuilt-but-equal message would hide which arm of the policy ran");

        // The same input through the strict variant must ALSO reduce: three in, at most one
        // out, so a malformed history cannot become a request that grows.
        IReadOnlyList<AgentMessage> strictKept =
            CompactionPolicy.TruncateToFitStrict(allToolResults, Budgeted, Tracker);

        await Assert.That(strictKept.Count).IsLessThan(allToolResults.Length)
            .Because("the strict variant's contract is a strict reduction, and that contract "
                   + "does not have an exception for malformed input");
    }

    /// <summary>
    ///     Rule 2: above the keep floor, the strict variant's whole reason for existing
    ///     is that the next request must actually shrink. A variant that returned the
    ///     input unchanged on an overfull history would loop: fail to compact, fall back,
    ///     request the same overfull history, fail again.
    /// </summary>
    [Test]
    public async Task TruncateToFitStrict_ReducesStrictlyAboveTheKeepFloor()
    {
        var nonReductions = new List<string>();
        foreach ((string name, AgentMessage[] history) in Sweep())
        {
            if (history.Length <= KeepFloor)
            {
                continue;
            }

            IReadOnlyList<AgentMessage> kept = CompactionPolicy.TruncateToFitStrict(
                history, Budgeted, Tracker);

            if (!KeptCount(kept, history.Length, name, "TruncateToFitStrict", out string why))
            {
                nonReductions.Add(why);
            }
        }

        await Assert.That(nonReductions).IsEmpty()
            .Because("TruncateToFitStrict states that whenever the history is longer than "
                   + $"{KeepFloor} messages the result is strictly shorter, 'even when the "
                   + "whole history would trivially fit'. It is engaged after a FAILED "
                   + "compaction, so a non-reduction is not a missed optimization: the "
                   + "run is stuck re-requesting a known-overfull context. Cases: "
                   + string.Join(" | ", nonReductions));
    }

    /// <summary>
    ///     Rule 3: materializing an already-materialized view changes nothing. The view
    ///     is rebuilt from the raw append-only history on every single turn
    ///     (<c>TurnRunner.cs:120</c>, <c>CompactionBehavior.cs:78</c>), so a second pass
    ///     over an already-folded view is a guaranteed code path, not a hypothetical.
    /// </summary>
    [Test]
    public async Task MaterializeCompactedView_IsIdempotent()
    {
        List<string> violations = [];
        foreach ((string name, AgentMessage[] history) in SummaryAnchoredSweep())
        {
            IReadOnlyList<AgentMessage> once =
                CompactionPolicy.MaterializeCompactedView(history);
            IReadOnlyList<AgentMessage> twice =
                CompactionPolicy.MaterializeCompactedView(once);

            if (once.Count != twice.Count)
            {
                violations.Add(
                    $"{name}: one pass kept {once.Count}, a second pass on the result "
                  + $"kept {twice.Count} — the view is re-folded on itself");
                continue;
            }

            for (int i = 0; i < once.Count; i++)
            {
                if (!ReferenceEquals(once[i], twice[i]))
                {
                    violations.Add(
                        $"{name}: message {i} changed identity between passes "
                      + $"({once[i].Id} -> {twice[i].Id})");
                    break;
                }
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because("the compacted view is recomputed from the raw history on every "
                   + "turn, so materializing it twice must be a fixed point. A second pass "
                   + "that drops more messages is history bleeding away one turn at a "
                   + "time, with nothing logged. Violations: " + string.Join(" | ", violations));
    }

    /// <summary>
    ///     Rule 4: the two fail-safe returns are the same instance, not an equal copy.
    ///     Returning a rebuilt-but-equal list would hide which arm ran; callers rely on
    ///     the identity to know that nothing was compacted.
    /// </summary>
    [Test]
    public async Task MaterializeCompactedView_ReturnsTheInputWhenThereIsNothingToMaterialize()
    {
        AgentMessage[] noSummary = [User("hello"), Assistant("hi")];
        IReadOnlyList<AgentMessage> noSummaryResult =
            CompactionPolicy.MaterializeCompactedView(noSummary);

        await Assert.That(ReferenceEquals(noSummaryResult, noSummary)).IsTrue()
            .Because("with no summary anchor there is nothing to fold, and the documented "
                   + "fail-safe is to return the input unchanged rather than rebuild it");

        // A summary whose anchor names a message that is not in the history: the other
        // fail-safe arm, and the one that protects against silent history loss.
        AgentMessage[] danglingAnchor =
        [
            User("hello"),
            Summary("everything so far, condensed", "no-such-message-id"),
            Assistant("later"),
        ];
        IReadOnlyList<AgentMessage> danglingResult =
            CompactionPolicy.MaterializeCompactedView(danglingAnchor);

        await Assert.That(ReferenceEquals(danglingResult, danglingAnchor)).IsTrue()
            .Because("an unresolvable anchor means the summary folded in an unknown slice; "
                   + "guessing would silently discard history, so the input is returned "
                   + "as-is and the next turn retries with the full history");
    }

    // =====================================================================
    // 2. Non-vacuity. Known-bad input through the SAME detector and the
    //    SAME comparator the rules above use.
    // =====================================================================

    /// <summary>
    ///     The detector reports an orphan. If this fails, the rule above is green for the
    ///     wrong reason — it would be asserting that no slice ever opens on a tool result
    ///     using a detector that cannot recognise one.
    /// </summary>
    [Test]
    public async Task Control_TheDetectorReportsAnOrphanWhenTheSliceOpensOnOne()
    {
        AgentMessage[] orphan =
        [
            ToolResult("read", "the file says hello"),
            Assistant("and then I carried on"),
        ];

        List<int> openings = FindOrphanOpenings(orphan);

        await Assert.That(openings).IsEquivalentTo(new[] { 0 })
            .Because("index 0 is a ToolResultMessage with no assistant tool_call before it "
                   + "in the slice, which is the shape TurnRunner would put in a request. "
                   + "The rules above call exactly this function, so a detector that stayed "
                   + "silent here would make them unfalsifiable");
    }

    /// <summary>
    ///     The detector is silent on a properly paired slice — the other direction. A
    ///     detector that reported everything would make the rules fail forever and get
    ///     disabled, which is the same outcome as one that never fires.
    /// </summary>
    [Test]
    public async Task Control_TheDetectorIsSilentOnAWellFormedSlice()
    {
        AgentMessage[] paired =
        [
            Assistant("reading the file"),
            ToolResult("read", "the file says hello"),
            Assistant("now writing the fix"),
        ];

        List<int> openings = FindOrphanOpenings(paired);

        await Assert.That(openings).IsEmpty()
            .Because("the tool result is preceded by the assistant turn that requested it, "
                   + "so there is nothing to report. A detector that flagged this would be "
                   + "grading shape rather than correctness, and the rules above would be "
                   + "turned off rather than fixed");
    }

    /// <summary>
    ///     The reduction comparator reports a non-reduction. Same contract for the second
    ///     comparator: it has to be able to say no.
    /// </summary>
    [Test]
    public async Task Control_TheReductionComparatorReportsANonReduction()
    {
        AgentMessage[] six =
        [
            User("a"), Assistant("b"), Assistant("c"),
            Assistant("d"), Assistant("e"), Assistant("f"),
        ];

        bool reduced = KeptCount(six, six.Length, "control", "control", out string why);

        await Assert.That(reduced).IsFalse()
            .Because("six messages in, six messages out is exactly the loop "
                   + "TruncateToFitStrict exists to break. The comparator must name it, or "
                   + "TruncateToFitStrict_ReducesStrictlyAboveTheKeepFloor cannot fail");

        await Assert.That(why).IsNotEmpty()
            .Because("a comparator that reports a non-reduction without saying which case "
                   + "and by how much leaves the author of the next break with nothing to "
                   + "go on");
    }

    // =====================================================================
    // 3. The detector, the comparator, and the sweep — shared by the rules
    //    and the controls, which is what makes the controls proof.
    // =====================================================================

    /// <summary>
    ///     The indices of the LEADING run of <see cref="ToolResultMessage" />s in a kept
    ///     slice — which is the only orphan this can prove from the slice alone.
    ///     <para>
    ///         A tool result at index 0 has, by construction, no tool_call to pair it:
    ///         whatever issued it is not in the slice, because the slice starts after it.
    ///         A result further along may be perfectly paired, and whether it is cannot be
    ///         told from the kept messages alone — an assistant turn precedes it, but the
    ///         tool_call could be one this slice dropped. So a result at index &gt; 0 is not
    ///         reported. Over-reporting would be the safe direction for a
    ///         never-returns-a-bad-thing detector, but this one grades a defect with a
    ///         specific shape, and grading shape instead of correctness turns the rule off
    ///         rather than fixing it.
    ///     </para>
    /// </summary>
    internal static List<int> FindOrphanOpenings(IReadOnlyList<AgentMessage> kept)
    {
        var openings = new List<int>();
        for (int i = 0; i < kept.Count; i++)
        {
            if (kept[i] is not ToolResultMessage)
            {
                break;
            }

            openings.Add(i);
        }

        return openings;
    }

    /// <summary>
    ///     Whether the kept slice is strictly shorter than the input. Writes the
    ///     sentence a failure should read into <paramref name="why" /> whether it passed
    ///     or not, so the controls and the rule report identically.
    /// </summary>
    internal static bool KeptCount(
        IReadOnlyList<AgentMessage> kept,
        int inputCount,
        string caseName,
        string method,
        out string why)
    {
        why = $"{method}({caseName}): {inputCount} messages in, {kept.Count} out";
        return kept.Count < inputCount;
    }

    /// <summary>
    ///     Re-derives the effective budget the way the product does, so rule 1 can assert
    ///     the number the sweep was sized against instead of trusting a comment.
    /// </summary>
    internal static int ComputeTruncationBudgetFor(ModelInfo model) =>
        model.ContextWindow - CompactionPolicy.DefaultReserveTokens - model.MaxOutputTokens < 4_096
            ? Math.Max(4_096, model.ContextWindow / 2)
            : model.ContextWindow - CompactionPolicy.DefaultReserveTokens - model.MaxOutputTokens;

    /// <summary>
    ///     The truncation sweep: histories whose newest run is a set of parallel tool
    ///     results, which is what an assistant turn with several tool calls always ends
    ///     with. The assistant's own message is sized over the whole budget so the
    ///     backwards walk has to stop inside the run — that is the whole mechanism.
    /// </summary>
    private static List<(string Name, AgentMessage[] History)> Sweep()
    {
        var cases = new List<(string, AgentMessage[])>();

        for (int calls = 1; calls <= 4; calls++)
        {
            var history = new List<AgentMessage>
            {
                User("start the task"),
                BigAssistantWithToolCalls(calls),
            };
            for (int i = 0; i < calls; i++)
            {
                history.Add(ToolResult("read", $"chunk {i}"));
            }

            cases.Add(($"assistant + {calls} parallel result(s)", [.. history]));
        }

        // The same run of results with no assistant in the history at all. A session that
        // starts this way is already malformed; what matters is that the policy does not
        // make it worse by keeping only the result.
        cases.Add((
            "results with no assistant anywhere",
            new AgentMessage[]
            {
                User("start the task"),
                ToolResult("read", "a"),
                ToolResult("read", "b"),
            }));

        // A result run preceded by small messages, so the backwards walk gets several
        // steps further in before it hits the oversized assistant.
        cases.Add((
            "small prefix, oversized assistant, 3 results",
            new AgentMessage[]
            {
                User("start the task"),
                Assistant("noted"),
                BigAssistantWithToolCalls(3),
                ToolResult("read", "a"),
                ToolResult("read", "b"),
                ToolResult("read", "c"),
            }));

        // Well under the budget: the variant must still leave a legal slice.
        cases.Add((
            "entirely within budget",
            new AgentMessage[]
            {
                User("start the task"),
                Assistant("done"),
                ToolResult("read", "a"),
                ToolResult("read", "b"),
            }));

        return cases;
    }

    /// <summary>
    ///     Histories that actually carry a summary anchor, for the materialization rules.
    ///     The anchor's <c>SummaryFirstKeptId</c> points at a real message, so the
    ///     idempotence test has a genuine fold to fold twice.
    /// </summary>
    private static List<(string Name, AgentMessage[] History)> SummaryAnchoredSweep()
    {
        UserMessage first = User("start the task");
        AgentMessage second = Assistant("reading the file");
        AgentMessage third = Assistant("writing the fix");

        return
        [
            ("anchor keeps a two-message tail",
            [
                first, second, third,
                Summary("everything so far, condensed", second.Id),
                Assistant("and the tests pass"),
            ]),
            ("anchor folded the entire pre-summary history",
            [
                first, second, third,
                Summary("everything so far, condensed", firstKeptId: null),
                Assistant("and the tests pass"),
            ]),
        ];
    }

    /// <summary>
    ///     One sentence per violated case, naming the history so the failure points at a
    ///     shape rather than at a line in the policy.
    /// </summary>
    private static IEnumerable<string> Describe(
        string caseName,
        string method,
        AgentMessage[] history,
        IReadOnlyList<AgentMessage> kept)
    {
        List<int> openings = FindOrphanOpenings(kept);
        if (openings.Count == 0)
        {
            yield break;
        }

        var openingRoles = new List<string>();
        foreach (int index in openings)
        {
            openingRoles.Add($"[{index}]={kept[index].Role}");
        }

        yield return $"{method}({caseName}): kept {kept.Count} of {history.Length}, "
                   + $"slice opens on orphan tool result(s) at {string.Join(",", openingRoles)}";
    }

    // ---- fixtures -------------------------------------------------------

    private static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    private static AssistantMessage Assistant(string text) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    private static AssistantMessage Summary(string text, string? firstKeptId) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model",
        IsSummary: true,
        SummaryFirstKeptId: firstKeptId);

    /// <summary>
    ///     An assistant turn that explains at length and then calls several tools. Its
    ///     prose alone puts it over <see cref="ExpectedBudget" />, which is what forces
    ///     the backwards walk to stop before reaching it and land inside the result run.
    /// </summary>
    private static AssistantMessage BigAssistantWithToolCalls(int calls)
    {
        var parts = new List<ContentPart> { new TextPart(new string('x', OverBudgetChars)) };
        for (int i = 0; i < calls; i++)
        {
            parts.Add(new ToolCallPart(
                $"call-{i}",
                "read",
                JsonDocument.Parse($$"""{"path":"file{{i}}.txt"}""").RootElement.Clone()));
        }

        return new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            "session-1",
            DateTimeOffset.UtcNow,
            parts,
            StopReason.ToolUse,
            new Usage(0, 0),
            "test-model");
    }

    private static ToolResultMessage ToolResult(string toolName, string output) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new ToolResultEntry("call-0", toolName, output, false) });
}
