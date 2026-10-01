using System.Text;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [GoF-A2] #473: <c>DialogOverlay</c>'s two <c>HandleKey</c> overloads are ONE
/// transition table written twice, and nothing compares the two copies.
///
/// THE COUNT, AND WHY 41 IS NOT WHAT IT LOOKS LIKE
///
/// The 41 dispatch points of the <c>_kind</c> enum do not describe 41
/// independent decisions. Sorted by what they are:
///   - 7 writers — the <c>Show*</c> methods that set <c>_kind</c>.
///   - 10 reader branches — <c>Input</c>, <c>Options</c>, <c>SelectedIndex</c>,
///     <c>SelectedOption</c>, <c>ApprovalTool</c>, <c>ApprovalFilePath</c>,
///     <c>RejectReason</c>.
///   - 5 paint branches — <c>Paint</c>'s if-chain (lines 883..901).
///   - 6 layout arms — <c>ControlRows</c> (line 984).
///   - 3 + 22 — key routing. The legacy overload dispatches in two steps
///     (lines 444, 449, then the 5-way <c>_kind</c> switch at 464 into
///     <c>HandlePromptKey</c>/<c>HandleSelectKey</c>/<c>HandleRadioKey</c>/
///     <c>HandleApprovalKey</c>/<c>HandleMultilineKey</c>); the kitty overload
///     inlines the SAME table key-major as 22 flat <c>if (_kind == …)</c>
///     branches (lines 499..645).
///
/// So 25 of the 41 are the same state x key -> transition table, written
/// twice, in two different shapes. The states are not the cause either: the
/// five kinds collapse onto two axes (has-a-choice-list: Select/Radio, and
/// has-a-text-buffer: Prompt/Multiline/Approval), and Approval is documented
/// in the class itself as "no new widgets, only assembly" of Radio +
/// Multiline (lines 13-16). A decomposition into "kinds" would therefore be
/// splitting a number, not a job. What is actually missing is the ONE place
/// where the two copies of the table are held to each other — so this file is
/// that place, and it is a differential comparison rather than a written-out
/// table, because a hand-written table would only describe the drift again.
///
/// THE RULE, QUOTED FROM THE CODE THAT CLAIMS IT
///
/// 1. <c>HandleKey(in KeyEvent)</c>'s own doc, line 476: "Mirrors the
///    ConsoleKeyInfo contract, plus Shift/Alt+Enter inserts a newline".
///    The one documented addition is excluded from the matrix below
///    (Multiline/Approval x Shift|Alt+Enter) because the legacy
///    <c>ConsoleKeyInfo</c> cannot express it. Everything else must match.
/// 2. <c>ComposerController.cs:173</c> types a char only when
// check-doc-cites: record-drift ComposerController.cs:173 now="if (mods.AcceptsTypedChar())" [#947: written over `if ((mods & (KeyModifiers.Ctrl | KeyModi`; repair deferred to the owner's symbol-rename decision] -->
///    <c>(mods & (Ctrl|Meta|Alt)) == 0</c>; <c>DiffViewerOverlay.cs:210</c>
// check-doc-cites: record-drift DiffViewerOverlay.cs:210 now="if (key.Modifiers.IsCommandModifier())" [#947: written over `if ((key.Modifiers & (ConsoleModifiers.C`; repair deferred to the owner's symbol-rename decision] -->
///    refuses <c>Control|Alt</c> and lets <c>Shift</c> through;
///    <c>ApprovalGateView.cs:256</c> refuses any modifier.
///    <c>DialogOverlay</c>'s kitty overload copies the composer gate
///    verbatim at line 632 — and the legacy overload cannot, because
///    <c>ConsoleKeyInfo.Modifiers</c> is read NOWHERE in the file. That
///    asymmetry is the defect.
///
/// The red this lands on is that asymmetry, not a refactor: the same gesture
/// is accepted by one overload and swallowed by the other.
/// </summary>
public class DialogKeyParityTests
{
    private static readonly DialogKind[] Kinds =
    [
        DialogKind.Alert,
        DialogKind.Confirm,
        DialogKind.Prompt,
        DialogKind.Select,
        DialogKind.Radio,
        DialogKind.Multiline,
        DialogKind.Approval,
    ];

