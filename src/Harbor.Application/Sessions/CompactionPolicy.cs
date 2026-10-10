using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Application.Sessions;

/// <summary>
///     The history-shaping half of context management: given a raw session history and a
///     model, what should the next LLM request be built from?
/// </summary>
/// <remarks>
///     <para>
///         Split out of <see cref="CompactionService" /> for #472, and the reason is not
///         the line count. Two of the three members here are called by files that are NOT
///         <see cref="ICompactionService" /> consumers at all: <c>TurnRunner.cs:138</c>
///         and <c>CompactionBehavior.cs:78</c> reached into a DI-registered implementation
///         class to call its statics. <c>CompactionBehavior</c> has
///         <see cref="ICompactionService" /> injected and still reached for the static,
///         which is the coupling in one line. The summarizer needs an
///         <c>IProviderRegistry</c> and an <c>ILogger</c>; none of this needs either, and
///         none of it is summarization.
///     </para>
///     <para>
///         Pure and static on purpose: no state, no DI, no new interface — feature freeze
///         #555. Every rule about what these methods return is graded by
///         <c>CompactionPolicyInvariantTests</c>, which is where the orphan guarantee
///         below is enforced rather than merely claimed.
///     </para>
///     <para>
///         Which of the two truncation policies is live has its answer in one place, and
///         this is it: <see cref="TruncateToFitStrict" />, from <c>TurnRunner.cs:138</c>, on
///         a turn where summarization has already failed. <see cref="TruncateToFit" /> is
///         its non-reducing sibling, has no product call site, and is kept as public
///         surface pending the owner call on #772. The two read alike on purpose, so check
///         the member's own doc before reaching for either.
///     </para>
/// </remarks>
public static class CompactionPolicy
{
    /// <summary>
    ///     Default token reserve used by the truncation methods when the caller does not
    ///     supply one.
    /// </summary>
    public const int DefaultReserveTokens = 16384;

    /// <summary>
    ///     Floor for the truncation budget so very small context windows still
    ///     keep a usable slice of history instead of an effectively empty one.
    /// </summary>
    private const int MinimumTruncationBudget = 4096;

    /// <summary>
    ///     Aggressive fallback for when LLM-based compaction fails: keep only
    ///     the most recent messages that fit the model's context budget
    ///     (<c>ContextWindow − reserve − MaxOutputTokens</c>) and drop the older
    ///     middle/head. The result is a plain tail slice — no summary is
    ///     produced, but the next request is no longer known-overfull.
    ///     <para>
    ///         The cut point never lands on a <see cref="ToolResultMessage" />:
    ///         orphan tool results whose assistant tool_call was dropped would
    ///         be rejected by providers. Orphaned results at the boundary are
    ///         dropped together with the head instead. At least one message is
    ///         always kept.
    ///     </para>
    ///     <para>
    ///         <b>No product call site (#772).</b> The live compaction-failure fallback is
    ///         <see cref="TruncateToFitStrict" />, called at <c>TurnRunner.cs:138</c>. This
    ///         method is its non-reducing sibling and nothing in the product calls it. The
    ///         two are near-duplicates by move-history, and this one is the more forgiving —
    ///         it returns the history unchanged when the history already fits, which is
    ///         exactly why it was never wired up as the fallback.
    ///         It is retained deliberately rather than left as weight nobody owns:
    ///         <c>Harbor.Application</c> is a packable library and this is public surface, so
    ///         removing it is a product decision (open on #772), not a cleanup. The rules
    ///         above stay graded while it waits — see
    ///         <c>CompactionPolicyInvariantTests.TruncateToFit_KeptSliceNeverOpensOnAnOrphanToolResult</c>
    ///         and <c>TruncateToFit_KeepsTheNewestMessageWhenNothingLegalExists</c> — so the
    ///         contract documented here is the contract the tests enforce, not a promise
    ///         about a code path nothing takes.
    ///     </para>
    /// </summary>
    /// <param name="messages">The current message history.</param>
    /// <param name="model">The target model (context window + output budget).</param>
    /// <param name="tokenTracker">Token estimator used to size the kept tail.</param>
    /// <param name="reserveTokens">Safety reserve below the context window.</param>
    /// <returns>A new list with the kept tail messages, or <paramref name="messages" /> when it is empty.</returns>
    public static IReadOnlyList<AgentMessage> TruncateToFit(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = DefaultReserveTokens)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        int budget = ComputeTruncationBudget(model, reserveTokens);

