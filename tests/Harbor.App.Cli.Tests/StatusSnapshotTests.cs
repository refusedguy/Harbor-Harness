using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Repl;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #457: the CellForge status footer paints a snapshot built once per frame
///     by <see cref="ReplLifecycle.BuildStatusSnapshot" />. Both cost and scroll
///     used to be hardcoded there (<c>0m</c> / <c>0</c>), so the value the
///     reducer carried never reached the projection and the scroll segment stayed
///     pinned. These tests drive the real store (dispatch →
///     <see cref="ChatAppReducer" /> → snapshot → <see cref="StatusProjector" />)
///     so a regression in any of those four hops fails here.
/// </summary>
/// <remarks>
///     #653: the totals are dispatched as a <see cref="SessionStatsEvent" /> —
///     the core's own event, carrying the cost it priced from the model's rate
///     table. The reducer no longer turns tokens into money, so a step-finish
///     event is no longer the way to move this cell.
/// </remarks>
public class StatusSnapshotTests
{
    private const int Rows = 24;
    private const int Total = 40;

    /// <summary>What the core published for the first turn.</summary>
    private const decimal OneStepCost = 0.0105m;

    /// <summary>What the core published after a second, identical turn.</summary>
    private const decimal TwoStepCost = 0.0210m;

    [Test]
    public async Task CoreCost_ReachesFooterProjection()
    {
        var store = new UiStore();
        _ = store.Dispatch(new ChatAppMsg.ConfigureRuntime("kilo-auto/free", "kilocode", "code"));
        StatsPublished(store, OneStepCost, 1_000, 500);

        var snapshot = ReplLifecycle.BuildStatusSnapshot(null, store.State, 1_000, 500, Rows, Total);

        await Assert.That(snapshot.Chat.Cost.CostUsd).IsEqualTo(OneStepCost);
        await Assert.That(Footer(snapshot)).Contains("0.0105");
    }

    [Test]
    public async Task CostOnlyChange_RebuildsPastTheStaleSnapshot()
    {
        var store = new UiStore();
        var before = ReplLifecycle.BuildStatusSnapshot(null, store.State, 1_000, 500, Rows, Total);
        await Assert.That(before.Chat.Cost.CostUsd).IsEqualTo(0m);

        // Same chrome, same token counts, same geometry — the cost total is the
        // only input that moved. This is exactly what the old memo dropped: it
        // compared the snapshot's 0 against a hardcoded 0, never rebuilt, and the
        // footer kept painting the stale $0.0000 segment.
        StatsPublished(store, OneStepCost, 1_000, 500);
        var after = ReplLifecycle.BuildStatusSnapshot(before, store.State, 1_000, 500, Rows, Total);

        await Assert.That(ReferenceEquals(before, after)).IsFalse();
        await Assert.That(after.Chat.Cost.CostUsd).IsEqualTo(OneStepCost);
        await Assert.That(Footer(after)).Contains("0.0105");
    }

    [Test]
    public async Task SecondTurn_CostIsTheCoreTotal_NotTwiceTheFirst()
    {
        var store = new UiStore();
        StatsPublished(store, OneStepCost, 1_000, 500);
        var afterOne = ReplLifecycle.BuildStatusSnapshot(null, store.State, 1_000, 500, Rows, Total);
        await Assert.That(afterOne.Chat.Cost.CostUsd).IsEqualTo(OneStepCost);

        // The core publishes ABSOLUTE session totals, so the footer follows it to
        // $0.0210. A reducer that folded the incoming value as a delta would
        // print $0.0315 here — the number would be its own invention again.
        StatsPublished(store, TwoStepCost, 2_000, 1_000);
        var storeState = store.State;
        var second = ReplLifecycle.BuildStatusSnapshot(
            afterOne, storeState, storeState.Chat.Cost.TokensIn, storeState.Chat.Cost.TokensOut, Rows, Total);

        await Assert.That(ReferenceEquals(afterOne, second)).IsFalse();
        await Assert.That(second.Chat.Cost.CostUsd).IsEqualTo(TwoStepCost);
        await Assert.That(Footer(second)).Contains("0.0210");
    }

    /// <summary>
    ///     #653: a model with no published price shows a dash, not "$0.0000" —
    ///     zero there is a claim ("this was free") that a paid provider with an
    ///     unpriced catalogue entry would be lying about.
    /// </summary>
    [Test]
    public async Task UnknownPrice_RendersADash_NotZeroDollars()
    {
        var store = new UiStore();
        _ = store.Dispatch(new ChatAppMsg.Agent(new SessionStatsEvent(
            "s1",
            new SessionMetadata(0m, 61_600, 196, 0, 0, 0, 1, null, IsCostKnown: false))));

        var snapshot = ReplLifecycle.BuildStatusSnapshot(null, store.State, 61_600, 196, Rows, Total);

        await Assert.That(snapshot.Chat.Cost.CostKnown).IsFalse();
        await Assert.That(StatusBarText.CostCell(snapshot.Chat.Cost.CostUsd, snapshot.Chat.Cost.CostKnown))
            .IsEqualTo("—");
        await Assert.That(Footer(snapshot)).DoesNotContain("$0.0000");
        await Assert.That(Footer(snapshot)).Contains("—");
    }

