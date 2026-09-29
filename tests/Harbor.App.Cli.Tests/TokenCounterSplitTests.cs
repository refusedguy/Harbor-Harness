using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #651 — the status line's <c>↑</c> counter and the bill were one number.
///
///     <para>
///         <see cref="Usage.InputTokens" /> is the size of ONE request's whole
///         input: the provider read the system prompt and the entire history
///         again, so folding it turn after turn makes the counter
///         <c>N × context</c>. The reported run — six messages, "61.6k↑ 196↓" —
///         was six full requests, each carrying the history and the system
///         prompt once more. Nothing in the prompt grew; the counter grew anyway.
///     </para>
///     <para>
///         The sum is not the bug: without a cache the provider bills the whole
///         input of every request, so the bill is genuinely the sum, and hiding
///         it means lying about money. The bug is that ONE figure was doing both
///         jobs — what we PAID and what the context OCCUPIES. These tests pin
///         the split: the cell a person reads follows the occupied context, the
///         cost cell keeps following the core's priced total over the sum.
///     </para>
///     <para>
///         Competing harnesses (opencode, codex, claude code, crush) all report
///         the increment or the current occupancy, never the N-fold re-read sum.
///     </para>
/// </summary>
public class TokenCounterSplitTests
{
    /// <summary>Turns in the reported session.</summary>
    private const int Turns = 6;

    /// <summary>
    ///     One request's prompt tokens — the context this session occupies, and
    ///     what each of its turns re-sent in full.
    /// </summary>
    private const int RequestInput = 10_270;

    /// <summary>
    ///     One request's generated tokens. Output is never re-read by the next
    ///     request, so the total of these is a real total, not an artefact.
    /// </summary>
    private const int RequestOutput = 33;

    /// <summary>
    ///     $3/M in, $15/M out — the pair the deleted UI constants used, so the
    ///     core prices this session at the "$0.1878" the report quotes for
    ///     61.6k in / 196 out. Only the core is allowed to multiply by these
    ///     (see <c>CostPricedInCoreRules</c>); here they are the core's input,
    ///     folded through its own <see cref="Pricing.CalculateCost" />.
    /// </summary>
    private static readonly Pricing Rates = new(3m, 15m);

    private static readonly Usage PerRequest = new(RequestInput, RequestOutput);

    [Test]
    public async Task SixTurns_TheCounter_ShowsWhatTheContextOccupies_NotSixRequests()
    {
        var store = new UiStore();
        for (int turn = 1; turn <= Turns; turn++)
        {
            RequestAccepted(store, turn);
            TotalsPublished(store, CoreTotalsAfter(turn));
        }

        string footer = Footer(store.State);

        // One request's prompt tokens — the same figure on turn 1 and on turn 6.
        await Assert.That(footer).Contains("10.3K↑ 198↓");

        // 61.6k is what SIX full requests cost. It is the bill, not the context,
        // and no person asking "how full is my window" wanted it.
        await Assert.That(footer).DoesNotContain("61.6K");
    }

    [Test]
    public async Task TheSumSurvives_TheBillIsStillPricedFromIt()
    {
        var store = new UiStore();
        for (int turn = 1; turn <= Turns; turn++)
        {
            RequestAccepted(store, turn);
            TotalsPublished(store, CoreTotalsAfter(turn));
        }

        // Both numbers exist at once and they are different: the state keeps the
        // paid total (what the core priced) next to the occupied context (what
        // the cell shows). Conflating them again would make these equal.
        await Assert.That(store.State.Chat.Cost.TokensIn).IsEqualTo(61_620);

        // …and the money is the core's, priced over the sum — not over one
        // request. #653's number, unchanged by #651.
        await Assert.That(Footer(store.State)).Contains("$0.1878");
    }

    /// <summary>
    ///     The degradation, pinned: a session that has published totals but has
    ///     run no request in this process has no request size to report, and the
    ///     only figure it knows is the session total. A resumed session looks
    ///     like this for the moment before its first turn; showing the total
    ///     beats showing nothing, and it is the total's LAST appearance in the
    ///     live path (every turn that follows publishes a request).
    /// </summary>
    [Test]
    public async Task TotalsWithNoRequestSeen_StillReportWhatTheSessionKnows()
    {
        var store = new UiStore();
        TotalsPublished(store, CoreTotalsAfter(Turns));

        await Assert.That(Footer(store.State)).Contains("61.6K↑ 198↓");
    }

    // #594 deleted the sibling test this one was written beside. It read
    // src/Harbor.Ui.Framework.Reducers/AppReducer.cs and pinned the same
    // ContextTokens/StepFinishEvent pairing in the LEGACY generic reducer, on
    // the reasoning that "a second reducer that keeps summing the paid total is
    // invisible in every golden frame". That reducer was never on a read path:
    // no composition root fed its AppStore. The pin is not restated against
    // State/ChatAppReducer.cs because the test above already drives that reducer
    // through a real UiStore and asserts the rendered footer, which is the
    // stronger form of the same claim.

    /// <summary>
    ///     The totals the core publishes after <paramref name="turns" /> turns —
    ///     folded by the core's own <see cref="SessionMetadata.AddUsage" />, so
    ///     the fixture cannot drift from what a real run publishes.
    /// </summary>
    private static SessionMetadata CoreTotalsAfter(int turns)
    {
        var stats = SessionMetadata.Empty;
        for (int i = 0; i < turns; i++)
        {
            stats = stats.AddUsage(PerRequest, Rates);
        }

        return stats;
    }

    /// <summary>The provider accepted a request: one step finished, with its usage.</summary>
    private static void RequestAccepted(UiStore store, int index) =>
        _ = store.Dispatch(new ChatAppMsg.Agent(new MessageUpdateEvent(
            new StepFinishEvent(index, "stop", PerRequest),
            AssistantMessage.Empty("s1", "m"))));

    /// <summary>What the core published once the turn's tokens were folded in.</summary>
    private static void TotalsPublished(UiStore store, SessionMetadata stats) =>
        _ = store.Dispatch(new ChatAppMsg.Agent(new SessionStatsEvent("s1", stats)));

    /// <summary>The status line, as a person reads it.</summary>
    private static string Footer(UiState state) => StatusProjector.ProjectFooter(state);
}
