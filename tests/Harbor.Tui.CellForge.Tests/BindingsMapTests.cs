// T6 (#1188, epic #1155): Binding-as-data + BindingsMap + keymap overrides.
// Engine-vocabulary only (EngineInput-qualified); chat untouched.

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// T6 (#1188, epic #1155): key-spec parsing, priority match, focus-to-app
/// merge (focused wins), id-based overrides with clash detect, shown_keys.
/// </summary>
public sealed class BindingsMapTests
{
    private sealed class Scope(string id, bool canFocus = true) : EngineInput.FocusableBase(id), EngineInput.IBindingScope
    {
        public override bool CanFocus => canFocus;

        public EngineInput.BindingsMap Bindings { get; } = new();
    }

    private static EngineInput.KeyEvent Key(string spec)
    {
        AssertKeySpec(spec, out EngineInput.KeyGesture gesture);
        return gesture.Key == EngineInput.KeyCode.Char
            ? EngineInput.KeyEvent.Char(gesture.Char, gesture.Modifiers)
            : EngineInput.KeyEvent.Simple(gesture.Key, gesture.Modifiers);
    }

    private static void AssertKeySpec(string spec, out EngineInput.KeyGesture gesture)
    {
        if (!EngineInput.KeySpec.TryParse(spec, out gesture))
        {
            throw new InvalidOperationException($"Bad test spec: {spec}");
        }
    }

    private static EngineInput.Binding Bind(string id, string spec, string action, int priority = 0, bool show = true, string? group = null)
    {
        AssertKeySpec(spec, out EngineInput.KeyGesture gesture);
        return new EngineInput.Binding(id, gesture, action, Description: id, Show: show, Priority: priority, Group: group);
    }

    [Test]
    public async Task KeySpec_Parses_Modifiers_Named_And_Chars()
    {
        AssertKeySpec("ctrl+j", out EngineInput.KeyGesture ctrlJ);
        await Assert.That(ctrlJ.Modifiers).IsEqualTo(EngineInput.KeyModifiers.Ctrl);
        await Assert.That((char)ctrlJ.Char.Value).IsEqualTo('j');

        AssertKeySpec("ctrl+alt+k", out EngineInput.KeyGesture ctrlAltK);
        await Assert.That(ctrlAltK.Modifiers).IsEqualTo(EngineInput.KeyModifiers.Ctrl | EngineInput.KeyModifiers.Alt);

        AssertKeySpec("space", out EngineInput.KeyGesture space);
        await Assert.That(space.Key).IsEqualTo(EngineInput.KeyCode.Char);
        await Assert.That((char)space.Char.Value).IsEqualTo(' ');

        AssertKeySpec("enter", out EngineInput.KeyGesture enter);
        await Assert.That(enter.Key).IsEqualTo(EngineInput.KeyCode.Enter);

        AssertKeySpec("?", out EngineInput.KeyGesture help);
        await Assert.That((char)help.Char.Value).IsEqualTo('?');

        AssertKeySpec("F12", out EngineInput.KeyGesture f12);
        await Assert.That(f12.Key).IsEqualTo(EngineInput.KeyCode.F12);
    }

    [Test]
    public async Task KeySpec_Rejects_Empty_And_Unknown()
    {
        await Assert.That(EngineInput.KeySpec.TryParse(null, out _)).IsFalse();
        await Assert.That(EngineInput.KeySpec.TryParse("  ", out _)).IsFalse();
        await Assert.That(EngineInput.KeySpec.TryParse("ctrl+", out _)).IsFalse();
        await Assert.That(EngineInput.KeySpec.TryParse("ctrl+bogus-key", out _)).IsFalse();
        await Assert.That(EngineInput.KeySpec.TryParse("hyper+x", out _)).IsFalse();
    }

    // #1575: the key-spec vocabulary is a TABLE now (KeySpec.ModifierBits and
    // KeySpec.NamedKeys), and the point of a table is that it cannot lose a
    // row without something saying so. These two tests are that something:
    // they walk every spelling the parser accepts and pin the gesture each
    // one produces, so a row dropped from either table fails here.
    [Test]
    public async Task KeySpec_Parses_Every_Modifier_Spelling()
    {
        (string Spec, EngineInput.KeyModifiers Mods)[] cases =
        [
            ("ctrl", EngineInput.KeyModifiers.Ctrl),
            ("control", EngineInput.KeyModifiers.Ctrl),
            ("shift", EngineInput.KeyModifiers.Shift),
            ("alt", EngineInput.KeyModifiers.Alt),
            ("option", EngineInput.KeyModifiers.Alt),
            ("meta", EngineInput.KeyModifiers.Meta),
            ("super", EngineInput.KeyModifiers.Meta),
            ("win", EngineInput.KeyModifiers.Meta),
            ("cmd", EngineInput.KeyModifiers.Meta),
            ("command", EngineInput.KeyModifiers.Meta),
        ];

        foreach ((string spec, EngineInput.KeyModifiers mods) in cases)
        {
            AssertKeySpec(spec + "+x", out EngineInput.KeyGesture gesture);
            await Assert.That(gesture.Modifiers).IsEqualTo(mods);
            await Assert.That((char)gesture.Char.Value).IsEqualTo('x');
        }
    }

