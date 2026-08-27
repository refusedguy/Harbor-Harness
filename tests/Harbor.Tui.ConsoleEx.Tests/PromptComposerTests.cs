using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

public class PromptBufferTests
{
    [Test]
    public async Task Insert_And_Snapshot_RoundTrip()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("harbor");
        await Assert.That(b.SnapshotText()).IsEqualTo("harbor");
        await Assert.That(b.Cursor).IsEqualTo(6);
    }

    [Test]
    public async Task Insert_SurrogatePair_CursorNeverInside()
    {
        var b = new PromptBuffer();
        _ = b.Insert(new Rune(0x1F600)); // 😀
        await Assert.That(b.Length).IsEqualTo(2);
        await Assert.That(b.Cursor).IsEqualTo(2);
        _ = b.MoveLeft();
        await Assert.That(b.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task Backspace_RemovesWholeSurrogatePair()
    {
        var b = new PromptBuffer();
        _ = b.Insert(new Rune(0x1F600));
        var outcome = b.Backspace();
        await Assert.That(b.IsEmpty).IsTrue();
        await Assert.That(outcome.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
    }

    [Test]
    public async Task Movement_AtEdges_IsUnchanged()
    {
        var b = new PromptBuffer();
        await Assert.That(b.MoveLeft().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
        await Assert.That(b.MoveRight().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
        await Assert.That(b.Backspace().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
        await Assert.That(b.DeleteForward().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
    }

    [Test]
    public async Task DeleteForward_RemovesRuneAfterCaret()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("ab");
        _ = b.MoveToStart();
        _ = b.DeleteForward();
        await Assert.That(b.SnapshotText()).IsEqualTo("b");
    }

    [Test]
    public async Task MoveTo_ClampsAndReportsCursorOnly()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("abc");
        _ = b.MoveTo(2);
        await Assert.That(b.Cursor).IsEqualTo(2);
        _ = b.MoveTo(-5);
        await Assert.That(b.Cursor).IsEqualTo(0);
        _ = b.MoveTo(99);
        await Assert.That(b.Cursor).IsEqualTo(3);
    }

    [Test]
    public async Task Insert_MidBuffer_ShiftsTailInsteadOfOverwrite()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("helo");
        _ = b.MoveLeft();
        _ = b.MoveLeft();
        _ = b.Insert(new Rune('l'));

        await Assert.That(b.SnapshotText()).IsEqualTo("hello");
        await Assert.That(b.Cursor).IsEqualTo(3);

        _ = b.MoveToStart();
        _ = b.InsertText("**");
        await Assert.That(b.SnapshotText()).IsEqualTo("**hello");
        await Assert.That(b.Cursor).IsEqualTo(2);
    }

    [Test]
    public async Task Insert_SurrogatePairMidBuffer_NeverSplits()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("ab!");
        _ = b.MoveLeft();
        _ = b.Insert(new Rune(0x1F600));

        await Assert.That(b.SnapshotText()).IsEqualTo("ab😀!");
        await Assert.That(b.Cursor).IsEqualTo(4);
    }

    [Test]
    public async Task RemoveRange_MiddleSpan_ShiftsTail()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("**bold**");
        var outcome = b.RemoveRange(0, 2);
        await Assert.That(b.SnapshotText()).IsEqualTo("bold**");
        await Assert.That(outcome.Kind).IsNotEqualTo(EditOutcomeKind.Unchanged);
        _ = b.MoveToEnd();
        _ = b.RemoveRange(4, 2);
        await Assert.That(b.SnapshotText()).IsEqualTo("bold");
        await Assert.That(b.Cursor).IsEqualTo(4);
    }

    [Test]
    public async Task RemoveRange_CursorInsideOrPastShifts()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("abcdef");
        _ = b.MoveTo(3);
        _ = b.RemoveRange(1, 2); // "a" "d|ef" — caret lands on shift seam
        await Assert.That(b.SnapshotText()).IsEqualTo("adef");
        await Assert.That(b.Cursor).IsEqualTo(1);
        _ = b.RemoveRange(99, 3);
        await Assert.That(b.SnapshotText()).IsEqualTo("adef");
    }

    [Test]
    public async Task ShiftEnter_Newlines_MultiLineNavigation()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("alpha\nbeta");
        await Assert.That(b.LineCount).IsEqualTo(2); // two logical lines

        _ = b.MoveToStart();
        int before = b.Cursor;
        _ = b.MoveDown();

        // lands on second line, same column (0)
        await Assert.That(b.Cursor).IsNotEqualTo(before);
        await Assert.That(b.LineIndexOf(b.Cursor)).IsEqualTo(1);

        _ = b.MoveUp();
        await Assert.That(b.LineIndexOf(b.Cursor)).IsEqualTo(0);
    }

    [Test]
    public async Task MoveDown_ColumnPreservedAcrossWideChars()
    {
        var b = new PromptBuffer();
        // line1: a中b  (4 cells), line2: xyzw
        _ = b.InsertText("a中b\nxyzw");
        _ = b.MoveToStart();
        _ = b.MoveRight();      // after 'a' → cell 1
        _ = b.MoveRight();      // after 中 → cell 3
        int colBefore = PromptBuffer.DisplayCells(b.SnapshotText().AsSpan(0, b.Cursor));
        _ = b.MoveDown();
        int colAfter = PromptBuffer.DisplayCells(b.SnapshotText().AsSpan(b.LineStartOf(b.Cursor), b.Cursor - b.LineStartOf(b.Cursor)));

        await Assert.That(colAfter).IsEqualTo(Math.Min(colBefore, 4));
    }

    [Test]
    public async Task DeleteWordBackward_RemovesTrailingWord()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("hello world");
        _ = b.DeleteWordBackward();
        await Assert.That(b.SnapshotText()).IsEqualTo("hello ");
        _ = b.DeleteWordBackward();
        await Assert.That(b.SnapshotText()).IsEqualTo("");
    }

    [Test]
    public async Task DeleteToLineStart_ClearsCurrentLineOnly()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("keep\ndelete-me");
        _ = b.MoveToEnd();
        _ = b.DeleteToLineStart();
        await Assert.That(b.SnapshotText()).IsEqualTo("keep\n");
    }

    [Test]
    public async Task DeleteToLineEnd_KillsTailOfCurrentLineOnly()
    {
        // Mid-line kill on line 1 keeps the newline + line 2 intact.
        var b = new PromptBuffer();
        _ = b.InsertText("first\nsecond");
        _ = b.MoveToStart();
        _ = b.MoveRight();
        _ = b.MoveRight();
        _ = b.DeleteToLineEnd();
        await Assert.That(b.SnapshotText()).IsEqualTo("fi\nsecond");

        // At line end the kill is a no-op.
        var atEnd = new PromptBuffer();
        _ = atEnd.InsertText("abc");
        await Assert.That(atEnd.DeleteToLineEnd().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
    }

    [Test]
    public async Task DeleteWordForward_RemovesWordAfterCaret()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("alpha beta gamma");
        _ = b.MoveWordLeft();      // before "gamma"
        _ = b.MoveWordLeft();      // before "beta"
        await Assert.That(b.SnapshotText()[b.Cursor..]).StartsWith("beta");
        _ = b.DeleteWordForward();
        await Assert.That(b.SnapshotText()).IsEqualTo("alpha  gamma");

        // At/beyond the end: unchanged.
        _ = b.MoveToEnd();
        await Assert.That(b.DeleteWordForward().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
    }

    [Test]
    public async Task WordMovement_HopsWhitespaceRuns_AndStopsAtEdges()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("one two   three");
        _ = b.MoveToStart();

        // Forward lands on the cell AFTER each word (emacs M-f), then drains
        // whole whitespace runs before continuing to hop.
        _ = b.MoveWordRight();
        await Assert.That(b.Cursor).IsEqualTo(3);
        _ = b.MoveWordRight();
        await Assert.That(b.Cursor).IsEqualTo(7);
        _ = b.MoveWordRight();
        await Assert.That(b.Cursor).IsEqualTo(15);
        await Assert.That(b.MoveWordRight().Kind).IsEqualTo(EditOutcomeKind.Unchanged);

        // Backward always lands on a word start (emacs M-b).
        _ = b.MoveWordLeft();
        await Assert.That(b.Cursor).IsEqualTo(10);
        _ = b.MoveWordLeft();
        await Assert.That(b.Cursor).IsEqualTo(4);
        _ = b.MoveWordLeft();
        await Assert.That(b.Cursor).IsEqualTo(0);
        await Assert.That(b.MoveWordLeft().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
    }

    [Test]
    public async Task TakeText_ResetsState()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("payload");
        string taken = b.TakeText();
        await Assert.That(taken).IsEqualTo("payload");
        await Assert.That(b.IsEmpty).IsTrue();
        await Assert.That(b.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task Kills_RecordLastKill_NoOpKeepsPrevious()
    {
        var b = new PromptBuffer();
        await Assert.That(b.LastKill).IsNull();

        // Ctrl+W semantics: trailing whitespace run + word are one kill span.
        _ = b.InsertText("hello world");
        _ = b.DeleteWordBackward();
        await Assert.That(b.SnapshotText()).IsEqualTo("hello ");
        await Assert.That(b.LastKill).IsEqualTo("world");

        // No-op kill (caret at line end) must not clobber the previous entry.
        await Assert.That(b.DeleteToLineEnd().Kind).IsEqualTo(EditOutcomeKind.Unchanged);
        await Assert.That(b.LastKill).IsEqualTo("world");

        // A fresh successful kill replaces the ring slot entirely.
        _ = b.InsertText("two");
        _ = b.MoveToStart();
        _ = b.DeleteToLineEnd();
        await Assert.That(b.LastKill).IsEqualTo("hello ");
    }

    [Test]
    public async Task MultilineKills_StayWithinCurrentLine()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("first\nsecond");
        _ = b.MoveToStart();
        _ = b.MoveRight();
        _ = b.MoveRight();
        _ = b.DeleteToLineEnd();
        await Assert.That(b.LastKill).IsEqualTo("rst");

        var back = new PromptBuffer();
        _ = back.InsertText("keep\ndelete-me");
        _ = back.MoveToEnd();
        _ = back.DeleteToLineStart();
        await Assert.That(back.LastKill).IsEqualTo("delete-me");

        // Forward word kill from a word start spans only the word itself.
        var fwd = new PromptBuffer();
        _ = fwd.InsertText("alpha beta gamma");
        _ = fwd.MoveWordLeft();
        _ = fwd.MoveWordLeft();
        _ = fwd.DeleteWordForward();
        await Assert.That(fwd.LastKill).IsEqualTo("beta");
    }

    [Test]
    public async Task BackspaceAndDeleteForward_AreNotKills()
    {
        var b = new PromptBuffer();
        _ = b.InsertText("abc");
        _ = b.Backspace();
        _ = b.MoveToStart();
        _ = b.DeleteForward();
        await Assert.That(b.LastKill).IsNull();
    }
}

