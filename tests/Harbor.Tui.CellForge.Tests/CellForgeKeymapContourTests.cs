using System.Text;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Track C contour: raw terminal keys translate to <see cref="UiKey"/> via
/// <see cref="KeyEventMapper"/>, resolve to <see cref="ChatAction"/> through
/// the central <see cref="ChatKeyMap"/> (no shell-side branches), and the
/// resulting <see cref="UiMsg.KeyInput"/> drives the store — including wheel,
/// geometry-measure and scroll-anchor messages.
/// </summary>
public class CellForgeKeymapContourTests
{
    private static readonly ChatKeyMap Map = new();

    private static ChatAction Resolve(KeyEvent key) =>
        KeyEventMapper.ToUiKey(key) is { } uiKey ? Map.Resolve(uiKey) : ChatAction.None;

    [Test]
    public async Task Resolve_NavigationKeys_MapToScrollActions()
    {
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.Up))).IsEqualTo(ChatAction.ScrollUpLine);
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.Down))).IsEqualTo(ChatAction.ScrollDownLine);
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.PageUp))).IsEqualTo(ChatAction.ScrollUpPage);
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.PageDown))).IsEqualTo(ChatAction.ScrollDownPage);
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.Home))).IsEqualTo(ChatAction.ScrollTop);
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.End))).IsEqualTo(ChatAction.ScrollBottom);
    }

    [Test]
    public async Task Resolve_CommandKeys_MapToActions()
    {
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.Enter))).IsEqualTo(ChatAction.Submit);
        await Assert.That(Resolve(KeyEvent.Simple(KeyCode.Escape))).IsEqualTo(ChatAction.Quit);
        await Assert.That(Resolve(KeyEvent.Char(new Rune('c'), KeyModifiers.Ctrl))).IsEqualTo(ChatAction.Abort);
        await Assert.That(Resolve(KeyEvent.Char(new Rune('l'), KeyModifiers.Ctrl))).IsEqualTo(ChatAction.Clear);
        await Assert.That(Resolve(KeyEvent.Char(new Rune('j'), KeyModifiers.Ctrl))).IsEqualTo(ChatAction.JumpPalette);
        await Assert.That(Resolve(KeyEvent.Char(new Rune('?')))).IsEqualTo(ChatAction.HelpPanel);
        await Assert.That(Resolve(KeyEvent.Char(new Rune('x')))).IsEqualTo(ChatAction.Char);
    }

    [Test]
    public async Task ToUiKey_Release_CarriesNoKeyMeaning()
    {
        var release = new KeyEvent(KeyCode.Char, new Rune('x'), KeyModifiers.None, KeyEventType.Release, false);
        await Assert.That(KeyEventMapper.ToUiKey(release)).IsNull();
        await Assert.That(Resolve(release)).IsEqualTo(ChatAction.None);
    }

    [Test]
    public async Task KeyInputDispatch_ScrollsSeededStore()
    {
        var store = new UiStore();
        store.Dispatch(new UiMsg.Viewport(10));
        store.Dispatch(new UiMsg.HistoryMeasured(100));

        _ = store.Dispatch(VirtualizedChatTimeline.LineUpMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(1);

        _ = store.Dispatch(VirtualizedChatTimeline.PageUpMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(1 + 8);

        _ = store.Dispatch(VirtualizedChatTimeline.LineDownMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(8);

        _ = store.Dispatch(VirtualizedChatTimeline.ScrollTopMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(90);

        _ = store.Dispatch(VirtualizedChatTimeline.ScrollBottomMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task WheelMsg_MapsSignToLineScroll()
    {
        await Assert.That(VirtualizedChatTimeline.WheelMsg(1) is UiMsg.KeyInput k1 && k1.Action == ChatAction.ScrollUpLine).IsTrue();
        await Assert.That(VirtualizedChatTimeline.WheelMsg(-1) is UiMsg.KeyInput k2 && k2.Action == ChatAction.ScrollDownLine).IsTrue();
        await Assert.That(VirtualizedChatTimeline.WheelMsg(0) is UiMsg.KeyInput k3 && k3.Action == ChatAction.None).IsTrue();
    }

    [Test]
    public async Task MeasureMsgs_OrderIsViewportHistoryScrollClamp()
    {
        var timeline = new VirtualizedChatTimeline();
        var msgs = timeline.MeasureMsgs(24);

        await Assert.That(msgs.Length).IsEqualTo(3);
        await Assert.That(msgs[0] is UiMsg.Viewport v && v.HistoryHeight == 24).IsTrue();
        await Assert.That(msgs[1] is UiMsg.HistoryMeasured).IsTrue();
        await Assert.That(msgs[2] is UiMsg.ScrollClamp).IsTrue();
    }
}