    [Test]
    public async Task KeySpec_Parses_Every_Named_Key()
    {
        (string Spec, EngineInput.KeyCode Key)[] cases =
        [
            ("enter", EngineInput.KeyCode.Enter),
            ("return", EngineInput.KeyCode.Enter),
            ("tab", EngineInput.KeyCode.Tab),
            ("backspace", EngineInput.KeyCode.Backspace),
            ("bspace", EngineInput.KeyCode.Backspace),
            ("escape", EngineInput.KeyCode.Escape),
            ("esc", EngineInput.KeyCode.Escape),
            ("up", EngineInput.KeyCode.Up),
            ("down", EngineInput.KeyCode.Down),
            ("left", EngineInput.KeyCode.Left),
            ("right", EngineInput.KeyCode.Right),
            ("home", EngineInput.KeyCode.Home),
            ("end", EngineInput.KeyCode.End),
            ("pageup", EngineInput.KeyCode.PageUp),
            ("page_up", EngineInput.KeyCode.PageUp),
            ("pgup", EngineInput.KeyCode.PageUp),
            ("pagedown", EngineInput.KeyCode.PageDown),
            ("page_down", EngineInput.KeyCode.PageDown),
            ("pgdn", EngineInput.KeyCode.PageDown),
            ("pgdown", EngineInput.KeyCode.PageDown),
            ("insert", EngineInput.KeyCode.Insert),
            ("ins", EngineInput.KeyCode.Insert),
            ("delete", EngineInput.KeyCode.Delete),
            ("del", EngineInput.KeyCode.Delete),
            ("f1", EngineInput.KeyCode.F1),
            ("f2", EngineInput.KeyCode.F2),
            ("f3", EngineInput.KeyCode.F3),
            ("f4", EngineInput.KeyCode.F4),
            ("f5", EngineInput.KeyCode.F5),
            ("f6", EngineInput.KeyCode.F6),
            ("f7", EngineInput.KeyCode.F7),
            ("f8", EngineInput.KeyCode.F8),
            ("f9", EngineInput.KeyCode.F9),
            ("f10", EngineInput.KeyCode.F10),
            ("f11", EngineInput.KeyCode.F11),
            ("f12", EngineInput.KeyCode.F12),
        ];

        foreach ((string spec, EngineInput.KeyCode key) in cases)
        {
            AssertKeySpec(spec, out EngineInput.KeyGesture gesture);
            await Assert.That(gesture.Key).IsEqualTo(key);
        }
    }

    [Test]
    public async Task KeySpec_Rejects_Names_Outside_The_Table()
    {
        foreach (string spec in new[] { "f0", "f13", "f01", "nope", "pgdn2", "spaces", "pageupup" })
        {
            await Assert.That(EngineInput.KeySpec.TryParse(spec, out _)).IsFalse();
        }
    }

    [Test]
    public async Task TryMatch_Highest_Priority_Wins_Char_CaseExact()
    {
        var map = new EngineInput.BindingsMap();
        map.Bind(Bind("lower", "g", "go", priority: 0));
        map.Bind(Bind("upper", "G", "go-top", priority: 5));

        await Assert.That(map.TryMatch(Key("G"), out EngineInput.Binding? hit)).IsTrue();
        await Assert.That(hit!.Action).IsEqualTo("go-top");
        await Assert.That(map.TryMatch(Key("g"), out EngineInput.Binding? low)).IsTrue();
        await Assert.That(low!.Id).IsEqualTo("lower");
    }

