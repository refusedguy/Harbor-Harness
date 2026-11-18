using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Input;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Pure contract/keyboard tests: no palette, no buffer, nothing to serialize.
public class ActionButtonTests
{
    private static KeyEvent Space() => KeyEvent.Char(new Rune(' '));
    private static KeyEvent Enter() => KeyEvent.Simple(KeyCode.Enter);

    [Test]
    public async Task Activation_InvokesExternalCallback_AndRaisesChanged()
    {
        int fired = 0;
        int changes = 0;
        var button = new ActionButton("save", "Save", () => fired++);
        button.Changed += (_, _) => changes++;

        await Assert.That(button.HandleKey(Enter())).IsTrue();
        await Assert.That(fired).IsEqualTo(1);
        await Assert.That(changes).IsEqualTo(1);
        await Assert.That(button.HandleKey(Space())).IsTrue();
        await Assert.That(fired).IsEqualTo(2);
    }

    [Test]
    public async Task Label_IsHostOwned_AndMutable()
    {
        var button = new ActionButton("id", "Label", () => { }, glyph: "[+]");
        await Assert.That(button.Label).IsEqualTo("Label");
        await Assert.That(button.Glyph).IsEqualTo("[+]");
        button.Label = "New";
        await Assert.That(button.Label).IsEqualTo("New");
    }

    [Test]
    public async Task CanFocus_ReadsExternalEnabledGetter()
    {
        bool enabled = false;
        var button = new ActionButton("id", "Save", () => { }, isEnabled: () => enabled);
        await Assert.That(button.CanFocus).IsFalse();
        enabled = true;
        await Assert.That(button.CanFocus).IsTrue();
    }

    [Test]
    public async Task Disabled_RefusesAllKeys_AndReportsCannotFocus()
    {
        int fired = 0;
        var button = new ActionButton("id", "Save", () => fired++, isEnabled: () => false);
        await Assert.That(button.CanFocus).IsFalse();
        await Assert.That(button.HandleKey(Enter())).IsFalse();
        await Assert.That(button.HandleKey(Space())).IsFalse();
        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune('s')))).IsFalse();
        await Assert.That(fired).IsEqualTo(0);
    }

    [Test]
    public async Task KeyGate_RefusesChordsReleasesAndUnboundRunes()
    {
        int fired = 0;
        var button = new ActionButton("id", "Save", () => fired++);
        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune(' '), KeyModifiers.Shift))).IsFalse();
        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(button.HandleKey(
            new KeyEvent(KeyCode.Enter, default, KeyModifiers.None, KeyEventType.Release, false))).IsFalse();
        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        await Assert.That(fired).IsEqualTo(0);
    }

    [Test]
    public async Task Hotkey_ExactMatchActivates_OtherModifiersAndCharsDoNot()
    {
        int fired = 0;
        var button = new ActionButton("id", "Save", () => fired++, hotkey: ActionHotkey.Char('s'));

        await Assert.That(button.MatchesHotkey(KeyEvent.Char(new Rune('s')))).IsTrue();
        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune('s')))).IsTrue();
        await Assert.That(fired).IsEqualTo(1);

        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune('S'), KeyModifiers.Shift))).IsFalse();
        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune('s'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        await Assert.That(fired).IsEqualTo(1);
    }

    [Test]
    public async Task Hotkey_NamedKey_AndDisplay()
    {
        int fired = 0;
        var hotkey = ActionHotkey.Named(KeyCode.F2);
        var button = new ActionButton("id", "Rename", () => fired++, hotkey: hotkey);

        await Assert.That(hotkey.Display).IsEqualTo("F2");
        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.F2))).IsTrue();
        await Assert.That(fired).IsEqualTo(1);
        await Assert.That(ActionHotkey.Char('p', KeyModifiers.Ctrl).Display).IsEqualTo("^P");
    }

    [Test]
    public async Task FocusRouter_DrivesFocus_AndActivationFlowsThroughFocusedEntry()
    {
        int first = 0;
        int second = 0;
        var seen = new List<string>();
        var a = new ActionButton("a", "A", () => { first++; seen.Add("a"); });
        var b = new ActionButton("b", "B", () => { second++; seen.Add("b"); });
        var router = new FocusRouter();
        router.Add(a);
        router.Add(b);

        await Assert.That(a.IsFocused).IsFalse();
        await Assert.That(router.Next()).IsTrue();
        await Assert.That(router.Current).IsSameReferenceAs(a);
        await Assert.That(a.IsFocused).IsTrue();

        _ = a.HandleKey(Enter());
        await Assert.That(seen).IsEquivalentTo(new[] { "a" });
        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(0);

        await Assert.That(router.FocusById("b")).IsTrue();
        await Assert.That(a.IsFocused).IsFalse();
        await Assert.That(b.IsFocused).IsTrue();
    }
}

