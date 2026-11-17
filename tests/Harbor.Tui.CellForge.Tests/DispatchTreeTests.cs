using System.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2359: declarative key context stack. Context override by depth, rebind and
/// unbind, capture from root to focus (global actions), bubble from focus to
/// root with default stop, and explicit no-action. The router is a primitive:
/// action ids stay opaque, so ChatKeyMap and LeaderKeyRouter keep their own
/// vocabularies.
/// </summary>
public class DispatchTreeTests
{
    private static EngineInput.KeyEvent Key(char c) => EngineInput.KeyEvent.Char(new Rune(c));

    private static (EngineInput.DispatchTree Tree, EngineInput.KeyViewNode Composer, EngineInput.KeyContext RootCtx, EngineInput.KeyContext ComposerCtx) TwoLevelTree()
    {
        var tree = new EngineInput.DispatchTree();
        var rootCtx = new EngineInput.KeyContext("root");
        rootCtx.Bind(EngineInput.KeyGesture.ForChar('g'), "root-action");
        tree.Root.AddContext(rootCtx);

        EngineInput.KeyViewNode composer = tree.AddChild("panel").AddChild(new EngineInput.KeyViewNode("composer"));
        var composerCtx = new EngineInput.KeyContext("composer");
        composerCtx.Bind(EngineInput.KeyGesture.ForChar('g'), "composer-action");
        composer.AddContext(composerCtx);
        tree.SetFocus(composer);
        return (tree, composer, rootCtx, composerCtx);
    }

    [Test]
    public async Task Resolve_DeepestContextOverridesShallower()
    {
        (EngineInput.DispatchTree tree, _, _, _) = TwoLevelTree();

        EngineInput.DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Verdict).IsEqualTo(EngineInput.KeyDispatchVerdict.Action);
        await Assert.That(result.ActionId).IsEqualTo("composer-action");
    }

    [Test]
    public async Task Unbind_DeepContext_RestoresShallowerBinding()
    {
        (EngineInput.DispatchTree tree, _, _, EngineInput.KeyContext composerCtx) = TwoLevelTree();

        await Assert.That(composerCtx.Unbind(EngineInput.KeyGesture.ForChar('g'))).IsTrue();
        EngineInput.DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.ActionId).IsEqualTo("root-action");
    }

    [Test]
    public async Task Rebind_LastWinsInsideOneContext()
    {
        var tree = new EngineInput.DispatchTree();
        var rootCtx = new EngineInput.KeyContext("root");
        rootCtx.Bind(EngineInput.KeyGesture.ForChar('g'), "first");
        rootCtx.Bind(EngineInput.KeyGesture.ForChar('g'), "second");
        tree.Root.AddContext(rootCtx);

        EngineInput.DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.ActionId).IsEqualTo("second");
        await Assert.That(rootCtx.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Capture_WalksRootToFocus()
    {
        (EngineInput.DispatchTree tree, EngineInput.KeyViewNode composer, _, _) = TwoLevelTree();
        List<string> order = [];
        tree.Root.OnCapture((_, id) => { order.Add($"root:{id}"); return false; });
        tree.Root.Children[0].OnCapture((_, id) => { order.Add($"panel:{id}"); return false; });
        composer.OnCapture((_, id) => { order.Add($"composer:{id}"); return true; });

        EngineInput.DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Phase).IsEqualTo(EngineInput.KeyRoutingPhase.Capture);
        await Assert.That(order.Count).IsEqualTo(3);
        await Assert.That(order[0]).IsEqualTo("root:composer-action");
        await Assert.That(order[1]).IsEqualTo("panel:composer-action");
        await Assert.That(order[2]).IsEqualTo("composer:composer-action");
    }

    [Test]
    public async Task Capture_RootHandler_WinsGlobalActionBeforeFocus()
    {
        var tree = new EngineInput.DispatchTree();
        var rootCtx = new EngineInput.KeyContext("root");
        EngineInput.KeyGesture abort = EngineInput.KeyGesture.ForChar('c', EngineInput.KeyModifiers.Ctrl);
        rootCtx.Bind(abort, "abort");
        tree.Root.AddContext(rootCtx);

        int rootCapture = 0;
        int childBubble = 0;
        tree.Root.OnCapture((_, id) => { rootCapture++; return id == "abort"; });
        EngineInput.KeyViewNode composer = tree.AddChild("panel").AddChild(new EngineInput.KeyViewNode("composer"));
        composer.OnBubble((_, _) => { childBubble++; return true; });
        tree.SetFocus(composer);

        EngineInput.DispatchResult result = tree.Dispatch(EngineInput.KeyEvent.Char(new Rune('c'), EngineInput.KeyModifiers.Ctrl));

        await Assert.That(rootCapture).IsEqualTo(1);
        await Assert.That(childBubble).IsEqualTo(0);
        await Assert.That(result.Verdict).IsEqualTo(EngineInput.KeyDispatchVerdict.Action);
        await Assert.That(result.ActionId).IsEqualTo("abort");
        await Assert.That(result.Phase).IsEqualTo(EngineInput.KeyRoutingPhase.Capture);
    }

    [Test]
    public async Task Bubble_StopsAtFirstHandler()
    {
        (EngineInput.DispatchTree tree, EngineInput.KeyViewNode composer, _, _) = TwoLevelTree();
        int child = 0;
        int root = 0;
        tree.Root.OnBubble((_, _) => { root++; return true; });
        composer.OnBubble((_, _) => { child++; return true; });

        EngineInput.DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Phase).IsEqualTo(EngineInput.KeyRoutingPhase.Bubble);
        await Assert.That(child).IsEqualTo(1);
        await Assert.That(root).IsEqualTo(0);
    }

    [Test]
    public async Task NoAction_ConsumesKey_WithoutCaptureOrBubble()
    {
        var tree = new EngineInput.DispatchTree();
        var modalCtx = new EngineInput.KeyContext("modal");
        modalCtx.BindNoAction(EngineInput.KeyGesture.ForChar('g'));
        EngineInput.KeyViewNode modal = tree.AddChild("modal");
        modal.AddContext(modalCtx);
        tree.SetFocus(modal);

        int capture = 0;
        int bubble = 0;
        tree.Root.OnCapture((_, _) => { capture++; return true; });
        modal.OnBubble((_, _) => { bubble++; return true; });

        EngineInput.DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Verdict).IsEqualTo(EngineInput.KeyDispatchVerdict.NoAction);
        await Assert.That(capture).IsEqualTo(0);
        await Assert.That(bubble).IsEqualTo(0);
    }

    [Test]
    public async Task Dispatch_NoBinding_IsUnhandled()
    {
        var tree = new EngineInput.DispatchTree();

        EngineInput.DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Verdict).IsEqualTo(EngineInput.KeyDispatchVerdict.Unhandled);
        await Assert.That(result.ActionId).IsNull();
        await Assert.That(result.Phase).IsEqualTo(EngineInput.KeyRoutingPhase.None);
    }

    [Test]
    public async Task SetFocus_ForeignNode_Throws()
    {
        var tree = new EngineInput.DispatchTree();
        var foreign = new EngineInput.KeyViewNode("foreign");

        await Assert.That(() => tree.SetFocus(foreign)).Throws<ArgumentException>();
    }
}
