using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Input;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Painting reads the global TerminalColorPalette, so these serialize with other paint tests.
[NotInParallel("pty")]
public class GenericToggleSelectTests
{
    private static KeyEvent Space() => KeyEvent.Char(new Rune(' '));

    private static GenericToggleSelect Boolean(
        ToggleSelectStyle style,
        Func<int> getValue,
        Action<int> onChange) => new("toggle", style, getValue, onChange);

    private static GenericToggleSelect List(
        ToggleSelectStyle style,
        Func<int> getValue,
        Action<int> onChange,
        params string[] options)
    {
        var widget = new GenericToggleSelect("choice", style, getValue, onChange);
        widget.SetOptions(options);
        return widget;
    }

    // ------------------------------------------------------------ contract --

    [Test]
    public async Task Value_IsReadFromHost_NotStoredInWidget()
    {
        int external = 1;
        var widget = Boolean(ToggleSelectStyle.Checkbox, () => external, _ => { });

        await Assert.That(widget.Value).IsEqualTo(1);
        external = 0; // host changes state on its own (reload, undo, ...)
        await Assert.That(widget.Value).IsEqualTo(0);
        await Assert.That(widget.Checked).IsFalse();
    }

    [Test]
    public async Task Activation_AnnouncesNext_AndHostAppliesIt()
    {
        int external = 0;
        var seen = new List<int>();
        var widget = Boolean(ToggleSelectStyle.Checkbox, () => external, next => { seen.Add(next); external = next; });

        await Assert.That(widget.HandleKey(Space())).IsTrue();
        await Assert.That(seen).Count().IsEqualTo(1);
        await Assert.That(seen[0]).IsEqualTo(1);
        await Assert.That(widget.Value).IsEqualTo(1); // getter reads the host's new state

        _ = widget.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(seen[1]).IsEqualTo(0);
        await Assert.That(widget.Checked).IsFalse();
    }

    [Test]
    [Arguments(ToggleSelectStyle.Checkbox)]
    [Arguments(ToggleSelectStyle.Switch)]
    [Arguments(ToggleSelectStyle.Toggle)]
    public async Task BooleanStyles_EnterAndSpace_FlipBetweenZeroAndOne(ToggleSelectStyle style)
    {
        int external = 0;
        var seen = new List<int>();
        var widget = Boolean(style, () => external, next => { seen.Add(next); external = next; });

        await Assert.That(widget.IsBoolean).IsTrue();
        await Assert.That(widget.SelectedIndex).IsEqualTo(-1);

        _ = widget.HandleKey(Space());
        _ = widget.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(seen).IsEquivalentTo(new[] { 1, 0 });
        await Assert.That(widget.Checked).IsFalse();
    }

    [Test]
    [Arguments(ToggleSelectStyle.Radio)]
    [Arguments(ToggleSelectStyle.Select)]
    public async Task ListStyles_ArrowsMoveCursor_EnterAndSpaceCommit(ToggleSelectStyle style)
    {
        int external = 0;
        var seen = new List<int>();
        var widget = List(style, () => external, next => { seen.Add(next); external = next; },
            "Alpha", "Beta", "Gamma");

        await Assert.That(widget.IsBoolean).IsFalse();
        await Assert.That(widget.Cursor).IsEqualTo(0);

        _ = widget.HandleKey(KeyEvent.Simple(KeyCode.Down));
        _ = widget.HandleKey(KeyEvent.Simple(KeyCode.Right));
        await Assert.That(widget.Cursor).IsEqualTo(2);
        await Assert.That(seen).IsEmpty(); // navigation never changes the controlled value

        _ = widget.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(seen[0]).IsEqualTo(2);
        await Assert.That(widget.SelectedIndex).IsEqualTo(2);

        _ = widget.HandleKey(KeyEvent.Simple(KeyCode.Home));
        await Assert.That(widget.HandleKey(Space())).IsTrue();
        await Assert.That(seen[1]).IsEqualTo(0);

        _ = widget.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(widget.Cursor).IsEqualTo(2);
    }

    [Test]
    public async Task ListStyles_ValueOutsideOptions_ReportsNoSelection()
    {
        var widget = List(ToggleSelectStyle.Select, () => 9, _ => { }, "Alpha", "Beta");

        await Assert.That(widget.SelectedIndex).IsEqualTo(-1);
    }

    // ------------------------------------------------------------- guarding --

    [Test]
    public async Task Disabled_RefusesKeys_AndReportsCannotFocus()
    {
        int external = 0;
        var seen = new List<int>();
        var widget = Boolean(ToggleSelectStyle.Checkbox, () => external, next => seen.Add(next));
        widget.Disabled = true;

        await Assert.That(widget.CanFocus).IsFalse();
        await Assert.That(widget.HandleKey(Space())).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(seen).IsEmpty();
    }