    [Test]
    public async Task QuietFrame_ReusesSnapshotInstance()
    {
        var store = new UiStore();
        StatsPublished(store, OneStepCost, 1_000, 500);

        var first = ReplLifecycle.BuildStatusSnapshot(null, store.State, 1_000, 500, Rows, Total);
        var second = ReplLifecycle.BuildStatusSnapshot(first, store.State, 1_000, 500, Rows, Total);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    /// <summary>
    ///     #653, end to end through the real store: the LLM's own step-finish
    ///     event moves no money and no tokens. 61.6k in + 196 out is the exact
    ///     pair that used to print $0.1878 on a free model.
    /// </summary>
    [Test]
    public async Task StepFinishAlone_MovesNoCost_NoTokens()
    {
        var store = new UiStore();
        StepFinished(store, 61_600, 196);

        var snapshot = ReplLifecycle.BuildStatusSnapshot(null, store.State, 61_600, 196, Rows, Total);

        await Assert.That(snapshot.Chat.Cost.CostUsd).IsEqualTo(0m);
        await Assert.That(snapshot.Chat.Cost.TokensIn).IsEqualTo(0);
        await Assert.That(Footer(snapshot)).DoesNotContain("$0.1878");
    }

    [Test]
    public async Task ScrolledUp_ProjectsPositionInsideTheRange_AndRebuilds()
    {
        var store = new UiStore();
        Measure(store, Rows, Total);

        // Offset 0 = live tail ⇒ the bottom of the range, not its start.
        var atTail = ReplLifecycle.BuildStatusSnapshot(null, store.State, 0, 0, Rows, Total);
        await Assert.That(atTail.Ui.ScrollOffset).IsEqualTo(Total - Rows);
        await Assert.That(Footer(atTail)).Contains("scroll 100%");

        for (int i = 0; i < 4; i++)
        {
            _ = store.Dispatch(VirtualizedChatTimeline.LineUpMsg());
        }

        // 4 of 16 rows lifted off the tail ⇒ 12/16 through the range.
        var lifted = ReplLifecycle.BuildStatusSnapshot(atTail, store.State, 0, 0, Rows, Total);
        await Assert.That(ReferenceEquals(atTail, lifted)).IsFalse();
        await Assert.That(lifted.Ui.ScrollOffset).IsEqualTo(Total - Rows - 4);
        await Assert.That(Footer(lifted)).Contains("scroll 75%");
    }

    [Test]
    public async Task HistoryShorterThanViewport_ProjectsLive()
    {
        var store = new UiStore();
        Measure(store, Rows, Rows);

        var snapshot = ReplLifecycle.BuildStatusSnapshot(null, store.State, 0, 0, Rows, Rows);

        await Assert.That(snapshot.Ui.ScrollOffset).IsEqualTo(0);
        await Assert.That(Footer(snapshot)).Contains("live");
    }

    [Test]
    public async Task StoreChrome_FlowsIntoSnapshot()
    {
        var store = new UiStore();
        _ = store.Dispatch(new ChatAppMsg.ConfigureRuntime("kilo-auto/free", "kilocode", "code"));

        var snapshot = ReplLifecycle.BuildStatusSnapshot(null, store.State, 0, 0, Rows, Total);

        await Assert.That(snapshot.Chat.Provider).IsEqualTo("kilocode");
        await Assert.That(snapshot.Chat.Model).IsEqualTo("kilo-auto/free");
        await Assert.That(snapshot.Chat.AgentName).IsEqualTo("code");
        await Assert.That(Footer(snapshot)).Contains("agent code");
    }

    /// <summary>Geometry handshake the frame loop performs before reading scroll.</summary>
    private static void Measure(UiStore store, int rows, int total) =>
        Apply(store, new AppMsg.Viewport(rows), new AppMsg.HistoryMeasured(total));

    private static void StepFinished(UiStore store, int inputTokens, int outputTokens) =>
        Apply(store, new ChatAppMsg.Agent(new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", new Usage(inputTokens, outputTokens)),
            AssistantMessage.Empty("s", "m"))));

    /// <summary>What the core publishes after a turn: the session totals it folded.</summary>
    private static void StatsPublished(UiStore store, decimal cost, int tokensIn, int tokensOut) =>
        Apply(store, new ChatAppMsg.Agent(new SessionStatsEvent(
            "s",
            new SessionMetadata(cost, tokensIn, tokensOut, 0, 0, 0, 1, null))));

    private static void Apply(UiStore store, params AppMsg[] msgs)
    {
        foreach (var msg in msgs)
        {
            _ = store.Dispatch(msg);
        }
    }

    private static string Footer(UiState state) => StatusProjector.ProjectFooter(state);
}
