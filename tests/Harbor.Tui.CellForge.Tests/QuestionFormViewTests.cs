using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [PRIM13] inline question form (#309): tabbed batch of multiselect /
/// single-select option lists plus a free-text custom row, seated inline in
/// the chat timeline (kilocode QuestionDock / crush batch-form pattern).
/// Pure state transitions plus paint smoke — no backends.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class QuestionFormViewTests
{
    private static QuestionFormView TwoQuestions() => new(
    [
        new QuestionItem(
            "scope",
            "What to change?",
            [new("code"), new("tests"), new("docs")],
            multiSelect: true),
        new QuestionItem(
            "mode",
            "Pick one:",
            [new("fast"), new("safe")],
            multiSelect: false),
    ]);

    private static KeyEvent CharKey(char c) => KeyEvent.Char(new Rune(c));

    private static void TypeCustom(QuestionFormView form, string text)
    {
        foreach (char c in text)
        {
            _ = form.HandleKey(CharKey(c));
        }
    }

    private static string PaintArt(QuestionFormView form, int cols = 60)
    {
        int rows = form.Measure(cols).BestGuess;
        var buffer = new ScreenBuffer(cols, rows);
        form.Paint(new BlockPaintContext(buffer, new Rect(0, 0, cols, rows), 0));
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task Measure_HeightIdentical_AfterSubmit()
    {
        var form = TwoQuestions();
        int before = form.Measure(60).BestGuess;

        await Assert.That(form.TrySubmit()).IsTrue();
        await Assert.That(form.IsPending).IsFalse();
        await Assert.That(form.Measure(60).BestGuess).IsEqualTo(before);
    }

    [Test]
    public async Task SelectQuestion_NextPrev_WrapAround()
    {
        var form = TwoQuestions();
        await Assert.That(form.SelectedQuestion).IsEqualTo(0);

        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(form.SelectedQuestion).IsEqualTo(1);

        // Wraps past the last tab.
        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(form.SelectedQuestion).IsEqualTo(0);

        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(form.SelectedQuestion).IsEqualTo(1);

        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Home))).IsTrue();
        await Assert.That(form.SelectedQuestion).IsEqualTo(0);
    }

    [Test]
    public async Task Digits_JumpTabs_WhenOnOptionRow()
    {
        var form = TwoQuestions();
        await Assert.That(form.HandleKey(CharKey('2'))).IsTrue();
        await Assert.That(form.SelectedQuestion).IsEqualTo(1);
    }

    [Test]
    public async Task Toggle_MultiSelect_TogglesEachOption()
    {
        var form = TwoQuestions();

        await Assert.That(form.HandleKey(CharKey(' '))).IsTrue();
        await Assert.That(form.SelectedOptions(0).Count).IsEqualTo(1);

        _ = form.MoveCursor(1);
        await Assert.That(form.HandleKey(CharKey(' '))).IsTrue();
        await Assert.That(form.SelectedOptions(0).Count).IsEqualTo(2);

        // Toggle off again.
        await Assert.That(form.HandleKey(CharKey(' '))).IsTrue();
        await Assert.That(form.SelectedOptions(0).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Toggle_SingleSelect_IsExclusive()
    {
        var form = TwoQuestions();
        _ = form.SelectQuestion(1);

        await Assert.That(form.HandleKey(CharKey(' '))).IsTrue();
        await Assert.That(form.SelectedOptions(1).Count).IsEqualTo(1);

        _ = form.MoveCursor(1);
        await Assert.That(form.HandleKey(CharKey(' '))).IsTrue();
        var selected = form.SelectedOptions(1);
        await Assert.That(selected.Count).IsEqualTo(1);
        await Assert.That(selected[0]).IsEqualTo(1);
    }

    [Test]
    public async Task CustomRow_Typing_Appends_Digits_And_Letters()
    {
        var form = TwoQuestions();
        // Three options + custom row: clamp lands on the custom row.
        _ = form.MoveCursor(99);
        await Assert.That(form.CursorAt(0)).IsEqualTo(3);

        // hjkl, digits and space are answer text here — not nav/tab-jump.
        TypeCustom(form, "hello 42");
        await Assert.That(form.CustomAnswer(0)).IsEqualTo("hello 42");
        await Assert.That(form.SelectedQuestion).IsEqualTo(0);
        await Assert.That(form.SelectedOptions(0).Count).IsEqualTo(0);
    }

    [Test]
    public async Task CustomRow_Backspace_DeletesRuneAware()
    {
        var form = TwoQuestions();
        _ = form.MoveCursor(99);

        // Prefill avoids Rune(char) on lone surrogates; backspace must still
        // delete the astral pair as one unit.
        await Assert.That(form.SetCustomAnswer(0, "a\uD83D\uDE00")).IsTrue();

        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Backspace))).IsTrue();
        await Assert.That(form.CustomAnswer(0)).IsEqualTo("a");

        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Backspace))).IsTrue();
        await Assert.That(form.CustomAnswer(0)).IsEqualTo(string.Empty);

        // Empty custom: nothing to delete.
        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Backspace))).IsFalse();
    }

    [Test]
    public async Task OptionRow_Typing_NotConsumed_ByComposer()
    {
        var form = TwoQuestions();
        await Assert.That(form.HandleKey(CharKey('x'))).IsFalse();
        await Assert.That(form.CustomAnswer(0)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task HandleKey_Ignores_Escape_Modifiers_And_Release()
    {
        var form = TwoQuestions();
        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsFalse();
        await Assert.That(form.HandleKey(KeyEvent.Char(new Rune('y'), KeyModifiers.Ctrl))).IsFalse();

        var release = new KeyEvent(KeyCode.Char, new Rune(' '), KeyModifiers.None, KeyEventType.Release, false);
        await Assert.That(form.HandleKey(release)).IsFalse();
        await Assert.That(form.IsPending).IsTrue();
    }

    [Test]
    public async Task Submit_Once_FiresEvent_And_FreezesKeys()
    {
        var form = TwoQuestions();
        int fired = 0;
        form.Submitted += (_, _) => fired++;

        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(fired).IsEqualTo(1);

        // One-shot: no second signal, keys frozen.
        await Assert.That(form.TrySubmit()).IsFalse();
        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(form.SelectQuestion(1)).IsFalse();
        await Assert.That(form.SetCustomAnswer(0, "late")).IsFalse();
        await Assert.That(fired).IsEqualTo(1);
    }

    [Test]
    public async Task GetAnswers_Snapshots_Labels_And_Custom()
    {
        var form = TwoQuestions();
        _ = form.ToggleAtCursor();
        _ = form.MoveCursor(99);
        TypeCustom(form, "other");

        var answers = form.GetAnswers();
        await Assert.That(answers.Count).IsEqualTo(2);
        await Assert.That(answers[0].SelectedLabels.Length).IsEqualTo(1);
        await Assert.That(answers[0].SelectedLabels[0]).IsEqualTo("code");
        await Assert.That(answers[0].CustomAnswer).IsEqualTo("other");
        await Assert.That(answers[1].CustomAnswer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Paint_Pending_ShowsHeader_Strip_Options_AndHint()
    {
        var form = TwoQuestions();
        string art = PaintArt(form);

        await Assert.That(art).Contains("? question");
        await Assert.That(art).Contains("[1 scope] │ 2 mode");
        await Assert.That(art).Contains("[ ] code");
        await Assert.That(art).Contains("✎ custom:");
        await Assert.That(art).Contains("[tab] switch");
    }

    [Test]
    public async Task Paint_Submitted_StampsOutcome_KeepingAudit()
    {
        var form = TwoQuestions();
        _ = form.ToggleAtCursor();
        _ = form.TrySubmit();

        string art = PaintArt(form);
        await Assert.That(art).Contains("✓ submitted");
        await Assert.That(art).Contains("[x] code");
        await Assert.That(art.Contains("[tab] switch")).IsFalse();
    }

    [Test]
    public async Task Paint_Focused_ShowsFocusRail()
    {
        var form = TwoQuestions();
        form.OnFocusChanged(true);
        await Assert.That(PaintArt(form)).Contains("▸");
    }

    [Test]
    public async Task Paint_SingleQuestion_HasNoStrip()
    {
        var form = new QuestionFormView([new QuestionItem("solo", "Only:", [new("yes")])]);
        await Assert.That(PaintArt(form).Contains("│")).IsFalse();
    }

    [Test]
    public async Task Paint_Empty_ShowsPlaceholder()
    {
        var form = new QuestionFormView([]);
        await Assert.That(form.Measure(60).MinLines).IsEqualTo(1);
        await Assert.That(PaintArt(form)).Contains("(empty)");
        await Assert.That(form.TrySubmit()).IsFalse();
    }

    [Test]
    public async Task RawText_ContainsLabels_AndState()
    {
        var form = TwoQuestions();
        await Assert.That(form.RawText()).Contains("scope");
        await Assert.That(form.TrySubmit()).IsTrue();
        await Assert.That(form.RawText()).Contains("submitted");
    }

    [Test]
    public async Task Ids_AreUnique_PerForm()
    {
        var a = TwoQuestions();
        var b = TwoQuestions();
        await Assert.That(a.Id).IsNotEqualTo(b.Id);
    }
}
