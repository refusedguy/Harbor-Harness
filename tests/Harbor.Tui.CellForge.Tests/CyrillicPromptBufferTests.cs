using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class CyrillicPromptBufferTests
{
    [Test]
    public async Task InsertText_Cyrillic_LengthAndCursor()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет");
        await Assert.That(buf.Length).IsEqualTo(6);
        await Assert.That(buf.Cursor).IsEqualTo(6);
        await Assert.That(buf.SnapshotText()).IsEqualTo("привет");

        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("мир");
        await Assert.That(buf2.Length).IsEqualTo(3);
        await Assert.That(buf2.Cursor).IsEqualTo(3);

        var buf3 = new PromptBuffer();
        _ = buf3.InsertText("ёжик");
        await Assert.That(buf3.Length).IsEqualTo(4);
        await Assert.That(buf3.Cursor).IsEqualTo(4);
        await Assert.That(buf3.SnapshotText()).IsEqualTo("ёжик");

        var buf4 = new PromptBuffer();
        _ = buf4.InsertText("Привет мир");
        await Assert.That(buf4.Length).IsEqualTo(10);
        await Assert.That(buf4.Cursor).IsEqualTo(10);
        await Assert.That(buf4.SnapshotText()).IsEqualTo("Привет мир");

        var buf5 = new PromptBuffer();
        _ = buf5.InsertText("hello привет");
        await Assert.That(buf5.Length).IsEqualTo(12);
        await Assert.That(buf5.Cursor).IsEqualTo(12);
        await Assert.That(buf5.SnapshotText()).IsEqualTo("hello привет");
    }

    [Test]
    public async Task Insert_Rune_Cyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.Insert(new Rune('п'));
        await Assert.That(buf.Length).IsEqualTo(1);
        await Assert.That(buf.Cursor).IsEqualTo(1);
        await Assert.That(buf.SnapshotText()).IsEqualTo("п");

        _ = buf.Insert(new Rune('р'));
        _ = buf.Insert(new Rune('и'));
        await Assert.That(buf.SnapshotText()).IsEqualTo("при");
        await Assert.That(buf.Cursor).IsEqualTo(3);

        // ё is Cyrillic single code unit, not surrogate
        _ = buf.Insert(new Rune('ё'));
        await Assert.That(buf.Length).IsEqualTo(4);
        await Assert.That(buf.SnapshotText()).IsEqualTo("приё");
    }

    [Test]
    public async Task Backspace_OnCyrillic_RemovesOneChar()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет");
        var outcome = buf.Backspace();
        await Assert.That(outcome.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(buf.SnapshotText()).IsEqualTo("приве");
        await Assert.That(buf.Length).IsEqualTo(5);
        await Assert.That(buf.Cursor).IsEqualTo(5);

        // second backspace
        _ = buf.Backspace();
        await Assert.That(buf.SnapshotText()).IsEqualTo("прив");
        await Assert.That(buf.Length).IsEqualTo(4);
    }

    [Test]
    public async Task Backspace_OnEmpty_IsUnchanged()
    {
        var buf = new PromptBuffer();
        await Assert.That(buf.Backspace()).IsEqualTo(EditOutcome.Unchanged);
        await Assert.That(buf.SnapshotText()).IsEqualTo("");
        await Assert.That(buf.Length).IsEqualTo(0);

        // also after inserting and moving to start
        _ = buf.InsertText("привет");
        _ = buf.MoveToStart();
        // Actually Backspace at 0 is unchanged, but DeleteForward would remove
        _ = buf.MoveTo(0);
        await Assert.That(buf.Backspace()).IsEqualTo(EditOutcome.Unchanged);
        await Assert.That(buf.SnapshotText()).IsEqualTo("привет");
    }

    [Test]
    public async Task DeleteForward_OnCyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет");
        _ = buf.MoveToStart();
        var outcome = buf.DeleteForward();
        await Assert.That(outcome.Kind).IsNotEqualTo(EditOutcomeKind.Unchanged);
        await Assert.That(buf.SnapshotText()).IsEqualTo("ривет");
        await Assert.That(buf.Length).IsEqualTo(5);
        await Assert.That(buf.Cursor).IsEqualTo(0);

        // delete in middle
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("привет");
        _ = buf2.MoveTo(3); // after "при"
        _ = buf2.DeleteForward(); // removes 'в'
        await Assert.That(buf2.SnapshotText()).IsEqualTo("приет");
        await Assert.That(buf2.Cursor).IsEqualTo(3);

        // at end is unchanged
        var buf3 = new PromptBuffer();
        _ = buf3.InsertText("мир");
        _ = buf3.MoveToEnd();
        await Assert.That(buf3.DeleteForward()).IsEqualTo(EditOutcome.Unchanged);
    }

    [Test]
    public async Task MoveLeft_Right_RespectsRuneBoundaries_Cyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет");
        await Assert.That(buf.Cursor).IsEqualTo(6);

        // Cyrillic single char per rune: each MoveLeft decrements by 1
        for (int expected = 5; expected >= 0; expected--)
        {
            _ = buf.MoveLeft();
            await Assert.That(buf.Cursor).IsEqualTo(expected);
        }
        await Assert.That(buf.MoveLeft().Kind).IsEqualTo(EditOutcomeKind.Unchanged);

        for (int expected = 1; expected <= 6; expected++)
        {
            _ = buf.MoveRight();
            await Assert.That(buf.Cursor).IsEqualTo(expected);
        }
        await Assert.That(buf.MoveRight().Kind).IsEqualTo(EditOutcomeKind.Unchanged);

        // mixed emoji + Cyrillic: emoji is surrogate pair (2 chars), Cyrillic is 1
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("привет 👍");
        // "привет" 6 + " " 1 + "👍" 2 = 9 chars length
        await Assert.That(buf2.Length).IsEqualTo(9);
        await Assert.That(buf2.Cursor).IsEqualTo(9);
        _ = buf2.MoveLeft();
        await Assert.That(buf2.Cursor).IsEqualTo(7); // jumped over surrogate pair
        _ = buf2.MoveRight();
        await Assert.That(buf2.Cursor).IsEqualTo(9);
        _ = buf2.MoveLeft();
        _ = buf2.MoveLeft(); // over space -> 6
        await Assert.That(buf2.Cursor).IsEqualTo(6);
        _ = buf2.MoveLeft(); // over 'т' -> 5
        await Assert.That(buf2.Cursor).IsEqualTo(5);
    }

    [Test]
    public async Task MoveWordLeft_OnCyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет мир");
        await Assert.That(buf.Cursor).IsEqualTo(10);
        _ = buf.MoveWordLeft();
        await Assert.That(buf.Cursor).IsEqualTo(7); // start of "мир"
        await Assert.That(buf.SnapshotText()[buf.Cursor..]).IsEqualTo("мир");
        _ = buf.MoveWordLeft();
        await Assert.That(buf.Cursor).IsEqualTo(0);
        await Assert.That(buf.MoveWordLeft().Kind).IsEqualTo(EditOutcomeKind.Unchanged);

        // multiple spaces
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("привет   мир");
        _ = buf2.MoveWordLeft();
        await Assert.That(buf2.Cursor).IsEqualTo(9);
        _ = buf2.MoveWordLeft();
        await Assert.That(buf2.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task MoveWordRight_OnCyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет мир");
        _ = buf.MoveToStart();
        _ = buf.MoveWordRight();
        await Assert.That(buf.Cursor).IsEqualTo(6); // after "привет"
        _ = buf.MoveWordRight();
        await Assert.That(buf.Cursor).IsEqualTo(10); // after "мир"
        await Assert.That(buf.MoveWordRight().Kind).IsEqualTo(EditOutcomeKind.Unchanged);

        // from middle of word
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("привет мир ёжик");
        _ = buf2.MoveTo(2);
        _ = buf2.MoveWordRight();
        await Assert.That(buf2.Cursor).IsEqualTo(6);
        _ = buf2.MoveWordRight();
        await Assert.That(buf2.Cursor).IsEqualTo(10);
        _ = buf2.MoveWordRight();
        await Assert.That(buf2.Cursor).IsEqualTo(15);
    }

    [Test]
    public async Task DeleteWordBackward_OnCyrillic_RemovesLastWord()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет мир");
        var outcome = buf.DeleteWordBackward();
        await Assert.That(outcome.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(buf.SnapshotText()).IsEqualTo("привет ");
        await Assert.That(buf.Cursor).IsEqualTo(7);
        await Assert.That(buf.LastKill).IsEqualTo("мир");

        _ = buf.DeleteWordBackward();
        await Assert.That(buf.SnapshotText()).IsEqualTo("");
        await Assert.That(buf.LastKill).IsEqualTo("привет ");

        // single word
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("ёжик");
        _ = buf2.DeleteWordBackward();
        await Assert.That(buf2.SnapshotText()).IsEqualTo("");
        await Assert.That(buf2.LastKill).IsEqualTo("ёжик");
    }

    [Test]
    public async Task DisplayCells_Cyrillic_Width()
    {
        await Assert.That(PromptBuffer.DisplayCells("привет".AsSpan())).IsEqualTo(6);
        await Assert.That(PromptBuffer.DisplayCells("мир".AsSpan())).IsEqualTo(3);
        await Assert.That(PromptBuffer.DisplayCells("ёжик".AsSpan())).IsEqualTo(4);
        await Assert.That(PromptBuffer.DisplayCells("Привет мир".AsSpan())).IsEqualTo(10);
        await Assert.That(PromptBuffer.DisplayCells("hello привет".AsSpan())).IsEqualTo(12);
        await Assert.That(PromptBuffer.DisplayCells("".AsSpan())).IsEqualTo(0);
    }

    [Test]
    public async Task DisplayCells_MixedCyrillicCjk_And_Emoji()
    {
        // Cyrillic 1 cell each, CJK 2 cells each
        await Assert.That(PromptBuffer.DisplayCells("привет中".AsSpan())).IsEqualTo(8);
        await Assert.That(PromptBuffer.DisplayCells("中привет".AsSpan())).IsEqualTo(8);
        await Assert.That(PromptBuffer.DisplayCells("a中b".AsSpan())).IsEqualTo(4);
        await Assert.That(PromptBuffer.DisplayCells("hello中привет".AsSpan())).IsEqualTo(13);

        // emoji + Cyrillic: "привет 👍" => 6 + 1 + 2 = 9
        await Assert.That(PromptBuffer.DisplayCells("привет 👍".AsSpan())).IsEqualTo(9);
        await Assert.That(PromptBuffer.DisplayCells("👍".AsSpan())).IsEqualTo(2);
        await Assert.That(PromptBuffer.DisplayCells("ёжик 👍".AsSpan())).IsEqualTo(7);
    }

    [Test]
    public async Task Undo_Redo_WithCyrillic_PreservesTextAndCursor()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет");
        await Assert.That(buf.Cursor).IsEqualTo(6);
        _ = buf.Backspace(); // "приве"
        await Assert.That(buf.SnapshotText()).IsEqualTo("приве");
        await Assert.That(buf.Cursor).IsEqualTo(5);

        var undo = buf.Undo();
        await Assert.That(undo.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(buf.SnapshotText()).IsEqualTo("привет");
        await Assert.That(buf.Cursor).IsEqualTo(6);

        var redo = buf.Redo();
        await Assert.That(redo.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(buf.SnapshotText()).IsEqualTo("приве");
        await Assert.That(buf.Cursor).IsEqualTo(5);

        // multiple inserts and undo chain
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("привет");
        _ = buf2.InsertText(" мир");
        await Assert.That(buf2.SnapshotText()).IsEqualTo("привет мир");
        _ = buf2.Undo();
        await Assert.That(buf2.SnapshotText()).IsEqualTo("привет");
        _ = buf2.Undo();
        await Assert.That(buf2.SnapshotText()).IsEqualTo("");
        _ = buf2.Redo();
        await Assert.That(buf2.SnapshotText()).IsEqualTo("привет");
        _ = buf2.Redo();
        await Assert.That(buf2.SnapshotText()).IsEqualTo("привет мир");
    }

    [Test]
    public async Task TakeText_SnapshotText_Roundtrip_Cyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет мир");
        await Assert.That(buf.SnapshotText()).IsEqualTo("привет мир");
        string taken = buf.TakeText();
        await Assert.That(taken).IsEqualTo("привет мир");
        await Assert.That(buf.IsEmpty).IsTrue();
        await Assert.That(buf.Cursor).IsEqualTo(0);
        await Assert.That(buf.SnapshotText()).IsEqualTo("");

        // after TakeText can insert again
        _ = buf.InsertText("ёжик");
        await Assert.That(buf.SnapshotText()).IsEqualTo("ёжик");

        // SnapshotText does not clear
        await Assert.That(buf.SnapshotText()).IsEqualTo("ёжик");
        await Assert.That(buf.SnapshotText()).IsEqualTo("ёжик");

        // emoji + Cyrillic roundtrip
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("привет 👍");
        string taken2 = buf2.TakeText();
        await Assert.That(taken2).IsEqualTo("привет 👍");
        await Assert.That(buf2.IsEmpty).IsTrue();
    }

    [Test]
    public async Task LineCount_WithEmbeddedNewline_Cyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет\nмир");
        await Assert.That(buf.LineCount).IsEqualTo(2);
        await Assert.That(buf.SnapshotText()).IsEqualTo("привет\nмир");
        await Assert.That(buf.Length).IsEqualTo(10); // 6 +1 +3

        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("а\nб\nв");
        await Assert.That(buf2.LineCount).IsEqualTo(3);

        var buf3 = new PromptBuffer();
        _ = buf3.InsertText("ёжик");
        await Assert.That(buf3.LineCount).IsEqualTo(1);

        var buf4 = new PromptBuffer();
        _ = buf4.InsertText("hello привет\nмир ёжик\ntest");
        await Assert.That(buf4.LineCount).IsEqualTo(3);

        // empty buffer is 1 line by definition
        var empty = new PromptBuffer();
        await Assert.That(empty.LineCount).IsEqualTo(1);
    }

    [Test]
    public async Task MoveUp_MoveDown_PreservesColumn_WithCyrillic()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("привет\nмир");
        _ = buf.MoveToStart();
        // move right 3 on first line => column 3
        _ = buf.MoveRight();
        _ = buf.MoveRight();
        _ = buf.MoveRight();
        await Assert.That(buf.Cursor).IsEqualTo(3);
        int colBefore = PromptBuffer.DisplayCells(buf.SnapshotText().AsSpan(0, buf.Cursor));
        await Assert.That(colBefore).IsEqualTo(3);

        _ = buf.MoveDown();
        // second line "мир" length 3, column should be preserved (min 3,3)
        await Assert.That(buf.LineIndexOf(buf.Cursor)).IsEqualTo(1);
        int colAfter = PromptBuffer.DisplayCells(buf.SnapshotText().AsSpan(buf.LineStartOf(buf.Cursor), buf.Cursor - buf.LineStartOf(buf.Cursor)));
        await Assert.That(colAfter).IsEqualTo(3);
        await Assert.That(buf.Cursor).IsEqualTo(10); // 7 (start of line2) +3

        _ = buf.MoveUp();
        await Assert.That(buf.LineIndexOf(buf.Cursor)).IsEqualTo(0);
        int colUp = PromptBuffer.DisplayCells(buf.SnapshotText().AsSpan(buf.LineStartOf(buf.Cursor), buf.Cursor - buf.LineStartOf(buf.Cursor)));
        await Assert.That(colUp).IsEqualTo(3);

        // CJK + Cyrillic: first line wider due to CJK
        var buf2 = new PromptBuffer();
        _ = buf2.InsertText("привет中\nмир");
        _ = buf2.MoveToStart();
        _ = buf2.MoveRight(); // 1
        _ = buf2.MoveRight(); // 2
        _ = buf2.MoveRight(); // 3
        _ = buf2.MoveRight(); // 4
        _ = buf2.MoveRight(); // 5
        _ = buf2.MoveRight(); // 6 (after привет)
        await Assert.That(buf2.Cursor).IsEqualTo(6);
        _ = buf2.MoveDown();
        // second line "мир" 3 cells, column 6 clamps to end (3)
        await Assert.That(buf2.LineIndexOf(buf2.Cursor)).IsEqualTo(1);
        await Assert.That(buf2.Cursor).IsEqualTo(buf2.Length);

        // at top, MoveUp stays at 0 or clamps
        var buf3 = new PromptBuffer();
        _ = buf3.InsertText("ёжик\nпривет");
        _ = buf3.MoveToStart();
        _ = buf3.MoveUp();
        await Assert.That(buf3.Cursor).IsEqualTo(0);
        _ = buf3.MoveToEnd();
        _ = buf3.MoveDown();
        await Assert.That(buf3.Cursor).IsEqualTo(buf3.Length);
    }
}
