using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Composer undo/redo core: every effective text edit checkpoints the
/// pre-edit state; no-op edits never pollute the timeline; clear purges
/// history so undo cannot cross draft boundaries.
/// </summary>
public class PromptBufferUndoTests
{
    [Test]
    public async Task Undo_Restores_Last_Insert_And_Walks_Back()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("a");
        _ = buf.InsertText("b");
        _ = buf.InsertText("c");

        var step1 = buf.Undo();
        await Assert.That(step1.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(buf.SnapshotText()).IsEqualTo("ab");

        var step2 = buf.Undo();
        await Assert.That(step2.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(buf.SnapshotText()).IsEqualTo("a");
    }

    [Test]
    public async Task Undo_At_Bottom_Is_Unchanged()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("hi");

        var beyond = buf.Undo();
        await Assert.That(beyond.Kind).IsNotEqualTo(EditOutcomeKind.Unchanged);
        await Assert.That(buf.SnapshotText()).IsEqualTo("");

        var over = buf.Undo();
        await Assert.That(over).IsEqualTo(EditOutcome.Unchanged);
        await Assert.That(buf.SnapshotText()).IsEqualTo("");
    }

    [Test]
    public async Task Redo_Reapplies_Undone_Change()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("ab");
        _ = buf.Backspace();

        _ = buf.Undo();
        await Assert.That(buf.SnapshotText()).IsEqualTo("ab");

        var redone = buf.Redo();
        await Assert.That(redone.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(buf.SnapshotText()).IsEqualTo("a");
    }

    [Test]
    public async Task New_Edit_After_Undo_Forks_Timeline()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("abc");
        _ = buf.Backspace(); // draft "ab"
        _ = buf.Undo();      // restored "abc"

        _ = buf.Insert(new Rune('!')); // fork: redo history must die
        await Assert.That(buf.Redo()).IsEqualTo(EditOutcome.Unchanged);

        _ = buf.Undo();
        await Assert.That(buf.SnapshotText()).IsEqualTo("abc");
    }

    [Test]
    public async Task Noop_Edits_Create_No_Checkpoints()
    {
        var buf = new PromptBuffer();

        for (var i = 0; i < 5; i++)
        {
            await Assert.That(buf.Backspace()).IsEqualTo(EditOutcome.Unchanged);
        }

        _ = buf.InsertText("x");
        _ = buf.Undo();
        await Assert.That(buf.SnapshotText()).IsEqualTo("");

        // If no-ops had polluted the timeline there would be extra steps here.
        await Assert.That(buf.Undo()).IsEqualTo(EditOutcome.Unchanged);
    }

    [Test]
    public async Task Delete_Family_Undos_Restore_Text_And_Kill_Ring_Stays_Alive()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("hello world");
        _ = buf.DeleteWordBackward(); // kill "world"
        await Assert.That(buf.SnapshotText()).IsEqualTo("hello ");

        _ = buf.Undo();
        await Assert.That(buf.SnapshotText()).IsEqualTo("hello world");

        // Undo must not clobber the kill ring: yank after undo still works.
        await Assert.That(buf.LastKill).IsEqualTo("world");
        _ = buf.InsertText(" ");
        _ = buf.InsertText(buf.LastKill!);
        await Assert.That(buf.SnapshotText()).IsEqualTo("hello world world");
    }

    [Test]
    public async Task Undo_Restores_Caret_Position()
    {
        var buf = new PromptBuffer();
        _ = buf.Insert(new Rune('A'));
        await Assert.That(buf.Cursor).IsEqualTo(1);

        _ = buf.Undo();
        await Assert.That(buf.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_Purges_Undo_History()
    {
        var buf = new PromptBuffer();
        _ = buf.InsertText("draft");
        buf.Clear();

        await Assert.That(buf.Undo()).IsEqualTo(EditOutcome.Unchanged);
        await Assert.That(buf.SnapshotText()).IsEqualTo("");
    }
}