    /// <summary>
    /// One gesture in both encodings. Alt and Ctrl are the interesting rows:
    /// <c>ctrl_a</c> is what <c>Console.ReadKey</c> produces (the control code
    /// lands in <c>KeyChar</c>, so <c>char.IsControl</c> alone happens to
    /// refuse it), while <c>ctrl_a_literal</c> is the printable-<c>KeyChar</c>
    /// encoding a host that forwards the modifier separately produces. Testing
    /// only the first would pass for the wrong reason — the gate that saves it
    /// is <c>IsControl</c>, not the modifier the rule is actually about.
    /// <c>Meta</c> has no <c>ConsoleKeyInfo</c> slot, so it is not in the
    /// matrix: it cannot be expressed on the legacy side at all.
    /// </summary>
    private static (string Name, ConsoleKeyInfo Legacy, KeyEvent Kitty)[] Gestures() =>
    [
        ("escape", new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false), KeyEvent.Simple(KeyCode.Escape)),
        ("tab", new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false), KeyEvent.Simple(KeyCode.Tab)),
        ("left", new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false), KeyEvent.Simple(KeyCode.Left)),
        ("right", new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false), KeyEvent.Simple(KeyCode.Right)),
        ("up", new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false), KeyEvent.Simple(KeyCode.Up)),
        ("down", new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), KeyEvent.Simple(KeyCode.Down)),
        ("page_up", new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false), KeyEvent.Simple(KeyCode.PageUp)),
        ("page_down", new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false), KeyEvent.Simple(KeyCode.PageDown)),
        ("home", new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false), KeyEvent.Simple(KeyCode.Home)),
        ("end", new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false), KeyEvent.Simple(KeyCode.End)),
        ("backspace", new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false), KeyEvent.Simple(KeyCode.Backspace)),
        ("delete", new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false), KeyEvent.Simple(KeyCode.Delete)),
        ("enter", new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), KeyEvent.Simple(KeyCode.Enter)),
        ("char_a", new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false), KeyEvent.Char(new Rune('a'))),
        ("shift_a", new ConsoleKeyInfo('a', ConsoleKey.A, true, false, false), KeyEvent.Char(new Rune('a'), KeyModifiers.Shift)),
        ("alt_a", new ConsoleKeyInfo('a', ConsoleKey.A, false, true, false), KeyEvent.Char(new Rune('a'), KeyModifiers.Alt)),
        ("ctrl_a", new ConsoleKeyInfo('\x01', ConsoleKey.A, false, false, true), KeyEvent.Char(new Rune('a'), KeyModifiers.Ctrl)),
        ("ctrl_a_literal", new ConsoleKeyInfo('a', ConsoleKey.A, false, false, true), KeyEvent.Char(new Rune('a'), KeyModifiers.Ctrl)),
    ];

    /// <summary>
    /// A dialog in each kind, seeded so the transitions have something to move:
    /// a non-empty buffer for the editing kinds, three options at index 1 for
    /// the choice kinds, and a diff for the approval modal.
    /// </summary>
    private static DialogOverlay Shown(DialogKind kind)
    {
        var dialog = new DialogOverlay();
        switch (kind)
        {
            case DialogKind.Alert:
                dialog.ShowAlert("title", "message");
                break;
            case DialogKind.Confirm:
                dialog.ShowConfirm("title", "message");
                break;
            case DialogKind.Prompt:
                dialog.ShowPrompt("title", "message", "seed");
                break;
            case DialogKind.Select:
                dialog.ShowSelect("title", "message", ["one", "two", "three"], 1);
                break;
            case DialogKind.Radio:
                dialog.ShowRadio("title", "message", ["one", "two", "three"], 1);
                break;
            case DialogKind.Multiline:
                dialog.ShowMultiline("title", "message", "one\ntwo");
                break;
            case DialogKind.Approval:
                dialog.ShowApproval("bash", "rm -rf?", "+added\n-removed", "src/x.cs", 1);
                break;
        }
        return dialog;
    }

    private readonly record struct Snapshot(
        bool Visible,
        DialogKind Kind,
        string Input,
        string EditorText,
        int SelectedIndex,
        int FocusedButton);

    private static Snapshot Snap(DialogOverlay dialog) => new(
        dialog.Visible,
        dialog.Kind,
        dialog.Input,
        dialog.Editor.Text,
        dialog.SelectedIndex,
        dialog.FocusedButtonIndex);

    /// <summary>
    /// Describes every observable the two overloads must agree on, or empty
    /// when they agree. <c>Input</c> and <c>EditorText</c> are both needed:
    /// <c>Input</c> reads the prompt buffer for Prompt and the editor for
    /// Multiline, and never the editor for Approval — the reject reason is
    /// only visible through <c>EditorText</c>.
    /// </summary>
    private static string Break(
        string kind,
        string gesture,
        Snapshot legacy,
        bool legacyHandled,
        Snapshot kitty,
        bool kittyHandled)
    {
        var parts = new List<string>();
        if (legacy.Visible != kitty.Visible)
        {
            parts.Add($"visible {legacy.Visible}/{kitty.Visible}");
        }
        if (legacy.Kind != kitty.Kind)
        {
            parts.Add($"kind {legacy.Kind}/{kitty.Kind}");
        }
        if (!string.Equals(legacy.Input, kitty.Input, StringComparison.Ordinal))
        {
            parts.Add($"input '{legacy.Input}'/'{kitty.Input}'");
        }
        if (!string.Equals(legacy.EditorText, kitty.EditorText, StringComparison.Ordinal))
        {
            parts.Add($"editor '{legacy.EditorText}'/'{kitty.EditorText}'");
        }
        if (legacy.SelectedIndex != kitty.SelectedIndex)
        {
            parts.Add($"selected {legacy.SelectedIndex}/{kitty.SelectedIndex}");
        }
        if (legacy.FocusedButton != kitty.FocusedButton)
        {
            parts.Add($"focus {legacy.FocusedButton}/{kitty.FocusedButton}");
        }
        if (legacyHandled != kittyHandled)
        {
            parts.Add($"handled {legacyHandled}/{kittyHandled}");
        }
        return parts.Count == 0 ? string.Empty : $"{kind}/{gesture}: {string.Join(", ", parts)}";
    }

    [Test]
    public async Task BothOverloads_Agree_On_EveryGesture_For_EveryKind()
    {
        var breaks = new List<string>();
        foreach (DialogKind kind in Kinds)
        {
            foreach ((string name, ConsoleKeyInfo legacyKey, KeyEvent kittyKey) in Gestures())
            {
                var legacy = Shown(kind);
                var kitty = Shown(kind);

                bool legacyHandled = legacy.HandleKey(legacyKey);
                bool kittyHandled = kitty.HandleKey(kittyKey);

                string broken = Break(
                    kind.ToString(),
                    name,
                    Snap(legacy),
                    legacyHandled,
                    Snap(kitty),
                    kittyHandled);
                if (broken.Length > 0)
                {
                    breaks.Add(broken);
                }
            }
        }

        await Assert.That(string.Join(" | ", breaks)).IsEqualTo(string.Empty);
    }

    // ── Controls ────────────────────────────────────────────────────────────
    // The parity test is only a guard if Break_ can do both jobs: report a
    // real difference, and stay quiet on a non-difference. A comparator that
    // always reports would be red for the wrong reason and get turned off; one
    // that never reports is green for the wrong reason and protects nothing.

    [Test]
    public async Task Control_Break_Reports_A_Known_Difference()
    {
        var typed = Shown(DialogKind.Prompt);
        var untouched = Shown(DialogKind.Prompt);
        bool typedHandled = typed.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false));

        string broken = Break("Prompt", "control", Snap(typed), typedHandled, Snap(untouched), false);

        await Assert.That(broken).IsNotEqualTo(string.Empty);
        await Assert.That(broken.Contains("input 'seeda'/'seed'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Control_Break_Is_Quiet_When_Both_Overloads_Type()
    {
        var legacy = Shown(DialogKind.Prompt);
        var kitty = Shown(DialogKind.Prompt);

        bool legacyHandled = legacy.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false));
        bool kittyHandled = kitty.HandleKey(KeyEvent.Char(new Rune('a')));

        await Assert.That(Break("Prompt", "control", Snap(legacy), legacyHandled, Snap(kitty), kittyHandled))
            .IsEqualTo(string.Empty);
        // Quiet because both moved — not because the comparator went deaf.
        await Assert.That(legacy.Input).IsEqualTo("seeda");
        await Assert.That(kitty.Input).IsEqualTo("seeda");
    }

    [Test]
    public async Task Control_Shift_Char_Still_Types_By_Both_Overloads()
    {
        // Pins the gate to (Ctrl|Meta|Alt) exactly, as ComposerController.cs:173
        // check-doc-cites: record-drift ComposerController.cs:173 now="if (mods.AcceptsTypedChar())" [#947: written over `if ((mods & (KeyModifiers.Ctrl | KeyModi`; repair deferred to the owner's symbol-rename decision] -->
        // has it — Shift is a case-shaper, not a command modifier. Without this
        // row the rule could be "satisfied" by refusing every modifier, which
        // would break Shift+a in every text field.
        var legacy = Shown(DialogKind.Multiline);
        var kitty = Shown(DialogKind.Multiline);

        bool legacyHandled = legacy.HandleKey(new ConsoleKeyInfo('z', ConsoleKey.A, true, false, false));
        bool kittyHandled = kitty.HandleKey(KeyEvent.Char(new Rune('z'), KeyModifiers.Shift));

        await Assert.That(Break("Multiline", "control", Snap(legacy), legacyHandled, Snap(kitty), kittyHandled))
            .IsEqualTo(string.Empty);
        await Assert.That(legacy.Editor.Text).IsEqualTo("one\ntwoz");
        await Assert.That(kitty.Editor.Text).IsEqualTo("one\ntwoz");
    }
}
