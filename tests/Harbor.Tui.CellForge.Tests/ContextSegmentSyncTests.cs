using Harbor.Abstractions.Models;
using Harbor.Abstractions.Events;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #623 regression: the ctx segment must render <em>context-window occupancy</em>,
/// not session-cumulative spend.
/// <para>
/// <see cref="SessionMetadata.AddUsage" /> accumulates <c>TokensInput</c> across
/// the whole session, so <c>SessionStatsEvent.Metadata</c> is a running total.
/// Feeding that into the ctx bar made <c>ctx</c> read 30k → 60k → 90k → 120k →
/// 150k across turns while every request stayed 30k: on a 128k window the bar
/// pinned at 100% (Error accent) by turn 4 even though the payload never grew.
/// The system prompt is re-sent per call and does not accumulate —
/// <c>AgentMessage</c> is a closed union of User/Assistant/ToolResult, so it
/// cannot be leaking into history.
/// </para>
/// <para>
/// Occupancy is the prompt-token count of the request the provider just accepted,
/// carried by <c>StepFinishEvent.Usage.InputTokens</c>.
/// </para>
/// </summary>
public class ContextSegmentSyncTests
{
    /// <summary>Window from the issue report. Small enough that the cumulative
    /// total passes it by turn 4 and trips the Error accent.</summary>
    private const int Window = 128_000;

    /// <summary>Prompt tokens of the request sent on every turn: constant.</summary>
    private const int RequestTokens = 30_000;

    private static readonly ModelInfo Model = new(
        "hy3", "kilocode", "Kilocode Hy3", Window, 4096, false, false, true, Pricing.Unknown, "openai");

    /// <summary>
    /// The core regression. Five turns: the request is <see cref="RequestTokens" />
    /// every time while session-cumulative spend climbs to 150k — past the entire
    /// 128k window. Occupancy must stay pinned at the request size on every turn.
    /// </summary>
    [Test]
    public async Task CumulativeSpend_DoesNotDriveOccupancy_AcrossTurns()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new AgentStartEvent("s1", [], Model));

        for (int turn = 1; turn <= 5; turn++)
        {
            // Session-cumulative spend — the running total that used to be
            // rendered as occupancy (30k, 60k, 90k, 120k, 150k).
            int cumulative = RequestTokens * turn;
            await bus.PublishAsync(new SessionStatsEvent(
                "s1", new SessionMetadata(0.01m * turn, cumulative, 500, 0, 0, 0, turn, null)));

            // The request actually sent this turn: unchanged at 30k.
            await bus.PublishAsync(new MessageUpdateEvent(
                new StepFinishEvent(0, "stop", new Usage(RequestTokens, 500)),
                AssistantMessage.Empty("s1", "m")));

            await Assert.That(status.TryGetContextTokens(out var used)).IsTrue();
            await Assert.That(used).IsEqualTo(RequestTokens); // not the cumulative total
            await Assert.That(status.ContextWindow).IsEqualTo(Window);
        }

        // Cumulative spend (150k) is larger than the whole window (128k), so the
        // old reading saturated at 100%. The real occupancy is ~23% → Success.
        await Assert.That(CtxAccent(status)).IsEqualTo(StatusAccent.Success);
        await Assert.That(CtxText(status)).IsEqualTo("▰▱▱▱▱▱");

        // The token/cost segments still track cumulative spend — only ctx moved.
        await Assert.That(status.Tokens).IsEqualTo("150k↑ 500↓");
    }

    /// <summary>
    /// Ordering guard: a stats event alone must never light the segment. Without
    /// a request there is no occupancy number to report, so the bar stays dark
    /// even though the session has already recorded spend.
    /// </summary>
    [Test]
    public async Task SessionStatsAlone_LeavesOccupancyDark()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new AgentStartEvent("s1", [], Model));
        await bus.PublishAsync(new SessionStatsEvent(
            "s1", new SessionMetadata(0.01m, 150_000, 500, 0, 0, 0, 5, null)));

        await Assert.That(status.TryGetContextTokens(out _)).IsFalse();

        // Spend still surfaces as text — only the occupancy reading is withheld.
        await Assert.That(status.Tokens).IsEqualTo("150k↑ 500↓");
    }

    /// <summary>The ctx bar is the first segment when no model string is set.</summary>
    private static StatusSeg CtxSegment(StatusViewModel status)
    {
        var workspace = new StatusSeg[8];
        int n = status.BuildSegments(workspace);
        return n > 0 ? workspace[0] : default;
    }

    private static StatusAccent CtxAccent(StatusViewModel status) => CtxSegment(status).Accent;

    private static string CtxText(StatusViewModel status) => CtxSegment(status).Text;
}
