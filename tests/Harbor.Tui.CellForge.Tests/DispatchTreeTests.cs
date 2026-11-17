using Harbor.Tui.CellForge.Input;

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
    private static KeyEvent Key(char c) => KeyEvent.Char(new Rune(c));

    private static (DispatchTree Tree, KeyViewNode Composer, KeyContext RootCtx, KeyContext ComposerCtx) TwoLevelTree()
    {
        var tree = new DispatchTree();
        var rootCtx = new KeyContext("root");
        rootCtx.Bind(KeyGesture.ForChar('g'), "root-action");
        tree.Root.AddContext(rootCtx);

        KeyViewNode composer = tree.AddChild("panel").AddChild(new KeyViewNode("composer"));
        var composerCtx = new KeyContext("composer");
        composerCtx.Bind(KeyGesture.ForChar('g'), "composer-action");
        composer.AddContext(composerCtx);
        tree.SetFocus(composer);
        return (tree, composer, rootCtx, composerCtx);
    }

    [Test]
    public async Task Resolve_DeepestContextOverridesShallower()
    {
        (DispatchTree tree, _, _, _) = TwoLevelTree();

        DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Verdict).IsEqualTo(KeyDispatchVerdict.Action);
        await Assert.That(result.ActionId).IsEqualTo("composer-action");
    }

    [Test]
    public async Task Unbind_DeepContext_RestoresShallowerBinding()
    {
        (DispatchTree tree, _, _, KeyContext composerCtx) = TwoLevelTree();

        await Assert.That(composerCtx.Unbind(KeyGesture.ForChar('g'))).IsTrue();
        DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.ActionId).IsEqualTo("root-action");
    }

    [Test]
    public async Task Rebind_LastWinsInsideOneContext()
    {
        var tree = new DispatchTree();
        var rootCtx = new KeyContext("root");
        rootCtx.Bind(KeyGesture.ForChar('g'), "first");
        rootCtx.Bind(KeyGesture.ForChar('g'), "second");
        tree.Root.AddContext(rootCtx);

        DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.ActionId).IsEqualTo("second");
        await Assert.That(rootCtx.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Capture_WalksRootToFocus()
    {
        (DispatchTree tree, KeyViewNode composer, _, _) = TwoLevelTree();
        List<string> order = [];
        tree.Root.OnCapture((_, id) => { order.Add($"root:{id}"); return false; });
        tree.Root.Children[0].OnCapture((_, id) => { order.Add($"panel:{id}"); return false; });
        composer.OnCapture((_, id) => { order.Add($"composer:{id}"); return true; });

        DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Phase).IsEqualTo(KeyRoutingPhase.Capture);
        await Assert.That(order.Count).IsEqualTo(3);
        await Assert.That(order[0]).IsEqualTo("root:composer-action");
        await Assert.That(order[1]).IsEqualTo("panel:composer-action");
        await Assert.That(order[2]).IsEqualTo("composer:composer-action");
    }

    [Test]
    public async Task Capture_RootHandler_WinsGlobalActionBeforeFocus()
    {
        var tree = new DispatchTree();
        var rootCtx = new KeyContext("root");
        var abort = KeyGesture.ForChar('c', KeyModifiers.Ctrl);
        rootCtx.Bind(abort, "abort");
        tree.Root.AddContext(rootCtx);

        int rootCapture = 0;
        int childBubble = 0;
        tree.Root.OnCapture((_, id) => { rootCapture++; return id == "abort"; });
        KeyViewNode composer = tree.AddChild("panel").AddChild(new KeyViewNode("composer"));
        composer.OnBubble((_, _) => { childBubble++; return true; });
        tree.SetFocus(composer);

        DispatchResult result = tree.Dispatch(KeyEvent.Char(new Rune('c'), KeyModifiers.Ctrl));

        await Assert.That(rootCapture).IsEqualTo(1);
        await Assert.That(childBubble).IsEqualTo(0);
        await Assert.That(result.Verdict).IsEqualTo(KeyDispatchVerdict.Action);
        await Assert.That(result.ActionId).IsEqualTo("abort");
        await Assert.That(result.Phase).IsEqualTo(KeyRoutingPhase.Capture);
    }

    [Test]
    public async Task Bubble_StopsAtFirstHandler()
    {
        (DispatchTree tree, KeyViewNode composer, _, _) = TwoLevelTree();
        int child = 0;
        int root = 0;
        tree.Root.OnBubble((_, _) => { root++; return true; });
        composer.OnBubble((_, _) => { child++; return true; });

        DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Phase).IsEqualTo(KeyRoutingPhase.Bubble);
        await Assert.That(child).IsEqualTo(1);
        await Assert.That(root).IsEqualTo(0);
    }

    [Test]
    public async Task NoAction_ConsumesKey_WithoutCaptureOrBubble()
    {
        var tree = new DispatchTree();
        var modalCtx = new KeyContext("modal");
        modalCtx.BindNoAction(KeyGesture.ForChar('g'));
        KeyViewNode modal = tree.AddChild("modal");
        modal.AddContext(modalCtx);
        tree.SetFocus(modal);

        int capture = 0;
        int bubble = 0;
        tree.Root.OnCapture((_, _) => { capture++; return true; });
        modal.OnBubble((_, _) => { bubble++; return true; });

        DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Verdict).IsEqualTo(KeyDispatchVerdict.NoAction);
        await Assert.That(capture).IsEqualTo(0);
        await Assert.That(bubble).IsEqualTo(0);
    }

    [Test]
    public async Task Dispatch_NoBinding_IsUnhandled()
    {
        var tree = new DispatchTree();

        DispatchResult result = tree.Dispatch(Key('g'));

        await Assert.That(result.Verdict).IsEqualTo(KeyDispatchVerdict.Unhandled);
        await Assert.That(result.ActionId).IsNull();
        await Assert.That(result.Phase).IsEqualTo(KeyRoutingPhase.None);
    }

    [Test]
    public async Task SetFocus_ForeignNode_Throws()
    {
        var tree = new DispatchTree();
        var foreign = new KeyViewNode("foreign");

        await Assert.That(() => tree.SetFocus(foreign)).Throws<ArgumentException>();
    }
}
