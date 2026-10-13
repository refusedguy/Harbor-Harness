// #1199 (epic #1195): focus scopes + modal trap.
// Engine-vocabulary only (EngineInput-qualified); chat untouched.

using System.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1199 (epic #1195): scope Tab-order, scope stack with focus return on
/// modal close, globals-up / rest-down-to-active-scope routing (modal trap).
/// </summary>
public sealed class FocusScopeTests
{
    private sealed class Stub(string id, bool canFocus = true) : EngineInput.FocusableBase(id)
    {
        private readonly bool _canFocus = canFocus;

        public override bool CanFocus => _canFocus && CanFocusMutable;

        public bool CanFocusMutable { get; set; } = true;

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

    private static EngineInput.FocusScope ScopeOf(string id, EngineInput.FocusScopeKind kind, params Stub[] entries)
    {
        var scope = new EngineInput.FocusScope(id, kind);
        for (int i = 0; i < entries.Length; i++)
        {
            scope.Add(entries[i]);
        }

        return scope;
    }

    [Test]
    public async Task Scope_Next_Wraps_And_Skips_Unfocusable()
    {
        var scope = ScopeOf("form", EngineInput.FocusScopeKind.Form, new Stub("a"), new Stub("off", canFocus: false), new Stub("b"));

        await Assert.That(scope.Next()).IsTrue();
        await Assert.That(scope.Current!.Id).IsEqualTo("a");
        await Assert.That(scope.Next()).IsTrue();
        await Assert.That(scope.Current!.Id).IsEqualTo("b");
        await Assert.That(scope.Next()).IsTrue();
        await Assert.That(scope.Current!.Id).IsEqualTo("a");
    }

    [Test]
    public async Task Scope_Previous_From_Unfocused_Lands_Last()
    {
        var scope = ScopeOf("tabs", EngineInput.FocusScopeKind.Container, new Stub("a"), new Stub("b"));

        await Assert.That(scope.Previous()).IsTrue();
        await Assert.That(scope.Current!.Id).IsEqualTo("b");
    }

    [Test]
    public async Task Push_Modal_Seeds_First_Pop_Returns_Focus()
    {
        var stack = new EngineInput.FocusScopeStack();
        var input = new Stub("input");
        var tabs = ScopeOf("tabs", EngineInput.FocusScopeKind.Container, input, new Stub("other"));
        _ = stack.Push(tabs);
        _ = tabs.FocusById("other");

        var ok = new Stub("ok");
        _ = stack.Push(ScopeOf("dialog", EngineInput.FocusScopeKind.Modal, ok));

        await Assert.That(stack.Active!.Id).IsEqualTo("dialog");
        await Assert.That(ok.IsFocused).IsTrue();

        _ = stack.Pop();

        await Assert.That(stack.Active!.Id).IsEqualTo("tabs");
        await Assert.That(ok.IsFocused).IsFalse();
        await Assert.That(stack.Active.Current!.Id).IsEqualTo("other");
        await Assert.That(input.IsFocused).IsFalse();
    }

    [Test]
    public async Task Push_Null_Or_Active_Refused_And_Pop_Empty_False()
    {
        var stack = new EngineInput.FocusScopeStack();
        var tabs = ScopeOf("tabs", EngineInput.FocusScopeKind.Container, new Stub("a"));

        await Assert.That(stack.Push(null)).IsFalse();
        await Assert.That(stack.Push(tabs)).IsTrue();
        await Assert.That(stack.Push(tabs)).IsTrue();
        await Assert.That(stack.Count).IsEqualTo(1);
        await Assert.That(stack.Pop()).IsTrue();
        await Assert.That(stack.Pop()).IsFalse();
        await Assert.That(stack.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Pop_Falls_Back_To_First_When_Return_Disabled()
    {
        var stack = new EngineInput.FocusScopeStack();
        var input = new Stub("input");
        var tabs = ScopeOf("tabs", EngineInput.FocusScopeKind.Container, input, new Stub("other"));
        _ = stack.Push(tabs);
        _ = stack.Push(ScopeOf("dialog", EngineInput.FocusScopeKind.Modal, new Stub("ok")));

        input.CanFocusMutable = false;
        _ = stack.Pop();

        await Assert.That(stack.Active!.Current!.Id).IsEqualTo("other");
    }

    [Test]
    public async Task Route_Globals_Preempt_Active_Scope()
    {
        var stack = new EngineInput.FocusScopeStack();
        var input = new Stub("input") { HandleResult = true };
        _ = stack.Push(ScopeOf("tabs", EngineInput.FocusScopeKind.Container, input));
        var globals = new EngineInput.KeyBindings();
        globals.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Escape), "quit");

        var verdict = stack.Route(Esc(), globals, out string? action);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.CaptureHandled);
        await Assert.That(action).IsEqualTo("quit");
        await Assert.That(input.Calls).IsEmpty();
    }

    [Test]
    public async Task Route_Traps_Beneath_Scope()
    {
        var stack = new EngineInput.FocusScopeStack();
        var beneath = new Stub("beneath") { HandleResult = true };
        var modal = new Stub("modal") { HandleResult = true };
        _ = stack.Push(ScopeOf("tabs", EngineInput.FocusScopeKind.Container, beneath));
        _ = stack.Push(ScopeOf("dialog", EngineInput.FocusScopeKind.Modal, modal));

        var verdict = stack.Route(Esc(), null, out _);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.FocusHandled);
        await Assert.That(beneath.Calls).IsEmpty();
        await Assert.That(modal.Calls).IsEquivalentTo(["preview:modal", "handle:modal"]);
    }

    [Test]
    public async Task Route_Empty_Stack_Runs_Remainder()
    {
        var stack = new EngineInput.FocusScopeStack();
        bool ran = false;

        var verdict = stack.Route(Esc(), null, out _, remainder: _ => ran = true);

        await Assert.That(verdict).IsEqualTo(EngineInput.InputDisposition.Unhandled);
        await Assert.That(ran).IsTrue();
    }
}
