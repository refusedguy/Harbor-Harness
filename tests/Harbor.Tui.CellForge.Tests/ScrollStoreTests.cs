using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-B-006 + CF-C-002/C-003: scroll goes through the store. PageUp/PageDown,
/// arrows, Home/End and mouse-wheel ticks are <see cref="UiMsg"/> values
/// (verified names: <c>KeyInput</c> + <c>ScrollResetToTail</c> + <c>ScrollClamp</c>,
/// geometry via <c>Viewport</c> + <c>HistoryMeasured</c>) dispatched to a real
/// <see cref="UiStore"/>; <see cref="VirtualizedChatTimeline"/> mirrors the
/// snapshot back via <c>ApplyStoreState</c> (tail-follow derived from
/// <c>ScrollOffset == 0</c>); resize re-ports geometry via <c>MeasureMsgs</c>.
/// </summary>
public class ScrollStoreTests
{
    private sealed class StoreBlock : IChatBlock
    {
        public StoreBlock(string text) => Text = text;
        public string Text { get; }
        public string Kind => "fixed";
        public bool IsStreamContinuation => false;
        public int BudgetBytes => 64;
        public BlockMeasure Measure(int width) => BlockMeasure.Exact(1);
        public int CheapEstimate(int width) => 1;

        public void Paint(in BlockPaintContext ctx)
        {
            ctx.Buffer.SetText(ctx.Rect.X, ctx.Rect.Y, Text, CellStyle.Plain);
        }

        public string RawText() => Text;
    }

    private static UiStore SeededStore(int viewportLines, int totalLines)
    {
        var store = new UiStore();
        _ = store.Dispatch(new UiMsg.Viewport(viewportLines));
        _ = store.Dispatch(new UiMsg.HistoryMeasured(totalLines));
        return store;
    }

    private static VirtualizedChatTimeline Populated(int blocks, int width, int viewportH)
    {
        var timeline = new VirtualizedChatTimeline();
        for (int i = 0; i < blocks; i++)
        {
            timeline.Append(new StoreBlock($"line {i}"));
        }

        _ = timeline.PrepareFrame(width, viewportH);
        return timeline;
    }

    [Test]
    public async Task KeyFactories_ProduceVerifiedUiMsgShapes()
    {
        var pageUp = (UiMsg.KeyInput)VirtualizedChatTimeline.PageUpMsg();
        await Assert.That(pageUp.Action).IsEqualTo(ChatAction.ScrollUpPage);

        var pageDown = (UiMsg.KeyInput)VirtualizedChatTimeline.PageDownMsg();
        await Assert.That(pageDown.Action).IsEqualTo(ChatAction.ScrollDownPage);

        var top = (UiMsg.KeyInput)VirtualizedChatTimeline.ScrollTopMsg();
        await Assert.That(top.Action).IsEqualTo(ChatAction.ScrollTop);

        var bottom = (UiMsg.KeyInput)VirtualizedChatTimeline.ScrollBottomMsg();
        await Assert.That(bottom.Action).IsEqualTo(ChatAction.ScrollBottom);

        await Assert.That(VirtualizedChatTimeline.ResetToTailMsg() is UiMsg.ScrollResetToTail).IsTrue();
    }

