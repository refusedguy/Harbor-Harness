using System.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #3617: the REPL's manual modal-barrier gestures are declarative key
/// contexts. Each host-owned gesture is pinned to an OPAQUE action id, a
/// rebind re-seats the gesture, and an unmatched key is a TryMatch miss — the
/// host's barrier still swallows it. The context order is the barrier order,
/// so the priority is testable without running the REPL.
/// </summary>
public class ReplModalContextTests
{
    private static EngineInput.ReplModalKeyContexts NewContexts() => new();

    private static EngineInput.KeyEvent Ctrl(char c, EngineInput.KeyModifiers extra = EngineInput.KeyModifiers.None) =>
        EngineInput.KeyEvent.Char(new Rune(c), EngineInput.KeyModifiers.Ctrl | extra);

    [Test]
    public async Task Markup_Gestures_MapToPinnedOpaqueActions()
    {
        EngineInput.KeyContext ctx = NewContexts().Markup;
        EngineInput.KeyEvent save = Ctrl('s');
        EngineInput.KeyEvent saveShift = Ctrl('S', EngineInput.KeyModifiers.Shift);
        EngineInput.KeyEvent send = EngineInput.KeyEvent.Simple(EngineInput.KeyCode.Enter, EngineInput.KeyModifiers.Ctrl);

        await Assert.That(ctx.TryMatch(save, out var saveHit)).IsTrue();
        await Assert.That(saveHit.ActionId).IsEqualTo(EngineInput.ReplModalKeyContexts.MarkupSave);

        // Both case spellings of the legacy Ctrl+S check stay bound.
        await Assert.That(ctx.TryMatch(saveShift, out var shiftHit)).IsTrue();
        await Assert.That(shiftHit.ActionId).IsEqualTo(EngineInput.ReplModalKeyContexts.MarkupSave);

        await Assert.That(ctx.TryMatch(send, out var sendHit)).IsTrue();
        await Assert.That(sendHit.ActionId).IsEqualTo(EngineInput.ReplModalKeyContexts.MarkupSend);
    }

    [Test]
    public async Task Image_Gesture_MapsToPinnedOpaqueAction()
    {
        EngineInput.KeyContext ctx = NewContexts().Image;

        await Assert.That(ctx.TryMatch(EngineInput.KeyEvent.Char(new Rune('m')), out var hit)).IsTrue();
        await Assert.That(hit.ActionId).IsEqualTo(EngineInput.ReplModalKeyContexts.ImageAnnotate);

        // IsMarkupAnnotateKey required an unmodified rune; Ctrl+m is not it.
        await Assert.That(ctx.TryMatch(Ctrl('m'), out _)).IsFalse();
    }

    [Test]
    public async Task UnmatchedKey_IsATryMatchMiss_SoTheBarrierStillSwallows()
    {
        var contexts = NewContexts();

        await Assert.That(contexts.Markup.TryMatch(EngineInput.KeyEvent.Char(new Rune('x')), out _)).IsFalse();
        await Assert.That(contexts.Image.TryMatch(EngineInput.KeyEvent.Simple(EngineInput.KeyCode.Escape), out _)).IsFalse();

        // The other barriers own no host gesture: their keys keep flowing to
        // the modal's own handler (leader router / sheet / palette / ...).
        foreach (EngineInput.KeyContext ctx in new[]
                 {
                     contexts.Terminal, contexts.Sheet, contexts.Onboarding,
                     contexts.Palette, contexts.Setup,
                 })
        {
            await Assert.That(ctx.Count).IsZero();
            await Assert.That(ctx.TryMatch(EngineInput.KeyEvent.Char(new Rune('x')), out _)).IsFalse();
        }
    }

    [Test]
    public async Task Terminal_LeaderPress_IsNotClaimedByTheContext()
    {
        // #1491: Ctrl+x must still arm LeaderKeyRouter, never be swallowed as
        // a context binding. No binding means the router sees the raw key.
        EngineInput.KeyContext ctx = NewContexts().Terminal;

        await Assert.That(ctx.TryMatch(Ctrl('x'), out _)).IsFalse();
    }

    [Test]
    public async Task Rebind_ReseatsTheGesture_AndLastWins()
    {
        EngineInput.KeyContext ctx = NewContexts().Markup;
        EngineInput.KeyEvent save = Ctrl('s');

        ctx.Bind(EngineInput.KeyGesture.ForChar('s', EngineInput.KeyModifiers.Ctrl), "first");
        ctx.Bind(EngineInput.KeyGesture.ForChar('s', EngineInput.KeyModifiers.Ctrl), "second");

        await Assert.That(ctx.TryMatch(save, out var hit)).IsTrue();
        await Assert.That(hit.ActionId).IsEqualTo("second");
        await Assert.That(ctx.Count).IsEqualTo(3); // Ctrl+S re-seated twice, no duplicate slot; the other two bindings stay
    }

    [Test]
    public async Task Escape_NoAction_ClearsThePendingActionWithoutFiring()
    {
        var ctx = new EngineInput.KeyContext("setup");
        EngineInput.KeyEvent escape = EngineInput.KeyEvent.Simple(EngineInput.KeyCode.Escape);
        EngineInput.KeyGesture gesture = EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Escape);

        ctx.Bind(gesture, "close");
        await Assert.That(ctx.TryMatch(escape, out var bound)).IsTrue();
        await Assert.That(bound.ActionId).IsEqualTo("close");

        // Rebind Escape to the explicit no-action: the key is still consumed
        // (swallowed) but the pending action is gone.
        ctx.BindNoAction(gesture);
        await Assert.That(ctx.TryMatch(escape, out var cleared)).IsTrue();
        await Assert.That(cleared.IsNoAction).IsTrue();
        await Assert.That(cleared.ActionId).IsNull();
        await Assert.That(ctx.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Active_PicksTheTopmostModalInBarrierOrder()
    {
        var contexts = NewContexts();
        var all = new EngineInput.ReplModalState(true, true, true, true, true, true, true);

        await Assert.That(contexts.Active(all)?.Name).IsEqualTo("terminal");

        // Drop barriers one by one; the next one down takes over each time.
        await Assert.That(contexts.Active(all with { TerminalAttached = false })?.Name).IsEqualTo("sheet");
        await Assert.That(contexts.Active(all with { TerminalAttached = false, SheetOpen = false })?.Name).IsEqualTo("markup");
        await Assert.That(contexts.Active(all with { TerminalAttached = false, SheetOpen = false, MarkupVisible = false })?.Name).IsEqualTo("image");
        await Assert.That(contexts.Active(all with { TerminalAttached = false, SheetOpen = false, MarkupVisible = false, ImageVisible = false })?.Name).IsEqualTo("onboarding");
        await Assert.That(contexts.Active(all with { TerminalAttached = false, SheetOpen = false, MarkupVisible = false, ImageVisible = false, OnboardingActive = false })?.Name).IsEqualTo("palette");
        await Assert.That(contexts.Active(all with { TerminalAttached = false, SheetOpen = false, MarkupVisible = false, ImageVisible = false, OnboardingActive = false, PaletteVisible = false })?.Name).IsEqualTo("setup");
    }

    [Test]
    public async Task Active_NoModal_ReturnsNull()
    {
        var contexts = NewContexts();

        await Assert.That(contexts.Active(default)).IsNull();
    }
}
