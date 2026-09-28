using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// Track C contour: diff navigation travels through the TEA store instead of
/// executing the diff view-model commands directly. With
/// <see cref="ApprovalGateRouter.Store"/> set the view-model index is
/// untouched and the store scroll moves; without it the legacy VM-command
/// path still steps the selection.
/// </summary>
public class ApprovalGateDiffNavStoreTests
{
    private static (ApprovalGateRouter Router, ChatTimelinePanel Panel, UiStore Store) MakeRouterWithStore()
    {
        var panel = new ChatTimelinePanel("chat", 40, 6);
        var store = new UiStore();
        store.Dispatch(new AppMsg.Viewport(10));
        store.Dispatch(new AppMsg.HistoryMeasured(100));
        var router = new ApprovalGateRouter(panel, new StatusViewModel()) { Store = store };
        return (router, panel, store);
    }

    private static DiffPreviewViewModel TwoDiffs()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff("diff one");
        vm.AddDiff("diff two");
        return vm;
    }

    [Test]
    public async Task StoreSet_NextStep_ScrollsStore_LeavesViewModel()
    {
        var (router, _, store) = MakeRouterWithStore();
        var vm = TwoDiffs();
        _ = store.Dispatch(new AppMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up)));

        router.RouteDiffNavigation(vm, ChatAction.ScrollDownLine);

        await Assert.That(vm.CurrentIndex).IsEqualTo(0);
        await Assert.That(store.State.Ui.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task StoreSet_PrevStep_ScrollsStoreUp()
    {
        var (router, _, store) = MakeRouterWithStore();
        var vm = TwoDiffs();
        _ = store.Dispatch(new AppMsg.KeyInput(ChatAction.ScrollDownLine, new UiKey(UiKeyCode.Down)));
        _ = store.Dispatch(new AppMsg.KeyInput(ChatAction.ScrollDownLine, new UiKey(UiKeyCode.Down)));

        router.RouteDiffNavigation(vm, ChatAction.ScrollUpLine);

        await Assert.That(vm.CurrentIndex).IsEqualTo(0);
        await Assert.That(store.State.Ui.ScrollOffset).IsEqualTo(1);
    }

    [Test]
    public async Task StoreSet_OtherAction_IsIgnored()
    {
        var (router, _, store) = MakeRouterWithStore();
        var vm = TwoDiffs();

        router.RouteDiffNavigation(vm, ChatAction.Submit);

        await Assert.That(vm.CurrentIndex).IsEqualTo(0);
        await Assert.That(store.State.Ui.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task StoreNull_LegacyPath_StepsViewModel()
    {
        var panel = new ChatTimelinePanel("chat", 40, 6);
        var router = new ApprovalGateRouter(panel, new StatusViewModel());
        var vm = TwoDiffs();

        router.RouteDiffNavigation(vm, ChatAction.ScrollDownLine);
        await Assert.That(vm.CurrentIndex).IsEqualTo(1);

        router.RouteDiffNavigation(vm, ChatAction.ScrollUpLine);
        await Assert.That(vm.CurrentIndex).IsEqualTo(0);
    }

    [Test]
    public async Task BridgeStorePassthrough_StepsThroughStore()
    {
        var panel = new ChatTimelinePanel("chat", 40, 6);
        using var bridge = new ChatScreenBridge(new InMemoryEventBus(), panel, new StatusViewModel(), autoSubscribe: false);
        var store = new UiStore();
        store.Dispatch(new AppMsg.Viewport(10));
        store.Dispatch(new AppMsg.HistoryMeasured(100));
        _ = store.Dispatch(new AppMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up)));
        bridge.Store = store;
        var vm = TwoDiffs();

        bridge.RouteDiffNavigation(vm, ChatAction.ScrollDownLine);

        await Assert.That(vm.CurrentIndex).IsEqualTo(0);
        await Assert.That(store.State.Ui.ScrollOffset).IsEqualTo(0);
    }
}
