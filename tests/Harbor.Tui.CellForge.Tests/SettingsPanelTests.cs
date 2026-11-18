using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Input;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Painting reads the global TerminalColorPalette, so these serialize with other paint tests.
[NotInParallel("pty")]
public class SettingsPanelTests
{
    private static SettingsGroup Group(string title, params SettingEntry[] entries) => new(title, entries);

    private static SettingsPage Page(string id, string title, params SettingsGroup[] groups) => new(id, title, groups);

    private static SettingsPanel Panel(
        Func<SettingEntry, string> getValue,
        Action<SettingEntry, string> onChange,
        Func<SettingEntry, string, string?>? validate = null,
        params SettingsPage[] pages) => new("settings", pages, getValue, onChange, validate);

    private static SettingsPage GeneralPage() => Page("general", "General",
        Group("Display",
            SettingEntry.Choice("theme", "Theme", ["dark", "light", "system"]),
            SettingEntry.Text("font", "Font")),
        Group("Alerts", SettingEntry.Toggle("bell", "Bell")));

    private static Dictionary<string, string> Values() => new()
    {
        ["theme"] = "dark",
        ["font"] = "14",
        ["bell"] = "0",
        ["lang"] = "en",
    };

    // ------------------------------------------------------------ contract --

    [Test]
    public async Task Entries_FlattenAcrossGroups_AndValuesComeFromHost()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null, GeneralPage());

        await Assert.That(panel.PageCount).IsEqualTo(1);
        await Assert.That(panel.Entries.Count).IsEqualTo(3);
        await Assert.That(panel.CurrentEntry!.Id).IsEqualTo("theme");
        await Assert.That(panel.CurrentValue).IsEqualTo("dark");

        _ = panel.Move(1);
        await Assert.That(panel.CurrentEntry!.Id).IsEqualTo("font");
        _ = panel.Move(1);
        await Assert.That(panel.CurrentEntry!.Id).IsEqualTo("bell");
        await Assert.That(panel.CurrentValue).IsEqualTo("0");
    }

    [Test]
    public async Task Pages_SwitchByLeftRight_AndResetEntryCursor()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null,
            GeneralPage(),
            Page("lang", "Language", Group("Locale", SettingEntry.Choice("lang", "Language", ["en", "ru"]))));

        _ = panel.Move(1);
        await Assert.That(panel.Index).IsEqualTo(1);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Right));

        await Assert.That(panel.PageIndex).IsEqualTo(1);
        await Assert.That(panel.Index).IsEqualTo(0);
        await Assert.That(panel.CurrentEntry!.Id).IsEqualTo("lang");

        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Left));
        await Assert.That(panel.PageIndex).IsEqualTo(0);
        await Assert.That(panel.Index).IsEqualTo(0);

        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Left));
        await Assert.That(panel.PageIndex).IsEqualTo(0);
    }

    [Test]
    public async Task PageUpPageDown_AlsoSwitchPages()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null,
            GeneralPage(), Page("lang", "Language", Group("Locale", SettingEntry.Text("lang", "Language"))));

        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.PageDown));
        await Assert.That(panel.PageIndex).IsEqualTo(1);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.PageUp));
        await Assert.That(panel.PageIndex).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyPanel_NavigatesAndPaintsWithoutEntries()
    {
        var panel = Panel(_ => string.Empty, (_, _) => { });
        var buffer = TestRender.Buffer(20, 4);

        await Assert.That(panel.PageCount).IsEqualTo(0);
        await Assert.That(panel.CurrentEntry).IsNull();
        await Assert.That(panel.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsFalse();
        await Assert.That(panel.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(panel.SetPage(3)).IsFalse();
        panel.Paint(buffer, new Rect(0, 0, 20, 4));
        await Assert.That(ScreenFixtures.Row(buffer, 1, 20).TrimEnd()).IsEqualTo("No settings");
    }

    [Test]
    public async Task HomeEnd_ClampCursorToPage()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null, GeneralPage());

        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(panel.Index).IsEqualTo(2);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Down));
        await Assert.That(panel.Index).IsEqualTo(2);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Home));
        await Assert.That(panel.Index).IsEqualTo(0);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(panel.Index).IsEqualTo(0);
    }

    // ----------------------------------------------------------- activation --

    [Test]
    public async Task Toggle_EnterAndSpace_CommitThroughHost_AndValidationGetsPreparedEntry()
    {
        var values = Values();
        var commits = new List<(string Id, string Value)>();
        var seen = new List<SettingEntry>();
        var panel = Panel(
            e => values[e.Id],
            (e, v) => { commits.Add((e.Id, v)); values[e.Id] = v; },
            (e, v) => { seen.Add(e); return null; },
            GeneralPage());

        _ = panel.MoveTo(2);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(values["bell"]).IsEqualTo("1");
        _ = panel.HandleKey(KeyEvent.Char(new Rune(' ')));
        await Assert.That(values["bell"]).IsEqualTo("0");
        await Assert.That(commits.Count).IsEqualTo(2);
        await Assert.That(commits[0].Id).IsEqualTo("bell");
        await Assert.That(commits[0].Value).IsEqualTo("1");
        await Assert.That(commits[1].Value).IsEqualTo("0");
        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0]).IsSameReferenceAs(panel.CurrentEntry!);
    }

    [Test]
    public async Task Choice_CyclesOptions_AndWraps()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null, GeneralPage());

        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(values["theme"]).IsEqualTo("light");
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(values["theme"]).IsEqualTo("system");
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(values["theme"]).IsEqualTo("dark");
    }

    [Test]
    public async Task Text_EnterOpensDraft_CharsAndBackspaceEditIt_EscapeCancels()
    {
        var values = Values();
        var commits = new List<string>();
        var panel = Panel(e => values[e.Id], (e, v) => { commits.Add(v); values[e.Id] = v; }, null, GeneralPage());

        _ = panel.MoveTo(1);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(panel.IsEditing).IsTrue();
        await Assert.That(panel.Draft).IsEqualTo("14");

        _ = panel.HandleKey(KeyEvent.Char(new Rune('x')));
        await Assert.That(panel.Draft).IsEqualTo("14x");
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Backspace));
        await Assert.That(panel.Draft).IsEqualTo("14");

        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Escape));
        await Assert.That(panel.IsEditing).IsFalse();
        await Assert.That(panel.Draft).IsEmpty();
        await Assert.That(values["font"]).IsEqualTo("14");
        await Assert.That(commits).IsEmpty();
    }

    [Test]
    public async Task Text_CommitValidates_ErrorKeepsDraft_UntilRetrySucceeds()
    {
        var values = Values();
        values["font"] = string.Empty;
        var commits = new List<string>();
        var seen = new List<(string Id, string Value)>();
        var panel = Panel(
            e => values[e.Id],
            (e, v) => { commits.Add(v); values[e.Id] = v; },
            (e, v) => { seen.Add((e.Id, v)); return v.Length == 3 ? "Too short." : null; },
            GeneralPage());

        _ = panel.MoveTo(1);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        foreach (char c in "abc")
        {
            _ = panel.HandleKey(KeyEvent.Char(new Rune(c)));
        }
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(panel.Error).IsEqualTo("Too short.");
        await Assert.That(panel.IsEditing).IsTrue();
        await Assert.That(panel.Draft).IsEqualTo("abc");
        await Assert.That(commits).IsEmpty();
        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0].Id).IsEqualTo("font");
        await Assert.That(seen[0].Value).IsEqualTo("abc");

        for (int i = 0; i < 3; i++)
        {
            _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Backspace));
        }
        foreach (char c in "42")
        {
            _ = panel.HandleKey(KeyEvent.Char(new Rune(c)));
        }
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(panel.Error).IsNull();
        await Assert.That(panel.IsEditing).IsFalse();
        await Assert.That(commits).IsEquivalentTo(new[] { "42" });
        await Assert.That(values["font"]).IsEqualTo("42");
    }

    [Test]
    public async Task RejectedToggle_DoesNotCallOnChange()
    {
        var values = Values();
        int commits = 0;
        var panel = Panel(
            e => values[e.Id],
            (e, v) => { commits++; values[e.Id] = v; },
            (_, _) => "Locked.",
            GeneralPage());

        _ = panel.MoveTo(2);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(values["bell"]).IsEqualTo("0");
        await Assert.That(commits).IsEqualTo(0);
        await Assert.That(panel.Error).IsEqualTo("Locked.");
    }

    // ------------------------------------------------------------- guarding --

    [Test]
    public async Task ShiftIsRefusedInCommandMode_ButTypesInEditMode()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null, GeneralPage());

        await Assert.That(panel.HandleKey(KeyEvent.Char(new Rune('x'), KeyModifiers.Shift))).IsFalse();
        await Assert.That(panel.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(panel.HandleKey(KeyEvent.Char(new Rune(' '), KeyModifiers.Shift))).IsFalse();

        _ = panel.MoveTo(1);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(panel.HandleKey(KeyEvent.Char(new Rune('X'), KeyModifiers.Shift))).IsTrue();
        await Assert.That(panel.Draft).IsEqualTo("14X");
        await Assert.That(panel.HandleKey(KeyEvent.Char(new Rune('y'), KeyModifiers.Ctrl))).IsFalse();
    }

    [Test]
    public async Task ReleaseChordsAndDisabled_AreRefused()
    {
        var values = Values();
        int validates = 0;
        var panel = Panel(
            e => values[e.Id],
            (e, v) => values[e.Id] = v,
            (_, _) => { validates++; return null; },
            GeneralPage());

        await Assert.That(panel.HandleKey(new KeyEvent(
            KeyCode.Enter, default, KeyModifiers.None, KeyEventType.Release, false))).IsFalse();
        await Assert.That(validates).IsEqualTo(0);

        panel.Disabled = true;
        await Assert.That(panel.CanFocus).IsFalse();
        await Assert.That(panel.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(panel.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsFalse();
        await Assert.That(panel.HandleKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        await Assert.That(validates).IsEqualTo(0);
    }

    [Test]
    public async Task FocusRouter_DrivesFocus_AndChangedRaisesOnEdits()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null, GeneralPage());
        var router = new FocusRouter();
        router.Add(panel);
        int changes = 0;
        panel.Changed += (_, _) => changes++;

        await Assert.That(router.Next()).IsTrue();
        await Assert.That(router.Current).IsSameReferenceAs(panel);
        await Assert.That(panel.IsFocused).IsTrue();
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Down));

        await Assert.That(changes).IsEqualTo(3); // focus, activate choice, move
        await Assert.That(router.FocusById("settings")).IsTrue();
        await Assert.That(router.Remove("settings")).IsTrue();
        await Assert.That(panel.IsFocused).IsFalse();
    }

    // --------------------------------------------------------------- golden --

    [Test]
    public async Task Golden_TabsGroupsAndEntryRows()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null,
            GeneralPage(), Page("lang", "Language", Group("Locale", SettingEntry.Text("lang", "Language"))));
        var buffer = TestRender.Buffer(32, 6);

        panel.Paint(buffer, new Rect(0, 0, 32, 6));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 32).TrimEnd()).IsEqualTo(">General   Language");
        await Assert.That(ScreenFixtures.Row(buffer, 1, 32).TrimEnd()).IsEqualTo("## Display");
        await Assert.That(ScreenFixtures.Row(buffer, 2, 32).TrimEnd()).IsEqualTo("> Theme: dark");
        await Assert.That(ScreenFixtures.Row(buffer, 3, 32).TrimEnd()).IsEqualTo("  Font: 14");
        await Assert.That(ScreenFixtures.Row(buffer, 4, 32).TrimEnd()).IsEqualTo("## Alerts");
        await Assert.That(ScreenFixtures.Row(buffer, 5, 32).TrimEnd()).IsEqualTo("  Bell: [ ]");
    }

    [Test]
    public async Task Golden_ToggleAndChoiceValues_UseGlyphAndText()
    {
        var values = Values();
        values["bell"] = "1";
        values["theme"] = "light";
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null, GeneralPage());
        _ = panel.MoveTo(2);
        var buffer = TestRender.Buffer(24, 6);

        panel.Paint(buffer, new Rect(0, 0, 24, 6));

        await Assert.That(ScreenFixtures.Row(buffer, 2, 24).TrimEnd()).IsEqualTo("  Theme: light");
        await Assert.That(ScreenFixtures.Row(buffer, 5, 24).TrimEnd()).IsEqualTo("> Bell: [x]");
        await Assert.That((buffer.Get(0, 5).Style.Attrs & StyleAttr.Bold) != 0).IsTrue();
    }

    [Test]
    public async Task Golden_ValidationError_AddsRowUnderRejectedEntry()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, (_, _) => "Locked.", GeneralPage());
        _ = panel.MoveTo(2);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        var buffer = TestRender.Buffer(28, 7);

        panel.Paint(buffer, new Rect(0, 0, 28, 7));

        await Assert.That(ScreenFixtures.Row(buffer, 5, 28).TrimEnd()).IsEqualTo("> Bell: [ ]");
        await Assert.That(ScreenFixtures.Row(buffer, 6, 28).TrimEnd()).IsEqualTo("  ! Locked.");
    }

    [Test]
    public async Task Golden_EditingRow_PaintsDraftWithCaret()
    {
        var values = Values();
        var panel = Panel(e => values[e.Id], (e, v) => values[e.Id] = v, null, GeneralPage());
        _ = panel.MoveTo(1);
        _ = panel.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        var buffer = TestRender.Buffer(24, 4);

        panel.Paint(buffer, new Rect(0, 0, 24, 4));

        await Assert.That(ScreenFixtures.Row(buffer, 3, 24).TrimEnd()).IsEqualTo("> Font: 14_");
    }
}