public class PromptViewportTests
{
    [Test]
    public async Task FitsWithinWidth_StartsAtZero()
    {
        var vp = PromptViewport.ScrollIntoView("short", 5, 20);
        await Assert.That(vp.Start).IsEqualTo(0);
    }

    [Test]
    public async Task CaretAtEnd_ScrollsWindowForward()
    {
        const string line = "abcdefghijklmnopqrstuvwxyz"; // 26 chars/cells, width 10
        var vp = PromptViewport.ScrollIntoView(line, 26, 10);
        await Assert.That(vp.Start).IsEqualTo(16);
        await Assert.That(line[vp.Start..]).IsEqualTo("qrstuvwxyz");
    }

    [Test]
    public async Task WideRunes_WindowSnapsToRuneBoundary()
    {
        const string line = "中中中中中中"; // 6 chars = 12 cells, width 5
        var vp = PromptViewport.ScrollIntoView(line, caretInLine: 6, widthCells: 5);
        int visibleCells = PromptBuffer.DisplayCells(line.AsSpan(vp.Start));
        await Assert.That(visibleCells).IsLessThanOrEqualTo(6);
        // Window starts on a rune lead char — never mid-surrogate/mid-cluster.
        await Assert.That(UnicodeWidth.Width(new Rune(line[vp.Start]))).IsEqualTo(2);
    }
}

