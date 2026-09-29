using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #651 — the CellForge footer half of the counter/bill split (the TEA
///     projection half is pinned by <c>TokenCounterSplitTests</c> in
///     Harbor.App.Cli.Tests).
///     <para>
///         The ctx bar next to this cell was moved onto the request the provider
///         just accepted in #630, and it was right: the window fills with the
///         prompt, not with the sum of every prompt ever sent. The token cell
///         beside it still printed that sum, so the row contradicted itself —
///         a bar at 8% of the window beside "61.6k↑".
///     </para>
///     <para>
///         The sum is not a bug in itself (it is what an uncached provider
///         bills); it is a bug as the answer to "how full is my context". The
///         cell now reports the occupied context, and the cost cell keeps the
///         core's total.
///     </para>
/// </summary>
public class StatusTokenOccupancyTests
{
    /// <summary>One request's prompt tokens — what the context occupies.</summary>
    private const int RequestInput = 10_270;

    private const int RequestOutput = 33;

    [Test]
    public async Task TokenCell_ReportsTheRequestJustAccepted_NotTheSessionTotal()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = "m" };
        using var bridge = new ChatScreenBridge(bus, panel, status);

        // Six turns, each re-sending the whole context: the provider reports
        // 10 270 prompt tokens every time, and the core accumulates 61 620.
        for (int turn = 0; turn < 6; turn++)
        {
            await bus.PublishAsync(new MessageUpdateEvent(
                new StepFinishEvent(turn, "stop", new Usage(RequestInput, RequestOutput)),
                AssistantMessage.Empty("s1", "m")));
        }

        await bus.PublishAsync(new SessionStatsEvent(
            "s1",
            new SessionMetadata(0.1878m, 6 * RequestInput, 6 * RequestOutput, 0, 0, 0, 6, null)));

        await Assert.That(status.Tokens).IsEqualTo("10.3k↑ 198↓");
        await Assert.That(status.Cost).IsEqualTo("$0.1878");
    }

    /// <summary>
    ///     The degradation, pinned: a session whose totals arrived without a
    ///     request in this process has no request size, so the cell falls back
    ///     to the total it was given rather than claiming an empty context.
    /// </summary>
    [Test]
    public async Task TotalsWithoutARequest_ReportTheTotalTheyWereGiven()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = "m" };
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new SessionStatsEvent(
            "s1",
            new SessionMetadata(0.1878m, 6 * RequestInput, 6 * RequestOutput, 0, 0, 0, 6, null)));

        await Assert.That(status.Tokens).IsEqualTo("61.6k↑ 198↓");
    }
}
