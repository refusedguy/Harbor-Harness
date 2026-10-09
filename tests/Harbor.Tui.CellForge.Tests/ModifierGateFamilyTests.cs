using System.Text;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #785: "who refuses Shift+char" is two families, not one rule with one
/// dissenter — and the family a site belongs to is decided by whether it has
/// a TEXT BUFFER, not by how its predicate is spelled.
///
/// The issue counted four sites and called it "3 to 1". Reading the widgets
/// rather than the predicates splits them:
///
///   TEXT BUFFER — a rune can be appended to something the user is filling in.
///     - <c>src/Harbor.Tui.CellForge/Chat/Rendering/ComposerController.cs:193</c>   composer body          (Ctrl|Meta|Alt)
///     - <c>DialogOverlay.cs:632</c>       reject reason, prompt, multiline (Ctrl|Meta|Alt)
///     - <c>FilePickerView.cs:153</c>, <c>CommandPaletteView.cs:254</c>  (None or Shift)
// check-doc-cites: record-drift CommandPaletteView.cs:254 now="case KeyCode.Char when key.Modifiers.AcceptsTypedChar():" [#947: written over `case KeyCode.Char when key.Modifiers is `; repair deferred to the owner's symbol-rename decision] -->
// check-doc-cites: record-drift FilePickerView.cs:153 now="case KeyCode.Char when key.Modifiers.AcceptsTypedChar():" [#947: written over `case KeyCode.Char when key.Modifiers is `; repair deferred to the owner's symbol-rename decision] -->
///     - <c>QuestionFormView.cs:498</c>    custom answer         (!= None)  <-- the defect
///
///   COMMAND ONLY — no buffer; runes are navigation (h/j/k/l) or a 3-letter
///   vote (y/n/a). Shift changes nothing a user could want here.
///     - <c>ApprovalGateView.cs:254</c>    y/n/a + Enter/Escape
// check-doc-cites: record-drift ApprovalGateView.cs:254 now="if (!key.Modifiers.IsUnmodified())" [#947: written over `if (key.Modifiers != KeyModifiers.None)`; repair deferred to the owner's symbol-rename decision] -->
///     - <c>TreeView.cs:269</c>, <c>Tabs.cs:143</c>, <c>ToolCardTracker.cs:624</c>
// check-doc-cites: record-drift ToolCardTracker.cs:624 now="|| !key.Modifiers.IsUnmodified())" [#947: written over `|| key.Modifiers != KeyModifiers.None)`; repair deferred to the owner's symbol-rename decision] -->
// check-doc-cites: record-drift Tabs.cs:143 now="|| !key.Modifiers.IsUnmodified())" [#947: written over `|| key.Modifiers != KeyModifiers.None)`; repair deferred to the owner's symbol-rename decision] -->
// check-doc-cites: record-drift TreeView.cs:269 now="|| !key.Modifiers.IsUnmodified())" [#947: written over `|| key.Modifiers != KeyModifiers.None)`; repair deferred to the owner's symbol-rename decision] -->
///     - <c>DiffViewerOverlay.cs:210</c>, <c>ImageViewerOverlay.cs:124</c>
// check-doc-cites: record-drift ImageViewerOverlay.cs:124 now="case KeyCode.Left:" [#947: written over `if ((key.Modifiers & (KeyModifiers.Ctrl `; repair deferred to the owner's symbol-rename decision] -->
// check-doc-cites: record-drift DiffViewerOverlay.cs:210 now="if (key.Modifiers.IsCommandModifier())" [#947: written over `if ((key.Modifiers & (ConsoleModifiers.C`; repair deferred to the owner's symbol-rename decision] -->
///
/// So <c>ApprovalGateView</c> is not the dissenter the issue named: it holds
/// no buffer, its letters are case-folded (<c>Rune.ToUpperInvariant</c>) so a
/// user presses <c>y</c> and <c>Shift+Y</c> is merely a redundant gesture it
/// declines — and its own doc says "no modifiers". The tally's premise (a user
/// typing a rejection reason) points at <c>DialogOverlay</c>, which already
/// admits Shift. The site that really matches that premise is
/// <c>QuestionFormView</c>, whose custom row IS a text buffer ("printable
/// chars + Backspace edit the custom answer", its own doc) and which was not
/// on the list at all.
///
/// WHY THE TEXT SITES MATTER AND THE COMMAND SITES DO NOT
///
/// For a buffer, dropping the rune loses data the user typed. For a command,
/// the strict gate is free: the same intent is reachable unshifted, so nothing
/// is lost. That asymmetry is why the two predicates are both correct and why
/// "make all four agree" was the wrong instruction — it would have forced the
/// command sites to start honouring <c>Ctrl+A</c>/<c>Ctrl+Y</c> and hand
/// approval to the readline chords.
///
/// This file holds the split in place from both sides: the buffer must type a
/// capital, and the command sites must KEEP refusing Shift so a future fix
/// cannot buy the first by widening the second.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class ModifierGateFamilyTests
{
    /// <summary>
    /// The gesture that separates the families. Real encoders deliver a capital
    /// as the capital rune PLUS the Shift modifier, so this is what a user
    /// pressing <c>Shift+A</c> actually produces — both halves have to survive,
    /// and dropping the modifier is what makes the row untypable.
    /// </summary>
    private static KeyEvent ShiftCapitalA() => KeyEvent.Char(new Rune('A'), KeyModifiers.Shift);

    private static QuestionFormView OnCustomRow()
    {
        var form = new QuestionFormView(
        [
            new QuestionItem(
                "scope",
                "What to change?",
                [new("code"), new("tests"), new("docs")],
                multiSelect: true),
        ]);

        // Three options + custom row: the cursor clamps onto the custom row.
        _ = form.MoveCursor(99);
        return form;
    }

    private static QuestionFormView OnOptionRow() => new(
    [
        new QuestionItem(
            "scope",
            "What to change?",
            [new("code"), new("tests"), new("docs")],
            multiSelect: true),
    ]);

    [Test]
    public async Task Text_Buffer_Site_Types_A_Capital_Under_Shift()
    {
        var form = OnCustomRow();

        bool handled = form.HandleKey(ShiftCapitalA());

        await Assert.That(handled)
            .IsTrue()
            .Because("the custom row is a text buffer, and Shift is a case-shaper, not a command modifier");
        await Assert.That(form.CustomAnswer(0)).IsEqualTo("A")
            .Because("a capital the user typed may not be swallowed by the gate");
    }

    [Test]
    public async Task Text_Buffer_Site_Still_Refuses_Ctrl_Alt_And_Meta()
    {
        // The control that keeps the fix honest: the gate must MOVE to
        // (Ctrl|Meta|Alt), not be deleted. Without this row "Shift+A types"
        // would also be satisfied by a widget that ignores modifiers entirely,
        // which would let Ctrl+A fall through as the character 'A'.
        KeyModifiers[] commands =
        [
            KeyModifiers.Ctrl,
            KeyModifiers.Alt,
            KeyModifiers.Meta,
        ];

        foreach (KeyModifiers mods in commands)
        {
            var form = OnCustomRow();
            bool handled = form.HandleKey(KeyEvent.Char(new Rune('A'), mods));
            await Assert.That(handled).IsFalse().Because($"{mods} is a command modifier, not a case-shaper");
            await Assert.That(form.CustomAnswer(0)).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task Text_Buffer_Site_Still_Types_Without_Any_Modifier()
    {
        // The other control: green must mean "Shift got through", not "the row
        // refuses everything".
        var form = OnCustomRow();

        bool handled = form.HandleKey(KeyEvent.Char(new Rune('a')));

        await Assert.That(handled).IsTrue();
        await Assert.That(form.CustomAnswer(0)).IsEqualTo("a");
    }

    [Test]
    public async Task Command_Only_Sites_Keep_Refusing_Shift_Char()
    {
        // The other half of the split, and pinned HERE rather than asserted as a
        // house rule: this row exists so the buffer fix below cannot be bought
        // by widening these gates. Loosening them would let the readline
        // chords (Ctrl+Y / Ctrl+A) decide and navigate an approval.
        var breaks = new List<string>();

        foreach ((string name, bool handled) in CommandSites(KeyModifiers.Shift))
        {
            if (handled)
            {
                breaks.Add(name);
            }
        }

        await Assert.That(string.Join(", ", breaks))
            .IsEqualTo(string.Empty)
            .Because("a command-only widget must not consume Shift+char");
    }

    [Test]
    public async Task Command_Only_Sites_Still_Consume_The_Unmodified_Char()
    {
        // Control for the control: the strict sites must be refusing Shift
        // because they require "no modifier", not because they ignore runes.
        var breaks = new List<string>();
        foreach ((string name, bool handled) in CommandSites(KeyModifiers.None))
        {
            if (!handled)
            {
                breaks.Add(name);
            }
        }

        await Assert.That(string.Join(", ", breaks))
            .IsEqualTo(string.Empty)
            .Because("the unmodified rune is the gesture these widgets exist for");
    }

    [Test]
    public async Task QuestionForm_Option_Row_Keeps_The_Command_Gate()
    {
        // QuestionFormView is one widget wearing both families: the custom row
        // is a buffer, the option rows are navigation (digits jump tabs, hjkl
        // move, space toggles). Only the buffer row may admit Shift.
        var form = OnOptionRow();

        bool handled = form.HandleKey(ShiftCapitalA());

        await Assert.That(handled).IsFalse()
            .Because("off the custom row the rune is navigation, not answer text");
        await Assert.That(form.SelectedOptions(0).Count).IsEqualTo(0);
        await Assert.That(form.CustomAnswer(0)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The command-only sites, each probed with a rune it genuinely consumes
    /// (each folds case via <c>Rune.ToUpperInvariant</c>, so Shift is the only
    /// thing standing between these and a Shift gesture). Every entry builds a
    /// fresh widget so one decision cannot leak into the next probe.
    /// </summary>
    private static (string Name, bool Handled)[] CommandSites(KeyModifiers mods)
    {
        KeyEvent Gate(char c) => KeyEvent.Char(new Rune(c), mods);

        return
        [
            ("ApprovalGateView(a)", new ApprovalGateView("bash", "ls -la").HandleKey(Gate('a'))),
            ("TreeView(j)", new TreeView([new TreeNode("root", [])]).HandleKey(Gate('j'))),
            ("Tabs(h)", new Tabs(["one", "two"]).HandleKey(Gate('h'))),
        ];
    }
}