public class ComposerControllerTests
{
    private static KeyEvent CharKey(char c, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Char(new Rune(c), mods);

    [Test]
    public async Task PlainEnter_Submits()
    {
        var composer = new ComposerController();
        _ = composer.Buffer.InsertText("hi");
        var action = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(action).IsEqualTo(ComposerAction.Submitted);
    }

    [Test]
    public async Task ShiftEnter_InsertsNewline_KittyDistinguishes()
    {
        var composer = new ComposerController();
        _ = composer.Buffer.InsertText("line");
        var action = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Shift, isKittyEncoded: true));

        await Assert.That(action).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("line\n");

        // Legacy encoder cannot distinguish — plain Enter still submits.
        var legacyEnter = KeyEvent.Simple(KeyCode.Enter);
        await Assert.That(composer.HandleKey(legacyEnter)).IsEqualTo(ComposerAction.Submitted);
    }

    [Test]
    public async Task AltEnter_AlsoInsertsNewline()
    {
        var composer = new ComposerController();
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Alt, isKittyEncoded: true));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("\n");
    }

    [Test]
    public async Task PrintableChar_Inserts()
    {
        var composer = new ComposerController();
        var action = composer.HandleKey(CharKey('x'));
        await Assert.That(action).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("x");
    }

    [Test]
    public async Task CtrlC_EmptyBuffer_Aborts_NonEmpty_Clears()
    {
        var composer = new ComposerController();
        await Assert.That(composer.HandleKey(CharKey('c', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Aborted);

        _ = composer.Buffer.InsertText("draft");
        await Assert.That(composer.HandleKey(CharKey('c', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();
    }

    [Test]
    public async Task CtrlU_DeletesLinePrefix()
    {
        var composer = new ComposerController();
        foreach (var c in "hello")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(CharKey('u', KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();
    }

    [Test]
    public async Task ArrowsAndHomeEnd_MoveCaret()
    {
        var composer = new ComposerController();
        foreach (var c in "abc")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Left));
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Home));
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(0);
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(3);
    }

    [Test]
    public async Task PasteText_GoesThroughInsert_NoSubmit()
    {
        var composer = new ComposerController();
        var action = composer.Buffer.InsertText("rm -rf /\nharbor --dangerous");
        await Assert.That(action.Kind).IsEqualTo(EditOutcomeKind.TextAndCursor);
        await Assert.That(composer.Buffer.LineCount).IsEqualTo(2);
    }

    [Test]
    public async Task CtrlA_CtrlE_JumpLineBoundaries_LikeReadline()
    {
        var composer = new ComposerController();
        foreach (var c in "abc")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(CharKey('a', KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(0);
        _ = composer.HandleKey(CharKey('e', KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(3);
    }

    [Test]
    public async Task CtrlK_KillsToEndOfLine_MultilineSafe()
    {
        var composer = new ComposerController();
        foreach (var c in "first")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Shift, isKittyEncoded: true));
        foreach (var c in "second")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        // Park mid-line on row 0 (deterministic column 2), then kill to its end.
        _ = composer.Buffer.MoveUp();           // clamps to end of the shorter row 0
        await Assert.That(composer.Buffer.LineIndexOf(composer.Buffer.Cursor)).IsEqualTo(0);
        _ = composer.Buffer.MoveToStart();
        _ = composer.Buffer.MoveRight();
        _ = composer.HandleKey(CharKey('k', KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.SnapshotText()).DoesNotContain("rst");
        await Assert.That(composer.Buffer.SnapshotText()).Contains("second");
    }

    [Test]
    public async Task CtrlArrows_WordHop_MetaBD_DeleteForward()
    {
        var composer = new ComposerController();
        foreach (var c in "one two")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Left, KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(4);

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Right, KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(7);

        // Alt+B hops back to the start of the current word.
        _ = composer.HandleKey(CharKey('b', KeyModifiers.Alt));
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(4);

        // Alt+D from a word start kills the word itself, keeping separators ahead.
        var altD = new ComposerController();
        foreach (var c in "one two")
        {
            _ = altD.HandleKey(CharKey(c));
        }

        _ = altD.Buffer.MoveToStart();
        _ = altD.HandleKey(CharKey('d', KeyModifiers.Alt));
        await Assert.That(altD.Buffer.SnapshotText()).IsEqualTo(" two");

        // Word delete backward still works after the new movement helpers.
        _ = altD.Buffer.MoveToEnd();
        _ = altD.HandleKey(CharKey('w', KeyModifiers.Ctrl));
        await Assert.That(altD.Buffer.SnapshotText()).IsEqualTo(" ");
    }

    [Test]
    public async Task AltS_AltI_AltC_MarkdownWrapChords()
    {
        var bold = new ComposerController();
        foreach (var c in "pay")
        {
            _ = bold.HandleKey(CharKey(c));
        }

        _ = bold.HandleKey(CharKey('s', KeyModifiers.Alt));
        await Assert.That(bold.Buffer.SnapshotText()).IsEqualTo("**pay**");
        await Assert.That(bold.Buffer.Cursor).IsEqualTo(2);

        _ = bold.HandleKey(CharKey('s', KeyModifiers.Alt)); // second toggle unwraps
        await Assert.That(bold.Buffer.SnapshotText()).IsEqualTo("pay");

        var italic = new ComposerController();
        foreach (var c in "soft")
        {
            _ = italic.HandleKey(CharKey(c));
        }

        _ = italic.HandleKey(CharKey('i', KeyModifiers.Alt));
        await Assert.That(italic.Buffer.SnapshotText()).IsEqualTo("*soft*");

        var code = new ComposerController();
        _ = code.HandleKey(CharKey('c', KeyModifiers.Alt)); // empty buffer: bare pair
        await Assert.That(code.Buffer.SnapshotText()).IsEqualTo("``");
        await Assert.That(code.Buffer.Cursor).IsEqualTo(1);
    }

    [Test]
    public async Task AltH_AltL_LinePrefixChords()
    {
        var heading = new ComposerController();
        foreach (var c in "notes")
        {
            _ = heading.HandleKey(CharKey(c));
        }

        _ = heading.HandleKey(CharKey('h', KeyModifiers.Alt));
        await Assert.That(heading.Buffer.SnapshotText()).IsEqualTo("# notes");

        var list = new ComposerController();
        foreach (var c in "notes")
        {
            _ = list.HandleKey(CharKey(c));
        }

        _ = list.HandleKey(CharKey('l', KeyModifiers.Alt));
        await Assert.That(list.Buffer.SnapshotText()).IsEqualTo("- notes");
        _ = list.HandleKey(CharKey('l', KeyModifiers.Alt)); // second toggle removes
        await Assert.That(list.Buffer.SnapshotText()).IsEqualTo("notes");

        var fresh = new ComposerController();
        _ = fresh.HandleKey(CharKey('l', KeyModifiers.Alt)); // empty prompt starts a list
        await Assert.That(fresh.Buffer.SnapshotText()).IsEqualTo("- ");
        await Assert.That(fresh.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsEqualTo(ComposerAction.Submitted); // chords keep submit path intact
    }
}

public class ComposerHistoryRecallTests
{
    private static KeyEvent CharKey(char c, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Char(new Rune(c), mods);

    /// <summary>Type one letter + Enter, mimicking the host's post-submit TakeText.</summary>
    private static void TypeAndSubmit(ComposerController composer, string text)
    {
        foreach (var c in text)
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        _ = composer.Buffer.TakeText();
    }

    [Test]
    public async Task Submit_PushesTrimmed_NonEmpty_Only()
    {
        var composer = new ComposerController();
        TypeAndSubmit(composer, "run tests");
        TypeAndSubmit(composer, string.Empty);
        TypeAndSubmit(composer, "  ");

        await Assert.That(composer.History.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UpThenDown_WalksHistory_AndRestoresDraft()
    {
        var composer = new ComposerController();
        TypeAndSubmit(composer, "one");
        TypeAndSubmit(composer, "two");
        TypeAndSubmit(composer, "wip");     // becomes the saved draft

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("wip");

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("two");
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one");

        // Oldest boundary: further Up keeps the oldest entry (caret move only).
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one");

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Down));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("two");
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Down));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("wip");
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Down));
        // Draft was captured right after the last TakeText ⇒ restored empty.
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();

        // Walk ended — Down is plain caret movement again.
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Down));
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();
    }

    [Test]
    public async Task MultilineDraft_EscalatesToRecallOnlyAtFirstLine()
    {
        var composer = new ComposerController();
        TypeAndSubmit(composer, "history-entry");

        // Caret parked on the LAST logical line ⇒ Up must not hijack the key.
        foreach (var c in "alpha")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Shift, isKittyEncoded: true));
        foreach (var c in "beta")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("alpha\nbeta");
        await Assert.That(composer.Buffer.LineIndexOf(composer.Buffer.Cursor)).IsEqualTo(0);

        // Now at the first line with idle history? No — a walk never started,
        // so Up falls through to caret movement until we hit TopLine+Enter-state…
        // Here it DOES start a walk: topmost line + entries exist.
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("history-entry");
    }

    [Test]
    public async Task CtrlC_Clear_ResetsWalk()
    {
        var composer = new ComposerController();
        TypeAndSubmit(composer, "kept");
        _ = composer.Buffer.InsertText("draft");
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("kept");

        _ = composer.Buffer.InsertText("!");   // edit breaks nothing by contract below
        _ = composer.HandleKey(CharKey('c', KeyModifiers.Ctrl));  // clear-all
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();

        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Down));
        // Walk was reset — Down cannot resurrect the abandoned recall state.
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();
    }
}