public class ToolbarTests
{
    [Test]
    public async Task TabNavigation_MovesFocusWithinRow()
    {
        var a = new ActionButton("a", "A", () => { });
        var b = new ActionButton("b", "B", () => { });
        var toolbar = new Toolbar(a, b);

        await Assert.That(toolbar.Focused).IsNull();
        await Assert.That(toolbar.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(toolbar.Focused).IsSameReferenceAs(a);
        await Assert.That(a.IsFocused).IsTrue();

        await Assert.That(toolbar.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(toolbar.Focused).IsSameReferenceAs(b);
        await Assert.That(a.IsFocused).IsFalse();

        await Assert.That(toolbar.HandleKey(KeyEvent.Simple(KeyCode.Tab, KeyModifiers.Shift))).IsTrue();
        await Assert.That(toolbar.Focused).IsSameReferenceAs(a);
    }

    [Test]
    public async Task Accelerator_FiresOnDeclaringRow_RegardlessOfFocus()
    {
        int firedA = 0;
        int firedB = 0;
        var a = new ActionButton("a", "Save", () => firedA++, hotkey: ActionHotkey.Char('s'));
        var b = new ActionButton("b", "Quit", () => firedB++, hotkey: ActionHotkey.Char('q'));
        var toolbar = new Toolbar(a, b);
        toolbar.Focus("b");

        await Assert.That(toolbar.HandleKey(KeyEvent.Char(new Rune('s')))).IsTrue();
        await Assert.That(firedA).IsEqualTo(1);
        await Assert.That(firedB).IsEqualTo(0);
        await Assert.That(toolbar.Focused).IsSameReferenceAs(a);

        // The focused row's own accelerator still fires once, not twice.
        await Assert.That(toolbar.HandleKey(KeyEvent.Char(new Rune('s')))).IsTrue();
        await Assert.That(firedA).IsEqualTo(2);
    }

    [Test]
    public async Task EnterAndSpace_GoToFocusedRowOnly()
    {
        int firedA = 0;
        int firedB = 0;
        var a = new ActionButton("a", "A", () => firedA++);
        var b = new ActionButton("b", "B", () => firedB++);
        var toolbar = new Toolbar(a, b);
        toolbar.Focus("b");

        await Assert.That(toolbar.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(toolbar.HandleKey(KeyEvent.Char(new Rune(' ')))).IsTrue();
        await Assert.That(firedA).IsEqualTo(0);
        await Assert.That(firedB).IsEqualTo(2);
    }

    [Test]
    public async Task UnboundKey_AndRelease_DoNothing()
    {
        int fired = 0;
        var a = new ActionButton("a", "A", () => fired++);
        var toolbar = new Toolbar(a);
        toolbar.Focus("a");

        await Assert.That(toolbar.HandleKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        await Assert.That(toolbar.HandleKey(
            new KeyEvent(KeyCode.Enter, default, KeyModifiers.None, KeyEventType.Release, false))).IsFalse();
        await Assert.That(fired).IsEqualTo(0);
    }
}

// Painting reads the global TerminalColorPalette, so these serialize with other paint tests.
[NotInParallel("pty")]
public class ActionToolbarPaintTests
{
    private static string Art(ActionButton button)
    {
        int width = button.MeasureWidth();
        var buffer = TestRender.Buffer(width, 1);
        button.Paint(buffer, new Rect(0, 0, width, 1));
        return ScreenFixtures.Row(buffer, 0, width);
    }

    [Test]
    public async Task Golden_ButtonRow_Unfocused()
    {
        var button = new ActionButton("save", "Save", () => { }, hotkey: ActionHotkey.Char('s'));
        await Assert.That(Art(button)).IsEqualTo("  Save [S]");
    }

    [Test]
    public async Task Golden_ButtonRow_Focused()
    {
        var button = new ActionButton("save", "Save", () => { }, hotkey: ActionHotkey.Char('s'));
        button.OnFocusChanged(true);
        await Assert.That(Art(button)).IsEqualTo("> Save [S]");
    }

    [Test]
    public async Task Golden_ButtonRow_GlyphNoHotkey()
    {
        var button = new ActionButton("add", "Add", () => { }, glyph: "[+]");
        await Assert.That(Art(button)).IsEqualTo("  [+] Add");
    }

    [Test]
    public async Task Golden_Disabled_DimsTextAndMarker()
    {
        var button = new ActionButton("save", "Save", () => { }, isEnabled: () => false);
        var buffer = TestRender.Buffer(button.MeasureWidth(), 1);
        button.Paint(buffer, new Rect(0, 0, button.MeasureWidth(), 1));

        await Assert.That(ScreenFixtures.Row(buffer, 0, button.MeasureWidth()).TrimEnd()).IsEqualTo("  Save");
        await Assert.That((buffer.Get(0, 0).Style.Attrs & StyleAttr.Dim) != 0).IsTrue();
    }

    [Test]
    public async Task Golden_FocusMarker_UsesReverseAttribute()
    {
        var button = new ActionButton("save", "Save", () => { });
        button.OnFocusChanged(true);
        var buffer = TestRender.Buffer(button.MeasureWidth(), 1);
        button.Paint(buffer, new Rect(0, 0, button.MeasureWidth(), 1));

        await Assert.That((buffer.Get(0, 0).Style.Attrs & StyleAttr.Reverse) != 0).IsTrue();
    }

    [Test]
    public async Task Golden_Toolbar_ComposesRowsWithSeparator()
    {
        var a = new ActionButton("go", "Go", () => { });
        var b = new ActionButton("stop", "Stop", () => { });
        var toolbar = new Toolbar(a, b);
        var buffer = TestRender.Buffer(11, 1);
        toolbar.Paint(buffer, new Rect(0, 0, 11, 1));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 11)).IsEqualTo("  Go   Stop");
    }

    [Test]
    public async Task Golden_Toolbar_FocusMarkerOnFocusedRow()
    {
        var a = new ActionButton("go", "Go", () => { });
        var b = new ActionButton("stop", "Stop", () => { });
        var toolbar = new Toolbar(a, b);
        toolbar.FocusNext();
        var buffer = TestRender.Buffer(11, 1);
        toolbar.Paint(buffer, new Rect(0, 0, 11, 1));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 11)).IsEqualTo("> Go   Stop");
    }
}
