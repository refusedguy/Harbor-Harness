// TR4 (#1223, epic #1195): IFocusable + FocusStack + capture-to-bubble routing.
// Engine-vocabulary only (EngineInput-qualified — the unqualified KeyEvent
// aliases pin the Rendering vocabulary); chat untouched.

using System.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// TR4 (#1223, epic #1195): focus stack ownership + three-phase routing
/// (capture bindings -> preview tunnel -> bubble -> remainder).
/// </summary>
public sealed class FocusStackRoutingTests
{
    private sealed class Stub(string id, bool canFocus = true) : EngineInput.FocusableBase(id)
    {
        public override bool CanFocus => canFocus;

        public List<string> Calls { get; } = [];

        public bool PreviewResult { get; set; }

        public bool HandleResult { get; set; }

        public override bool PreviewKey(EngineInput.KeyEvent key)
        {
            Calls.Add($"preview:{Id}");
            return PreviewResult;
        }

        public override bool HandleKey(EngineInput.KeyEvent key)
        {
            Calls.Add($"handle:{Id}");
            return HandleResult;
        }
    }

    private static EngineInput.KeyEvent Esc() => EngineInput.KeyEvent.Simple(EngineInput.KeyCode.Escape);

    private static EngineInput.KeyEvent Char(char c, EngineInput.KeyModifiers mods = EngineInput.KeyModifiers.None) =>
        EngineInput.KeyEvent.Char(new Rune(c), mods);

    [Test]
    public async Task Push_FocusesTop_AndBlursPrevious()
    {
        var stack = new EngineInput.FocusStack();
        var a = new Stub("a");
        var b = new Stub("b");

        await Assert.That(stack.Push(a)).IsTrue();
        await Assert.That(a.IsFocused).IsTrue();
        _ = stack.Push(b);

        await Assert.That(a.IsFocused).IsFalse();
        await Assert.That(b.IsFocused).IsTrue();
        await Assert.That(stack.Current!.Id).IsEqualTo("b");
    }

    [Test]
    public async Task Pop_RestoresFocusBeneath()
    {
        var stack = new EngineInput.FocusStack();
        var a = new Stub("a");
        var b = new Stub("b");
        _ = stack.Push(a);
        _ = stack.Push(b);
        _ = stack.Pop();

        await Assert.That(b.IsFocused).IsFalse();
        await Assert.That(a.IsFocused).IsTrue();
        await Assert.That(stack.Current!.Id).IsEqualTo("a");
    }

    [Test]
    public async Task Push_Unfocusable_Refused_StackUntouched()
    {
        var stack = new EngineInput.FocusStack();
        var a = new Stub("a");
        _ = stack.Push(a);

        await Assert.That(stack.Push(new Stub("off", canFocus: false))).IsFalse();
        await Assert.That(stack.Count).IsEqualTo(1);
        await Assert.That(a.IsFocused).IsTrue();
    }

    [Test]
    public async Task TryFocusById_ReseatsOnTop()
    {
        var stack = new EngineInput.FocusStack();
        _ = stack.Push(new Stub("a"));
        _ = stack.Push(new Stub("b"));

        await Assert.That(stack.TryFocusById("a")).IsTrue();
        await Assert.That(stack.Current!.Id).IsEqualTo("a");
        await Assert.That(stack.TryFocusById("missing")).IsFalse();
    }

    [Test]
    public async Task Remove_Top_FocusesBeneath_Buried_NotifiesNobody()
    {
        var stack = new EngineInput.FocusStack();
        var a = new Stub("a");
        var b = new Stub("b");
        _ = stack.Push(a);
        _ = stack.Push(b);

        await Assert.That(stack.Remove("a")).IsTrue();
        await Assert.That(b.IsFocused).IsTrue();
        await Assert.That(a.IsFocused).IsFalse();
        await Assert.That(stack.Remove("missing")).IsFalse();
    }

    [Test]
    public async Task Capture_Preempts_FocusedHandler()
    {
        var stack = new EngineInput.FocusStack();
        var input = new Stub("input") { HandleResult = true };
        _ = stack.Push(input);
        var capture = new EngineInput.KeyBindings();
        capture.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Escape), "quit");

        var verdict = EngineInput.InputRouting.Route(Esc(), capture, stack, out string? action);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.CaptureHandled);
        await Assert.That(action).IsEqualTo("quit");
        await Assert.That(input.Calls).IsEmpty();
    }

    [Test]
    public async Task Preview_Tunnel_Runs_BottomUp()
    {
        var stack = new EngineInput.FocusStack();
        var bottom = new Stub("bottom");
        var top = new Stub("top") { PreviewResult = true };
        _ = stack.Push(bottom);
        _ = stack.Push(top);

        var verdict = EngineInput.InputRouting.Route(Esc(), null, stack, out _);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.PreviewHandled);
        await Assert.That(bottom.Calls).IsEquivalentTo(["preview:bottom"]);
        await Assert.That(top.Calls).IsEquivalentTo(["preview:top"]);
    }

    [Test]
    public async Task Bubble_Runs_TopDown_After_Silent_Preview()
    {
        var stack = new EngineInput.FocusStack();
        var bottom = new Stub("bottom");
        var top = new Stub("top") { HandleResult = true };
        _ = stack.Push(bottom);
        _ = stack.Push(top);

        var verdict = EngineInput.InputRouting.Route(Esc(), null, stack, out _);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.FocusHandled);
        await Assert.That(top.Calls).IsEquivalentTo(["preview:top", "handle:top"]);
        await Assert.That(bottom.Calls).IsEquivalentTo(["preview:bottom"]);
    }

    [Test]
    public async Task Remainder_Runs_When_Nothing_Handles()
    {
        var stack = new EngineInput.FocusStack();
        _ = stack.Push(new Stub("input"));
        EngineInput.KeyEvent? rest = null;

        var verdict = EngineInput.InputRouting.Route(
            Esc(), null, stack, out _, remainder: k => rest = k);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.Unhandled);
        await Assert.That(rest.HasValue).IsTrue();
    }

    [Test]
    public async Task Gesture_Modifiers_Exact_And_Rune_CaseExact()
    {
        var capture = new EngineInput.KeyBindings();
        capture.Bind(EngineInput.KeyGesture.ForChar('G'), "go-top");

        await Assert.That(capture.TryMatch(Char('G'), out _)).IsTrue();
        await Assert.That(capture.TryMatch(Char('g'), out _)).IsFalse();
        await Assert.That(capture.TryMatch(Char('G', EngineInput.KeyModifiers.Ctrl), out _)).IsFalse();
        await Assert.That(capture.TryMatch(Esc(), out _)).IsFalse();
    }

    [Test]
    public async Task Empty_Stack_Routes_To_Remainder()
    {
        var stack = new EngineInput.FocusStack();
        bool ran = false;

        var verdict = EngineInput.InputRouting.Route(Esc(), null, stack, out _, remainder: _ => ran = true);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.Unhandled);
        await Assert.That(ran).IsTrue();
        await Assert.That(stack.Pop()).IsFalse();
    }
}
