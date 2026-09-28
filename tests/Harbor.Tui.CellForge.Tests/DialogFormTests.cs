using System.Text;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [PRIM4] form primitives (#289): SelectList + Radio + multiline TextInput
/// hosted in <see cref="DialogOverlay"/> (seated on the z-stack since [PRIM2c]).
/// Pure state transitions plus paint smoke — no backends.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class DialogFormTests
{
    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo EscKey = new('\x1b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo TabKey = new('\t', ConsoleKey.Tab, false, false, false);

    // DialogOverlay prompt editing only inspects Key for Enter/Backspace —
    // any other Key value types KeyChar verbatim.
    private static ConsoleKeyInfo CharKey(char c) => new(c, ConsoleKey.A, false, false, false);

    private static ConsoleKeyInfo SpecialKey(ConsoleKey key) => new('\0', key, false, false, false);

    private static void Type(DialogOverlay dialog, string text)
    {
        foreach (char c in text)
        {
            _ = dialog.HandleKey(CharKey(c));
        }
    }

    private static string PaintArt(DialogOverlay dialog, int cols = 48, int rows = 14)
    {
        var viewport = new Rect(0, 0, cols, rows);
        var buffer = new ScreenBuffer(cols, rows);
        dialog.Paint(buffer, viewport);
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task ShowSelect_State_ExposesOptions_AndClampsInitial()
    {
        var dialog = new DialogOverlay();
        dialog.ShowSelect("Pick", "Choose one:", ["a", "b", "c"], selectedIndex: 9);

        await Assert.That(dialog.Kind).IsEqualTo(DialogKind.Select);
        await Assert.That(dialog.Visible).IsTrue();
        await Assert.That(dialog.SelectedIndex).IsEqualTo(2);
        await Assert.That(dialog.SelectedOption).IsEqualTo("c");
        await Assert.That(dialog.Options.Count).IsEqualTo(3);
        await Assert.That(dialog.FocusedButtonIndex).IsEqualTo(0);
    }

    [Test]
    public async Task ShowSelect_Empty_ThrowsArgumentException()
    {
        var dialog = new DialogOverlay();

        await Assert.That(() => dialog.ShowSelect("T", "m", [])).Throws<ArgumentException>();
    }

    [Test]
    public async Task ShowRadio_Empty_ThrowsArgumentException()
    {
        var dialog = new DialogOverlay();

        await Assert.That(() => dialog.ShowRadio("T", "m", [])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Select_Arrows_MoveAndClamp()
    {
        var dialog = new DialogOverlay();
        dialog.ShowSelect("T", "m", ["a", "b", "c"]);

        for (int i = 0; i < 5; i++)
        {
            _ = dialog.HandleKey(SpecialKey(ConsoleKey.DownArrow));
        }
        await Assert.That(dialog.SelectedIndex).IsEqualTo(2);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.UpArrow));
        await Assert.That(dialog.SelectedOption).IsEqualTo("b");
    }

    [Test]
    public async Task Select_PageHomeEnd_Navigate()
    {
        var dialog = new DialogOverlay();
        dialog.ShowSelect("T", "m", ["a", "b", "c", "d", "e", "f", "g", "h"]);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.PageDown));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(5);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.Home));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(0);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.End));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(7);
    }

    [Test]
    public async Task Select_Enter_SubmitsToHost_Escape_Dismisses()
    {
        var dialog = new DialogOverlay();
        dialog.ShowSelect("T", "m", ["a", "b"]);
        _ = dialog.HandleKey(SpecialKey(ConsoleKey.DownArrow));

        await Assert.That(dialog.HandleKey(EnterKey)).IsFalse();
        await Assert.That(dialog.Visible).IsTrue();
        await Assert.That(dialog.SelectedOption).IsEqualTo("b");

        await Assert.That(dialog.HandleKey(EscKey)).IsTrue();
        await Assert.That(dialog.Visible).IsFalse();
    }

    [Test]
    public async Task Select_Paint_ShowsOptions_WithSelectedMarker()
    {
        var dialog = new DialogOverlay();
        dialog.ShowSelect("Pick", "Choose:", ["apple", "banana"]);
        _ = dialog.HandleKey(SpecialKey(ConsoleKey.DownArrow));

        string art = PaintArt(dialog);

        await Assert.That(art.Contains("apple")).IsTrue();
        await Assert.That(art.Contains("banana")).IsTrue();
        await Assert.That(art.Contains("› banana")).IsTrue();
    }

    [Test]
    public async Task Select_Overflow_Scrolls_AndPaintsScrollbar()
    {
        var dialog = new DialogOverlay();
        var options = new string[20];
        for (int i = 0; i < options.Length; i++)
        {
            options[i] = "option " + i.ToString("00");
        }
        dialog.ShowSelect("T", "m", options);

        for (int i = 0; i < 10; i++)
        {
            _ = dialog.HandleKey(SpecialKey(ConsoleKey.DownArrow));
        }
        string art = PaintArt(dialog, cols: 40, rows: 10);

        await Assert.That(dialog.SelectedIndex).IsEqualTo(10);
        await Assert.That(art.Contains("option 10")).IsTrue();
        await Assert.That(dialog.SelectList.Viewport.Offset > 0).IsTrue();
    }

    [Test]
    public async Task Radio_LeftRight_WrapAround()
    {
        var dialog = new DialogOverlay();
        dialog.ShowRadio("T", "m", ["yes", "no", "maybe"]);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.LeftArrow));
        await Assert.That(dialog.SelectedOption).IsEqualTo("maybe");

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.RightArrow));
        await Assert.That(dialog.SelectedOption).IsEqualTo("yes");
    }

    [Test]
    public async Task Radio_UpDown_AreAliases_HomeEnd_Jump()
    {
        var dialog = new DialogOverlay();
        dialog.ShowRadio("T", "m", ["a", "b", "c"]);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.DownArrow));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(1);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.UpArrow));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(0);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.End));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(2);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.Home));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Radio_Enter_SubmitsToHost_Escape_Dismisses()
    {
        var dialog = new DialogOverlay();
        dialog.ShowRadio("T", "m", ["a", "b"]);
        _ = dialog.HandleKey(SpecialKey(ConsoleKey.RightArrow));

        await Assert.That(dialog.HandleKey(EnterKey)).IsFalse();
        await Assert.That(dialog.Visible).IsTrue();
        await Assert.That(dialog.SelectedOption).IsEqualTo("b");

        await Assert.That(dialog.HandleKey(EscKey)).IsTrue();
        await Assert.That(dialog.Visible).IsFalse();
    }

    [Test]
    public async Task Radio_Paint_ShowsTwoToneMarkers()
    {
        var dialog = new DialogOverlay();
        dialog.ShowRadio("Confirm?", "Proceed:", ["yes", "no"]);

        string art = PaintArt(dialog);

        await Assert.That(art.Contains("yes")).IsTrue();
        await Assert.That(art.Contains("no")).IsTrue();
        await Assert.That(art.Contains("●")).IsTrue();
        await Assert.That(art.Contains("○")).IsTrue();
    }

    [Test]
    public async Task Multiline_Typing_Inserts_BackspaceDeletes()
    {
        var dialog = new DialogOverlay();
        dialog.ShowMultiline("Notes", "Add details:");

        Type(dialog, "hi");
        await Assert.That(dialog.Input).IsEqualTo("hi");

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.Backspace));
        await Assert.That(dialog.Input).IsEqualTo("h");
        await Assert.That(dialog.Editor.LineCount).IsEqualTo(1);
    }

    [Test]
    public async Task Multiline_Enter_SubmitsToHost()
    {
        var dialog = new DialogOverlay();
        dialog.ShowMultiline("Notes", "Add details:");
        Type(dialog, "draft");

        await Assert.That(dialog.HandleKey(EnterKey)).IsFalse();
        await Assert.That(dialog.Visible).IsTrue();
        await Assert.That(dialog.Input).IsEqualTo("draft");
    }

    [Test]
    public async Task Multiline_Prefill_PaintsAllRows()
    {
        var dialog = new DialogOverlay();
        dialog.ShowMultiline("Notes", "Read:", "one\ntwo\nthree");

        await Assert.That(dialog.Editor.LineCount).IsEqualTo(3);
        await Assert.That(dialog.Input).IsEqualTo("one\ntwo\nthree");

        string art = PaintArt(dialog);
        await Assert.That(art.Contains("one")).IsTrue();
        await Assert.That(art.Contains("two")).IsTrue();
        await Assert.That(art.Contains("three")).IsTrue();
    }

    [Test]
    public async Task Multiline_UpDown_MoveBetweenLines_WithoutEditing()
    {
        var dialog = new DialogOverlay();
        dialog.ShowMultiline("Notes", "Edit:", "ab\ncd");
        int end = dialog.Editor.Cursor;

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.UpArrow));

        await Assert.That(dialog.Editor.Cursor < end).IsTrue();
        await Assert.That(dialog.Input).IsEqualTo("ab\ncd");

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.DownArrow));
        await Assert.That(dialog.Editor.Cursor).IsEqualTo(end);
    }

    [Test]
    public async Task Multiline_Arrows_MoveCaret_HomeEnd_LineEdges()
    {
        var dialog = new DialogOverlay();
        dialog.ShowMultiline("Notes", "Edit:", "abc");
        int end = dialog.Editor.Cursor;

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.LeftArrow));
        await Assert.That(dialog.Editor.Cursor).IsEqualTo(end - 1);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.Home));
        await Assert.That(dialog.Editor.Cursor).IsEqualTo(0);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.End));
        await Assert.That(dialog.Editor.Cursor).IsEqualTo(end);
    }

    [Test]
    public async Task Multiline_KeyEvent_ShiftEnter_InsertsNewline_PlainEnter_Submits()
    {
        var dialog = new DialogOverlay();
        dialog.ShowMultiline("Notes", "Edit:", "ab");

        bool newline = dialog.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Shift, isKittyEncoded: true));

        await Assert.That(newline).IsTrue();
        await Assert.That(dialog.Editor.LineCount).IsEqualTo(2);

        await Assert.That(dialog.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(dialog.Visible).IsTrue();
    }

    [Test]
    public async Task Multiline_KeyEvent_Char_InsertsText()
    {
        var dialog = new DialogOverlay();
        dialog.ShowMultiline("Notes", "Edit:");

        await Assert.That(dialog.HandleKey(KeyEvent.Char(new Rune('x')))).IsTrue();
        await Assert.That(dialog.Input).IsEqualTo("x");
    }

    [Test]
    public async Task Prompt_Legacy_Frozen_TypingBackspaceEnter_ArrowsCycleFocus()
    {
        var dialog = new DialogOverlay();
        dialog.ShowPrompt("Auth", "Key:", "", "Next", "Cancel");

        Type(dialog, "k1");
        await Assert.That(dialog.Input).IsEqualTo("k1");

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.Backspace));
        await Assert.That(dialog.Input).IsEqualTo("k");

        // Frozen: horizontal arrows cycle button focus (multiline owns them as caret moves).
        int before = dialog.FocusedButtonIndex;
        _ = dialog.HandleKey(SpecialKey(ConsoleKey.RightArrow));
        await Assert.That(dialog.FocusedButtonIndex).IsEqualTo((before + 1) % dialog.Buttons.Count);

        await Assert.That(dialog.HandleKey(EnterKey)).IsFalse();
    }

    [Test]
    public async Task NonChoice_SelectedDefaults_AreEmpty()
    {
        var dialog = new DialogOverlay();
        dialog.ShowAlert("Hi", "hello");

        await Assert.That(dialog.SelectedIndex).IsEqualTo(-1);
        await Assert.That(dialog.SelectedOption).IsEqualTo(string.Empty);
        await Assert.That(dialog.Options.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Alert_Paint_ContainsTitleMessageAndButton()
    {
        var dialog = new DialogOverlay();
        // Single-word message: WrapText splits on spaces (PRIM2c precedent).
        dialog.ShowAlert("Hello", "worldbody");

        string art = PaintArt(dialog);

        await Assert.That(art.Contains("Hello")).IsTrue();
        await Assert.That(art.Contains("worldbody")).IsTrue();
        await Assert.That(art.Contains("[OK]")).IsTrue();
    }

    [Test]
    public async Task Tab_CyclesButtons_InFormKinds()
    {
        var dialog = new DialogOverlay();
        dialog.ShowSelect("T", "m", ["a", "b"]);

        _ = dialog.HandleKey(TabKey);

        await Assert.That(dialog.FocusedButtonIndex).IsEqualTo(1);
        await Assert.That(dialog.SelectedIndex).IsEqualTo(0);
    }
}
