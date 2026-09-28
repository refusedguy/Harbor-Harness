using System.Text;
using System.Text;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Epic C: leader chords carry store meaning — msg-bound chords stage a
/// <see cref="AppMsg"/> the host dispatches, while legacy action-only chords
/// keep firing bare actions with no staged message.
/// </summary>
public class LeaderKeyRouterMsgTests
{
    private static KeyEvent Plain(char c) => KeyEvent.Char(new Rune(c));
    private static KeyEvent Leader() => KeyEvent.Char(new Rune('x'), KeyModifiers.Ctrl);

    [Test]
    public async Task MsgBoundChord_StagesPendingMsg_AndRunsSideEffect()
    {
        var router = new LeaderKeyRouter();
        int ran = 0;
        router.Bind('g', VirtualizedChatTimeline.ScrollTopMsg(), () => ran++);

        _ = router.HandleKey(Leader(), nowMs: 0);
        await Assert.That(router.TakePendingMsg()).IsNull();

        _ = router.HandleKey(Plain('g'), nowMs: 100);
        await Assert.That(ran).IsEqualTo(1);

        var staged = router.TakePendingMsg();
        await Assert.That(staged is AppMsg.KeyInput k && k.Action == ChatAction.ScrollTop).IsTrue();
        await Assert.That(router.TakePendingMsg()).IsNull();
    }

    [Test]
    public async Task ActionOnlyChord_StagesNoMsg()
    {
        var router = new LeaderKeyRouter();
        int ran = 0;
        router.Bind('p', () => ran++);

        _ = router.HandleKey(Leader(), nowMs: 0);
        _ = router.HandleKey(Plain('p'), nowMs: 100);

        await Assert.That(ran).IsEqualTo(1);
        await Assert.That(router.TakePendingMsg()).IsNull();
    }

    [Test]
    public async Task UnknownChord_StagesNoMsg()
    {
        var router = new LeaderKeyRouter();
        router.Bind('g', VirtualizedChatTimeline.ScrollTopMsg());

        _ = router.HandleKey(Leader(), nowMs: 0);
        _ = router.HandleKey(Plain('z'), nowMs: 100);

        await Assert.That(router.TakePendingMsg()).IsNull();
    }

    [Test]
    public async Task MsgRebind_Replaces()
    {
        var router = new LeaderKeyRouter();
        router.Bind('g', VirtualizedChatTimeline.ScrollTopMsg());
        router.Bind('g', VirtualizedChatTimeline.ScrollBottomMsg());

        _ = router.HandleKey(Leader(), nowMs: 0);
        _ = router.HandleKey(Plain('g'), nowMs: 100);

        var staged = router.TakePendingMsg();
        await Assert.That(staged is AppMsg.KeyInput k && k.Action == ChatAction.ScrollBottom).IsTrue();
    }
}