    [Test]
    public async Task Merge_Focused_Wins_Gesture_Clash_Regardless_Of_Priority()
    {
        var app = new EngineInput.BindingsMap();
        app.Bind(Bind("app-quit", "escape", "app-quit", priority: 100));
        var focused = new EngineInput.BindingsMap();
        focused.Bind(Bind("dlg-close", "escape", "dlg-close", priority: 0));

        EngineInput.BindingsMap merged = EngineInput.BindingsMap.Merge([focused, app]);

        await Assert.That(merged.TryMatch(Key("escape"), out EngineInput.Binding? hit)).IsTrue();
        await Assert.That(hit!.Id).IsEqualTo("dlg-close");
        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ApplyOverride_Rebinds_By_Id_And_Reports_Clash()
    {
        var map = new EngineInput.BindingsMap();
        map.Bind(Bind("quit", "escape", "quit"));
        map.Bind(Bind("help", "?", "help"));

        await Assert.That(map.ApplyOverride("help", "escape", out string? clash)).IsTrue();
        await Assert.That(clash).IsEqualTo("quit");

        await Assert.That(map.TryMatch(Key("escape"), out EngineInput.Binding? hit)).IsTrue();
        await Assert.That(hit!.Id).IsEqualTo("help");
    }

    [Test]
    public async Task ApplyOverride_Clean_Rebind_NoClash_Unknown_And_BadSpec_Fail()
    {
        var map = new EngineInput.BindingsMap();
        map.Bind(Bind("quit", "escape", "quit"));

        await Assert.That(map.ApplyOverride("quit", "ctrl+q", out string? clash)).IsTrue();
        await Assert.That(clash).IsNull();
        await Assert.That(map.TryMatch(Key("ctrl+q"), out _)).IsTrue();
        await Assert.That(map.TryMatch(Key("escape"), out _)).IsFalse();

        await Assert.That(map.ApplyOverride("missing", "x", out _)).IsFalse();
        await Assert.That(map.ApplyOverride("quit", "hyper+x", out _)).IsFalse();
    }

    [Test]
    public async Task ShownKeys_Filters_Hidden_And_Orders_By_Priority()
    {
        var map = new EngineInput.BindingsMap();
        map.Bind(Bind("a-low", "a", "a", priority: 0));
        map.Bind(Bind("secret", "s", "s", show: false));
        map.Bind(Bind("z-high", "z", "z", priority: 10));

        List<EngineInput.Binding> shown = map.ShownKeys();

        await Assert.That(shown.Count).IsEqualTo(2);
        await Assert.That(shown[0].Id).IsEqualTo("z-high");
        await Assert.That(shown[1].Id).IsEqualTo("a-low");
    }

    [Test]
    public async Task Chain_Focused_Scope_Beats_App_Unfocusable_Skipped()
    {
        var stack = new EngineInput.FocusStack();
        var app = new EngineInput.BindingsMap();
        app.Bind(Bind("app-save", "ctrl+s", "app-save"));

        var focused = new Scope("dialog");
        focused.Bindings.Bind(Bind("dlg-save", "ctrl+s", "dlg-save"));
        var off = new Scope("off", canFocus: false);
        off.Bindings.Bind(Bind("off-save", "ctrl+s", "off-save"));
        await Assert.That(stack.Push(off)).IsFalse();
        _ = stack.Push(focused);
        // Unfocusable entries are refused at push; the chain must also skip a
        // scope that flipped off after seating (dynamic CanFocus, in place).
        var flip = new Scope("flip");
        flip.Bindings.Bind(Bind("flip-x", "x", "flip-x"));
        _ = stack.Push(flip);

        await Assert.That(EngineInput.BindingsChain.TryResolve(Key("ctrl+s"), stack, app, out EngineInput.Binding? hit)).IsTrue();
        await Assert.That(hit!.Id).IsEqualTo("dlg-save");

        EngineInput.BindingsMap merged = EngineInput.BindingsChain.Merge(stack, app);
        await Assert.That(merged.TryGet("dlg-save", out _)).IsTrue();
        await Assert.That(merged.TryGet("app-save", out _)).IsFalse();
    }

    [Test]
    public async Task Chain_Falls_Back_To_App_When_No_Scope_Matches()
    {
        var stack = new EngineInput.FocusStack();
        _ = stack.Push(new Scope("plain"));
        var app = new EngineInput.BindingsMap();
        app.Bind(Bind("quit", "escape", "quit"));

        await Assert.That(EngineInput.BindingsChain.TryResolve(Key("escape"), stack, app, out EngineInput.Binding? hit)).IsTrue();
        await Assert.That(hit!.Action).IsEqualTo("quit");
        await Assert.That(EngineInput.BindingsChain.TryResolve(Key("f1"), stack, app, out _)).IsFalse();
    }

    [Test]
    public async Task DisplayKey_Formats_Gesture_Unless_Overridden()
    {
        EngineInput.Binding auto = Bind("save", "ctrl+s", "save");
        await Assert.That(auto.DisplayKey).IsEqualTo("ctrl+s");

        EngineInput.Binding space = Bind("jump", "space", "jump");
        await Assert.That(space.DisplayKey).IsEqualTo("space");

        AssertKeySpec("ctrl+j", out EngineInput.KeyGesture gesture);
        var custom = new EngineInput.Binding("j", gesture, "jump", KeyDisplay: "C-j");
        await Assert.That(custom.DisplayKey).IsEqualTo("C-j");
    }
}
