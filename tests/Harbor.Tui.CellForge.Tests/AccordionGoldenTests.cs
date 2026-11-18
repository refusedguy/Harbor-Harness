using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Accordion (#2379): external expanded state, single/multiple policy, keyboard
// toggle and pinned grid art. Goldens are style-blind on purpose — the painted
// GLYPHS are what a user reads; styles are asserted separately below.
// Painting reads the global TerminalColorPalette — serialized with other paint tests.
[NotInParallel("pty")]
public class AccordionGoldenTests
{
    private static readonly AccordionSection[] Sections =
    [
        new("a", "Alpha", ["one", "two"]),
        new("b", "Beta", ["three"]),
        new("c", "Gamma", ["four"]),
    ];

    private static Accordion Widget(
        AccordionPolicy policy,
        HashSet<string> expanded,
        Action<IReadOnlySet<string>>? onChange = null) =>
        new("accordion", Sections, policy, () => expanded, onChange ?? (_ => { }));

    private static Action<IReadOnlySet<string>> ApplyTo(HashSet<string> expanded) => next =>
    {
        expanded.Clear();
        foreach (string id in next)
        {
            expanded.Add(id);
        }
    };

    private static string Row(ScreenBuffer buffer, int row, int width = 24) =>
        ScreenFixtures.Row(buffer, row, width).TrimEnd();

    // ------------------------------------------------------------ contract --

    [Test]
    public async Task Expanded_IsReadFromTheHost_NotStoredInTheWidget()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Single, expanded);

        await Assert.That(widget.IsExpanded("a")).IsFalse();
        expanded.Add("a"); // host reloads or restores a session
        await Assert.That(widget.IsExpanded("a")).IsTrue();
        await Assert.That(widget.Expanded.Contains("a")).IsTrue();

        expanded.Clear();
        await Assert.That(widget.IsExpanded("a")).IsFalse();
    }

    [Test]
    public async Task Single_Toggle_ReplacesTheOpenSectionAndCollapsesItAgain()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Single, expanded, ApplyTo(expanded));

        await Assert.That(widget.Toggle(0)).IsTrue();
        await Assert.That(expanded.SetEquals(new[] { "a" })).IsTrue();

        await Assert.That(widget.Toggle(1)).IsTrue();
        await Assert.That(expanded.SetEquals(new[] { "b" })).IsTrue();

        await Assert.That(widget.Toggle(1)).IsTrue();
        await Assert.That(expanded.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Multiple_Toggle_LeavesOtherSectionsAlone()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Multiple, expanded, ApplyTo(expanded));

        widget.Toggle(0);
        widget.Toggle(2);
        await Assert.That(expanded.SetEquals(new[] { "a", "c" })).IsTrue();

        widget.Toggle(0);
        await Assert.That(expanded.SetEquals(new[] { "c" })).IsTrue();
    }

    [Test]
    public async Task OutOfRangeAndDisabled_TogglesAreRefused()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Multiple, expanded, ApplyTo(expanded));

        await Assert.That(widget.Toggle(-1)).IsFalse();
        await Assert.That(widget.Toggle(99)).IsFalse();
        await Assert.That(expanded.Count).IsEqualTo(0);

        widget.Disabled = true;
        await Assert.That(widget.Toggle(0)).IsFalse();
        await Assert.That(expanded.Count).IsEqualTo(0);
    }

    [Test]
    public async Task HostRejectingTheNextSet_LeavesTheWidgetInSync()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Single, expanded, _ => { });

        await Assert.That(widget.Toggle(0)).IsTrue();
        await Assert.That(widget.IsExpanded("a")).IsFalse()
            .Because("the host owns the set; a callback that refuses the change keeps the widget honest");
    }

    // ------------------------------------------------------------ keyboard --

    [Test]
    public async Task Keyboard_NavigatesSectionsWithArrowsVimHomeAndEnd()
    {
        var widget = Widget(AccordionPolicy.Multiple, new HashSet<string>());
        await Assert.That(widget.Cursor).IsEqualTo(0);

        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(1);
        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune('j')))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(2);
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(1);
        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune('h')))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(0);

        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.End))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(2);
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Home))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(0);

        // Edges are consumed without moving, like the other primitives.
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Up))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task Keyboard_EnterAndSpaceToggleTheFocusedSection()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Multiple, expanded, ApplyTo(expanded));

        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(expanded.SetEquals(new[] { "b" })).IsTrue();

        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune(' ')))).IsTrue();
        await Assert.That(expanded.Count).IsEqualTo(0);
    }

    [Test]
    public async Task KeyGate_RefusesModifiedAndReleaseKeys()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Multiple, expanded, ApplyTo(expanded));

        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune('j'), KeyModifiers.Shift))).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune(' '), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Down, KeyModifiers.Alt))).IsFalse();
        await Assert.That(widget.HandleKey(new KeyEvent(
            KeyCode.Down, default, KeyModifiers.None, KeyEventType.Release, false))).IsFalse();
        await Assert.That(expanded.Count).IsEqualTo(0);
        await Assert.That(widget.Cursor).IsEqualTo(0);

        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune('j')))).IsTrue();
        await Assert.That(widget.Cursor).IsEqualTo(1);
    }

    [Test]
    public async Task Disabled_RefusesEveryKey()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Multiple, expanded, ApplyTo(expanded));
        widget.Disabled = true;

        await Assert.That(widget.CanFocus).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(widget.HandleKey(KeyEvent.Char(new Rune(' ')))).IsFalse();
        await Assert.That(expanded.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Changed_RaisedOnNavigationFocusAndSectionChanges()
    {
        var widget = Widget(AccordionPolicy.Multiple, new HashSet<string>());
        int changes = 0;
        widget.Changed += (_, _) => changes++;

        widget.HandleKey(KeyEvent.Simple(KeyCode.Down));
        widget.OnFocusChanged(true);
        widget.SetSections(Sections);
        widget.Disabled = true;
        await Assert.That(changes).IsEqualTo(4);
    }

    [Test]
    public async Task SetSections_ClampsCursorIntoTheNewList()
    {
        var widget = Widget(AccordionPolicy.Multiple, new HashSet<string>());
        widget.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(widget.Cursor).IsEqualTo(2);

        widget.SetSections([Sections[0], Sections[1]]);
        await Assert.That(widget.Cursor).IsEqualTo(1);
        await Assert.That(widget.Sections.Count).IsEqualTo(2);
    }

    // --------------------------------------------------------------- golden --

    [Test]
    public async Task Golden_AllCollapsed()
    {
        var widget = Widget(AccordionPolicy.Multiple, new HashSet<string>());
        var buffer = TestRender.Buffer(24, 8);
        widget.Paint(buffer, new Rect(0, 0, 24, 8));

        await Assert.That(Row(buffer, 0)).IsEqualTo("> + Alpha");
        await Assert.That(Row(buffer, 1)).IsEqualTo("  + Beta");
        await Assert.That(Row(buffer, 2)).IsEqualTo("  + Gamma");
        await Assert.That(Row(buffer, 3)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Golden_SingleOneOpen_ShiftsLaterSectionsDown()
    {
        var widget = Widget(AccordionPolicy.Single, new HashSet<string> { "a" });
        var buffer = TestRender.Buffer(24, 8);
        widget.Paint(buffer, new Rect(0, 0, 24, 8));

        await Assert.That(Row(buffer, 0)).IsEqualTo("> - Alpha");
        await Assert.That(Row(buffer, 1)).IsEqualTo("    one");
        await Assert.That(Row(buffer, 2)).IsEqualTo("    two");
        await Assert.That(Row(buffer, 3)).IsEqualTo("  + Beta");
        await Assert.That(Row(buffer, 4)).IsEqualTo("  + Gamma");
    }

    [Test]
    public async Task Golden_MultipleTwoOpen()
    {
        var widget = Widget(AccordionPolicy.Multiple, new HashSet<string> { "a", "c" });
        var buffer = TestRender.Buffer(24, 8);
        widget.Paint(buffer, new Rect(0, 0, 24, 8));

        await Assert.That(Row(buffer, 0)).IsEqualTo("> - Alpha");
        await Assert.That(Row(buffer, 1)).IsEqualTo("    one");
        await Assert.That(Row(buffer, 2)).IsEqualTo("    two");
        await Assert.That(Row(buffer, 3)).IsEqualTo("  + Beta");
        await Assert.That(Row(buffer, 4)).IsEqualTo("  - Gamma");
        await Assert.That(Row(buffer, 5)).IsEqualTo("    four");
    }

    [Test]
    public async Task Golden_KeyboardToggle_ChangesTheArt()
    {
        var expanded = new HashSet<string>();
        var widget = Widget(AccordionPolicy.Single, expanded, ApplyTo(expanded));
        widget.HandleKey(KeyEvent.Simple(KeyCode.Down));
        widget.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        var buffer = TestRender.Buffer(24, 8);
        widget.Paint(buffer, new Rect(0, 0, 24, 8));

        await Assert.That(Row(buffer, 0)).IsEqualTo("  + Alpha");
        await Assert.That(Row(buffer, 1)).IsEqualTo("> - Beta");
        await Assert.That(Row(buffer, 2)).IsEqualTo("    three");
        await Assert.That(Row(buffer, 3)).IsEqualTo("  + Gamma");
    }

    [Test]
    public async Task Golden_NarrowRect_ClipsHeaders()
    {
        var widget = Widget(AccordionPolicy.Multiple, new HashSet<string>());
        var buffer = TestRender.Buffer(8, 4);
        widget.Paint(buffer, new Rect(0, 0, 8, 4));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 8).TrimEnd()).IsEqualTo("> + Alph");
        await Assert.That(ScreenFixtures.Row(buffer, 1, 8).TrimEnd()).IsEqualTo("  + Beta");
    }

    [Test]
    public async Task Paint_StylesCursorHeaderAndBodyDifferently()
    {
        var widget = Widget(AccordionPolicy.Multiple, new HashSet<string> { "a" });
        var buffer = TestRender.Buffer(24, 8);
        widget.Paint(buffer, new Rect(0, 0, 24, 8));

        var cursorHeader = buffer.Get(0, 0);
        var plainHeader = buffer.Get(0, 3);
        var body = buffer.Get(4, 1);
        await Assert.That(cursorHeader.HasSameStyle(plainHeader)).IsFalse();
        await Assert.That(body.HasSameStyle(plainHeader)).IsFalse();
    }
}