        // Walk backwards accumulating the newest messages until the budget is hit.
        int tailTokens = 0;
        int tailStart = messages.Count;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            int msgTokens = tokenTracker.EstimateMessage(messages[i]);
            if (tailTokens + msgTokens > budget)
            {
                break;
            }

            tailTokens += msgTokens;
            tailStart = i;
        }

        // Never open the kept slice with an orphan tool_result — its assistant
        // tool_call is in the dropped head and providers reject the pair.
        while (tailStart < messages.Count && messages[tailStart] is ToolResultMessage)
        {
            tailStart++;
        }

        // The skip above can consume the whole tail — see RewindToMessageBoundary.
        tailStart = RewindToMessageBoundary(messages, tailStart, messages.Count);

        var kept = new List<AgentMessage>(messages.Count - tailStart);
        for (int i = tailStart; i < messages.Count; i++)
        {
            kept.Add(messages[i]);
        }

        return kept;
    }

    /// <summary>
    ///     Strict-reduction variant of <see cref="TruncateToFit" />, used as the
    ///     compaction-failure fallback. Unlike the budget-fit walk — which keeps
    ///     everything when the history already fits — this ALWAYS drops part of the
    ///     head once the history exceeds a small floor, because after a
    ///     failed LLM compaction the next request must actually shrink, not
    ///     merely be "not provably overfull".
    ///     <para>
    ///         Policy (deterministic): keep system-role messages (none exist in
    ///         session history — the system prompt travels separately on
    ///         <c>LlmRequest</c>) plus the newest K messages, where
    ///         K = min(max(total / 2, 4), budgetFit) — the newest half clamped
    ///         to [4 .. budget-fit]. Whenever total &gt; 4 the target is strictly
    ///         below total, so reduction is guaranteed even when the whole
    ///         history would trivially fit. The cut point never opens on an
    ///         orphan <see cref="ToolResultMessage" /> (see <see cref="TruncateToFit" />),
    ///         and at least one message is always kept.
    ///     </para>
    /// </summary>
    /// <param name="messages">The current message history.</param>
    /// <param name="model">The target model (context window + output budget).</param>
    /// <param name="tokenTracker">Token estimator used to size the budget-fit ceiling.</param>
    /// <param name="reserveTokens">Safety reserve below the context window.</param>
    /// <returns>A new list holding strictly fewer messages than the input whenever the input exceeds the keep floor.</returns>
    public static IReadOnlyList<AgentMessage> TruncateToFitStrict(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = DefaultReserveTokens)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        const int MinimumKeptMessages = 4;
        int total = messages.Count;

        // Budget-fit ceiling: size of the newest run that fits the token
        // budget; at least one message is always kept.
        int budget = ComputeTruncationBudget(model, reserveTokens);
        int budgetFit = 0;
        int tailTokens = 0;
        for (int i = total - 1; i >= 0; i--)
        {
            int msgTokens = tokenTracker.EstimateMessage(messages[i]);
            if (tailTokens + msgTokens > budget)
            {
                break;
            }

            tailTokens += msgTokens;
            budgetFit++;
        }

        budgetFit = Math.Max(budgetFit, 1);

        // Newest half, clamped to [MinimumKeptMessages .. budgetFit].
        // For total > MinimumKeptMessages this is strictly less than total.
        int keep = Math.Min(Math.Max(MinimumKeptMessages, total / 2), budgetFit);

        int tailStart = total - keep;

        // Never open the kept slice with an orphan tool_result — its assistant
        // tool_call is in the dropped head and providers reject the pair.
        while (tailStart < total && messages[tailStart] is ToolResultMessage)
        {
            tailStart++;
        }

        // The skip above can consume the whole tail — see RewindToMessageBoundary.
        tailStart = RewindToMessageBoundary(messages, tailStart, total);

        var kept = new List<AgentMessage>(total - tailStart);
        for (int i = tailStart; i < total; i++)
        {
            kept.Add(messages[i]);
        }

        return kept;
    }

    /// <summary>
    ///     Materialize the effective post-compaction history from a raw session
    ///     history that contains compaction summaries.
    ///     <para>
    ///         Compaction is lazy: the raw history keeps every message, and the
    ///         newest <see cref="AssistantMessage.IsSummary" /> message anchors
    ///         the cut through its <see cref="AssistantMessage.SummaryFirstKeptId" />.
    ///         The returned view is <c>[summary] + tail-from-anchor +
    ///         messages-appended-after-the-summary</c> — everything folded into
    ///         the summary is dropped, so token estimation and LLM requests see
    ///         the compacted history instead of an ever-growing raw list.
    ///     </para>
    ///     <para>
    ///         Fail-safe: when no summary exists, or the anchor id cannot be
    ///         resolved, the input instance is returned unchanged rather than
    ///         risking silent history loss.
    ///     </para>
    /// </summary>
    /// <param name="messages">The raw (append-only) session history.</param>
    /// <returns>The compacted view, or <paramref name="messages" /> when nothing is compacted.</returns>
    public static IReadOnlyList<AgentMessage> MaterializeCompactedView(IReadOnlyList<AgentMessage> messages)
    {
        int summaryIndex = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage { IsSummary: true })
            {
                summaryIndex = i;
                break;
            }
        }

        if (summaryIndex < 0)
        {
            return messages;
        }

        var summary = (AssistantMessage)messages[summaryIndex];

        // Resolve the kept-tail start. A null anchor means the summary folded
        // in the ENTIRE pre-summary history (nothing was kept verbatim).
        int keptStart = summaryIndex;
        if (summary.SummaryFirstKeptId is string anchor)
        {
            bool resolved = false;
            for (int i = 0; i < summaryIndex; i++)
            {
                if (string.Equals(messages[i].Id, anchor, StringComparison.Ordinal))
                {
                    keptStart = i;
                    resolved = true;
                    break;
                }
            }

            if (!resolved)
            {
                return messages;
            }
        }

        var view = new List<AgentMessage>(
            1 + summaryIndex - keptStart + (messages.Count - summaryIndex - 1));
        view.Add(summary);
        for (int i = keptStart; i < summaryIndex; i++)
        {
            view.Add(messages[i]);
        }
        for (int i = summaryIndex + 1; i < messages.Count; i++)
        {
            view.Add(messages[i]);
        }

        return view;
    }

    /// <summary>
    ///     Shared token budget for <see cref="TruncateToFit" /> and
    ///     <see cref="TruncateToFitStrict" />: context window minus reserve and
    ///     max output tokens, floored so tiny windows still keep a usable slice.
    /// </summary>
    private static int ComputeTruncationBudget(ModelInfo model, int reserveTokens)
    {
        int budget = model.ContextWindow - reserveTokens - model.MaxOutputTokens;
        if (budget < MinimumTruncationBudget)
        {
            budget = Math.Max(MinimumTruncationBudget, model.ContextWindow / 2);
        }

        return budget;
    }

    /// <summary>
    ///     The boundary a kept slice starts at, once the forward orphan-skip has run.
    ///     <para>
    ///         The skip walks FORWARD off a run of trailing tool results and, when the
    ///         whole budget-fit landed inside that run, all the way past the end of the
    ///         list. The code this replaces clamped to <c>tailLength - 1</c> — "keep at
    ///         least one message" — which put the cut straight back inside the run. The
    ///         kept slice was then a single unpaired <see cref="ToolResultMessage" />,
    ///         which providers reject, so the two doc promises above ("never opens on an
    ///         orphan" and "at least one message is always kept") were being resolved
    ///         against each other silently.
    ///     </para>
    ///     <para>
    ///         Stepping BACK to the nearest message that is not a tool result is the only
    ///         slice that stays paired: the assistant turn that issued those tool calls
    ///         comes back with its results. That can exceed the token budget, and that is
    ///         the lesser evil by a wide margin — an overfull request is refused with a
    ///         context-length error the harness can compact and retry, whereas an unpaired
    ///         tool result is refused outright and, because the strict fallback stays
    ///         engaged for the rest of the run, re-derives the same malformed history on
    ///         every subsequent turn.
    ///     </para>
    ///     <para>
    ///         If the entire history is tool results there is no such message and nothing
    ///         legal exists; the newest one is kept, because the input was already
    ///         malformed and an empty history would be worse. That is the only input for
    ///         which the orphan guarantee cannot hold, it is not reachable from session
    ///         history (which is append-only from a user message, and whose compacted view
    ///         starts with a summary or a user turn), and it is graded by its own test in
    ///         <c>CompactionPolicyInvariantTests</c> rather than left unmentioned.
    ///     </para>
    /// </summary>
    private static int RewindToMessageBoundary(
        IReadOnlyList<AgentMessage> messages,
        int tailStart,
        int tailLength)
    {
        for (int i = Math.Min(tailStart, tailLength - 1); i >= 0; i--)
        {
            if (messages[i] is not ToolResultMessage)
            {
                return i;
            }
        }

        return tailLength - 1;
    }
}