    [Test]
    public async Task KeyGate_RefusesChordsReleasesAndUnboundRunes()
    {
        var seen = new List<int>();
        var widget = Boolean(ToggleSelectStyle.Toggle, () => 0, next => seen.Add(next));

        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune(' '), KeyModifiers.Shift))).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(widget.HandleKey(
            new KeyEvent(KeyCode.Enter, default, KeyModifiers.None, KeyEventType.Release, false))).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        await Assert.That(seen).IsEmpty();
    }

    [Test]
    public async Task FocusRouter_DrivesFocus_AndActivationFlowsThroughFocusedEntry()
    {
        int first = 0;
        int second = 0;
        var seen = new List<string>();
        var a = new GenericToggleSelect("a", ToggleSelectStyle.Checkbox, () => first, _ => { first = 1; seen.Add("a"); });
        var b = new GenericToggleSelect("b", ToggleSelectStyle.Switch, () => second, _ => { second = 1; seen.Add("b"); });
        var router = new FocusRouter();
        router.Add(a);
        router.Add(b);

        await Assert.That(a.IsFocused).IsFalse();
        await Assert.That(router.Next()).IsTrue();
        await Assert.That(router.Current).IsSameReferenceAs(a);
        await Assert.That(a.IsFocused).IsTrue();

        _ = a.HandleKey(Space());
        await Assert.That(seen).IsEquivalentTo(new[] { "a" });
        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(0); // the unfocused entry was untouched

        await Assert.That(router.FocusById("b")).IsTrue();
        await Assert.That(a.IsFocused).IsFalse();
        await Assert.That(b.IsFocused).IsTrue();
    }

    [Test]
    public async Task Changed_RaisedOnFocusActivationAndEnableChanges()
    {
        int external = 0;
        int changes = 0;
        var widget = Boolean(ToggleSelectStyle.Checkbox, () => external, next => external = next);
        widget.Changed += (_, _) => changes++;

        widget.OnFocusChanged(true);
        _ = widget.HandleKey(Space());
        widget.Disabled = true;

        await Assert.That(changes).IsEqualTo(3);
    }

    // --------------------------------------------------------------- golden --

    [Test]
    [Arguments(ToggleSelectStyle.Checkbox, 0, "[ ]")]
    [Arguments(ToggleSelectStyle.Checkbox, 1, "[x]")]
    [Arguments(ToggleSelectStyle.Switch, 0, "[-]")]
    [Arguments(ToggleSelectStyle.Switch, 1, "[=]")]
    [Arguments(ToggleSelectStyle.Toggle, 0, "< >")]
    [Arguments(ToggleSelectStyle.Toggle, 1, "<x>")]
    public async Task Golden_BooleanStyle_StateMatrix(ToggleSelectStyle style, int value, string glyph)
    {
        var widget = Boolean(style, () => value, _ => { });
        widget.Label = "Label";
        var buffer = TestRender.Buffer(24, 1);

        widget.Paint(buffer, new Rect(0, 0, 24, 1));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 24).TrimEnd()).IsEqualTo(glyph + " Label");
    }

    [Test]
    [Arguments(ToggleSelectStyle.Radio, "( )", "(*)")]
    [Arguments(ToggleSelectStyle.Select, "[ ]", "[x]")]
    public async Task Golden_ListStyle_StateMatrix(ToggleSelectStyle style, string off, string on)
    {
        var widget = List(style, () => 1, _ => { }, "Alpha", "Beta", "Gamma");
        var buffer = TestRender.Buffer(24, 3);

        widget.Paint(buffer, new Rect(0, 0, 24, 3));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 24).TrimEnd()).IsEqualTo("  " + off + " Alpha");
        await Assert.That(ScreenFixtures.Row(buffer, 1, 24).TrimEnd()).IsEqualTo("> " + on + " Beta");
        await Assert.That(ScreenFixtures.Row(buffer, 2, 24).TrimEnd()).IsEqualTo("  " + off + " Gamma");
    }

    [Test]
    public async Task Golden_Disabled_PaintsSameText_WithDimStyle()
    {
        var widget = Boolean(ToggleSelectStyle.Checkbox, () => 1, _ => { });
        widget.Label = "Label";
        widget.Disabled = true;
        var buffer = TestRender.Buffer(24, 1);

        widget.Paint(buffer, new Rect(0, 0, 24, 1));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 24).TrimEnd()).IsEqualTo("[x] Label");
        await Assert.That((buffer.Get(0, 0).Style.Attrs & StyleAttr.Dim) != 0).IsTrue();
    }
}
