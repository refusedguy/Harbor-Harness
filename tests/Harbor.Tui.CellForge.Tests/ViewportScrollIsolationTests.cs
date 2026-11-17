using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #2357: "one handle = one logical viewport". The scroll triple (offset,
///     visible rows, total rows) is owned by one named entry in
///     <see cref="TerminalUiState.ViewportScrolls"/>. Two virtualized regions
///     address their own entry, so neither shares an offset nor overwrites the
///     other's scrollbar thumb window (its hit range).
/// </summary>
public class ViewportScrollIsolationTests
{
    private const string Files = "files";

    private static UiState Seeded()
    {
        var state = new UiState();
        return state with
        {
            Ui = state.Ui
                .SetViewportLines(TerminalUiState.DefaultViewport, 10)
                .SetTotalLines(TerminalUiState.DefaultViewport, 100)
                .SetScroll(TerminalUiState.DefaultViewport, 60)
                .SetViewportLines(Files, 5)
                .SetTotalLines(Files, 20)
                .SetScroll(Files, 12)
        };
    }

    private static (int Start, int Length, int End) HitRange((int Offset, int Viewport, int Total) s, int track = 10)
        => Scrollbar.ThumbGeometry(s.Total, s.Offset, s.Viewport, track);

    [Test]
    public async Task GeometryMessages_AddressTheirOwnViewport()
    {
        var state = Seeded();
        var chatBefore = state.Ui.ScrollOf(TerminalUiState.DefaultViewport);

        state = AppReducer.Update(state, new AppMsg.Viewport(8, Files)).State;
        state = AppReducer.Update(state, new AppMsg.HistoryMeasured(40, Files)).State;
        state = AppReducer.Update(state, new AppMsg.ScrollClamp(7, Files)).State;

        // Files moved on its own handle: 12 -> 7, viewport 5 -> 8, total 20 -> 40.
        var files = state.Ui.ScrollOf(Files);
        await Assert.That(files.Offset).IsEqualTo(7);
        await Assert.That(files.Viewport).IsEqualTo(8);
        await Assert.That(files.Total).IsEqualTo(40);

        // Chat owns a different handle and is unchanged by every files message.
        await Assert.That(state.Ui.ScrollOf(TerminalUiState.DefaultViewport)).IsEqualTo(chatBefore);
        await Assert.That(state.Ui.ScrollOffset).IsEqualTo(60);
        await Assert.That(state.Ui.ViewportLines).IsEqualTo(10);
        await Assert.That(state.Ui.TotalLines).IsEqualTo(100);
    }

    [Test]
    public async Task ResetToTail_NamedViewport_LeavesOtherViewportOffsetAlone()
    {
        var state = Seeded();

        state = AppReducer.Update(state, VirtualizedChatTimeline.ResetToTailMsg(Files)).State;

        await Assert.That(state.Ui.ScrollOf(Files).Offset).IsEqualTo(0);
        await Assert.That(state.Ui.ScrollOffset).IsEqualTo(60);
    }

    [Test]
    public async Task TwoViewports_DoNotShareThumbHitRange()
    {
        var state = Seeded();
        var chatHit = HitRange(state.Ui.ScrollOf(TerminalUiState.DefaultViewport));
        var filesHit = HitRange(state.Ui.ScrollOf(Files));
        await Assert.That(chatHit).IsNotEqualTo(filesHit);

        // Reposition chat; files' hit window must be untouched.
        state = AppReducer.Update(state, new AppMsg.ScrollClamp(20, TerminalUiState.DefaultViewport)).State;
        await Assert.That(HitRange(state.Ui.ScrollOf(Files))).IsEqualTo(filesHit);
        await Assert.That(HitRange(state.Ui.ScrollOf(TerminalUiState.DefaultViewport))).IsNotEqualTo(chatHit);
    }

    [Test]
    public async Task MeasureMsgs_AndResetToTailMsg_CarryViewportName()
    {
        var timeline = new VirtualizedChatTimeline();
        AppMsg[] msgs = timeline.MeasureMsgs(24, Files);

        await Assert.That(((AppMsg.Viewport)msgs[0]).ViewportName).IsEqualTo(Files);
        await Assert.That(((AppMsg.HistoryMeasured)msgs[1]).ViewportName).IsEqualTo(Files);
        await Assert.That(((AppMsg.ScrollClamp)msgs[2]).ViewportName).IsEqualTo(Files);
        await Assert.That(((AppMsg.ScrollResetToTail)VirtualizedChatTimeline.ResetToTailMsg(Files)).ViewportName).IsEqualTo(Files);

        // Omitted name still means the chat handle (behavior preservation).
        await Assert.That(((AppMsg.Viewport)timeline.MeasureMsgs(24)[0]).ViewportName)
            .IsEqualTo(TerminalUiState.DefaultViewport);
    }
}