    [Test]
    public async Task PageDown_PageUp_ViaDispatch_MovesAndClampsOffset()
    {
        var store = SeededStore(viewportLines: 10, totalLines: 30); // max = 20
        _ = store.Dispatch(VirtualizedChatTimeline.ScrollTopMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20);

        _ = store.Dispatch(VirtualizedChatTimeline.PageDownMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(12); // 20 - (10 - 2)

        _ = store.Dispatch(VirtualizedChatTimeline.PageUpMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20); // 12 + 8, clamped

        _ = store.Dispatch(VirtualizedChatTimeline.PageUpMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20); // stays at the top
    }

    [Test]
    public async Task Wheel_ViaRouter_DispatchesLineScroll()
    {
        var store = SeededStore(viewportLines: 10, totalLines: 30);
        var target = new TimelineWheelTarget("timeline", msg => { _ = store.Dispatch(msg); });
        var router = new MouseRouter();
        router.Bind(target, new Rect(0, 0, 80, 24));

        router.Wheel(5, 5, 3); // one tick = one line message, magnitude stays host-side
        await Assert.That(store.State.ScrollOffset).IsEqualTo(1);

        router.Wheel(5, 5, -1);
        await Assert.That(store.State.ScrollOffset).IsEqualTo(0);

        router.Wheel(5, 5, -1);
        await Assert.That(store.State.ScrollOffset).IsEqualTo(0); // clamped at the tail

        var up = (UiMsg.KeyInput)MouseRouter.WheelToMessage(2);
        await Assert.That(up.Action).IsEqualTo(ChatAction.ScrollUpLine);

        var down = (UiMsg.KeyInput)MouseRouter.WheelToMessage(-1);
        await Assert.That(down.Action).IsEqualTo(ChatAction.ScrollDownLine);

        var none = (UiMsg.KeyInput)MouseRouter.WheelToMessage(0);
        await Assert.That(none.Action).IsEqualTo(ChatAction.None);
    }

    [Test]
    public async Task TailFollow_UnpinRepin_ViaStore()
    {
        var timeline = Populated(blocks: 20, width: 40, viewportH: 5);
        var store = SeededStore(viewportLines: 5, totalLines: 20);

        _ = timeline.ApplyStoreState(store.State, width: 40, viewportH: 5);
        await Assert.That(timeline.FollowTail).IsTrue();
        await Assert.That(timeline.ScrollY).IsEqualTo(15);

        _ = store.Dispatch(VirtualizedChatTimeline.PageUpMsg()); // offset 0 -> 3
        await Assert.That(store.State.ScrollOffset).IsEqualTo(3);

        _ = timeline.ApplyStoreState(store.State, width: 40, viewportH: 5);
        await Assert.That(timeline.FollowTail).IsFalse();
        await Assert.That(timeline.ScrollY).IsEqualTo(12);

        timeline.Append(new StoreBlock("new arrival"));
        _ = timeline.ApplyStoreState(store.State, width: 40, viewportH: 5);
        await Assert.That(timeline.FollowTail).IsFalse();
        await Assert.That(timeline.ScrollY).IsEqualTo(13); // tail moved on, view did not jump

        _ = store.Dispatch(VirtualizedChatTimeline.ResetToTailMsg());
        _ = timeline.ApplyStoreState(store.State, width: 40, viewportH: 5);
        await Assert.That(timeline.FollowTail).IsTrue();
        await Assert.That(timeline.ScrollY).IsEqualTo(16);
    }

    [Test]
    public async Task Clamp_Boundaries()
    {
        var store = SeededStore(viewportLines: 10, totalLines: 30); // max = 20

        _ = store.Dispatch(VirtualizedChatTimeline.ScrollBottomMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(0);

        _ = store.Dispatch(VirtualizedChatTimeline.LineDownMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(0); // clamped at the tail

        _ = store.Dispatch(VirtualizedChatTimeline.ScrollTopMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20);

        _ = store.Dispatch(VirtualizedChatTimeline.ScrollTopMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20);

        _ = store.Dispatch(VirtualizedChatTimeline.LineUpMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20); // clamped at the top

        _ = store.Dispatch(new UiMsg.ScrollClamp(5));
        await Assert.That(store.State.ScrollOffset).IsEqualTo(5);

        _ = store.Dispatch(new UiMsg.ScrollClamp(0));
        await Assert.That(store.State.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task Resize_Viewport_HistoryMeasured_ScrollClamp()
    {
        var store = SeededStore(viewportLines: 10, totalLines: 30);
        _ = store.Dispatch(VirtualizedChatTimeline.ScrollTopMsg());
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20);

        // The Viewport arm reports geometry only — a stale offset survives until
        // the host dispatches the trailing ScrollClamp (MeasureMsgs order).
        _ = store.Dispatch(new UiMsg.Viewport(25));
        await Assert.That(store.State.ViewportLines).IsEqualTo(25);
        await Assert.That(store.State.ScrollOffset).IsEqualTo(20);

        _ = store.Dispatch(new UiMsg.ScrollClamp(5));
        await Assert.That(store.State.ScrollOffset).IsEqualTo(5);

        var timeline = Populated(blocks: 30, width: 40, viewportH: 25);
        UiMsg[] msgs = timeline.MeasureMsgs(25);
        await Assert.That(msgs.Length).IsEqualTo(3);
        await Assert.That(((UiMsg.Viewport)msgs[0]).HistoryHeight).IsEqualTo(25);
        await Assert.That(((UiMsg.HistoryMeasured)msgs[1]).TotalLines).IsEqualTo(30);
        await Assert.That(((UiMsg.ScrollClamp)msgs[2]).MaxScroll).IsEqualTo(5);

        _ = timeline.ApplyStoreState(store.State, width: 40, viewportH: 25);
        await Assert.That(timeline.FollowTail).IsFalse();
        await Assert.That(timeline.ScrollY).IsEqualTo(0); // offset 5 = max -> top row
    }

    [Test]
    public async Task Roundtrip_Timeline_Store_Timeline()
    {
        var timeline = Populated(blocks: 30, width: 40, viewportH: 10);
        var store = new UiStore();
        foreach (UiMsg msg in timeline.MeasureMsgs(10))
        {
            _ = store.Dispatch(msg);
        }

        await Assert.That(store.State.ViewportLines).IsEqualTo(10);
        await Assert.That(store.State.TotalLines).IsEqualTo(30);

        _ = store.Dispatch(VirtualizedChatTimeline.ScrollTopMsg());
        _ = timeline.ApplyStoreState(store.State, width: 40, viewportH: 10);
        await Assert.That(timeline.FollowTail).IsFalse();
        await Assert.That(timeline.ScrollY).IsEqualTo(0);

        _ = store.Dispatch(VirtualizedChatTimeline.ResetToTailMsg());
        _ = timeline.ApplyStoreState(store.State, width: 40, viewportH: 10);
        await Assert.That(timeline.FollowTail).IsTrue();
        await Assert.That(timeline.ScrollY).IsEqualTo(20);
    }
}
