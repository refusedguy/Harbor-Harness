using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using UiKey = Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.Tui.CellForge.Tests;

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
    public async Task CtrlZ_Undoes_Last_Edit_CtrlShiftZ_Redoes()
    {
        var composer = new ComposerController();
        foreach (var c in "abc")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        await Assert.That(composer.HandleKey(CharKey('z', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("ab");

        // Kitty CSI-u reports the shifted codepoint with C|S modifiers set.
        await Assert.That(
                composer.HandleKey(CharKey('Z', KeyModifiers.Ctrl | KeyModifiers.Shift)))
            .IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("abc");
    }

    [Test]
    public async Task Undo_Chords_Are_Dead_When_History_Empty()
    {
        var composer = new ComposerController();

        await Assert.That(composer.HandleKey(CharKey('z', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Ignored);
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();

        // Redo is ignored even after edits — nothing undone yet, fork rule.
        foreach (var c in "draft")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        await Assert.That(composer.HandleKey(CharKey('Z', KeyModifiers.Ctrl | KeyModifiers.Shift)))
            .IsEqualTo(ComposerAction.Ignored);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
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

    [Test]
    public async Task AltD_KillsWord_CtrlY_YanksItBackAtCaret()
    {
        var composer = new ComposerController();
        foreach (var c in "one two")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        // Kill the first word, then yank it right back in front of " two".
        _ = composer.HandleKey(CharKey('a', KeyModifiers.Ctrl));
        await Assert.That(composer.HandleKey(CharKey('d', KeyModifiers.Alt))).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo(" two");

        await Assert.That(composer.HandleKey(CharKey('y', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one two");
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(3);

        // C-k/C-y cycle: a fresh line-end kill becomes the slot and yanks back.
        _ = composer.HandleKey(CharKey('k', KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one");
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(composer.HandleKey(CharKey('y', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one two");
    }

    [Test]
    public async Task CtrlY_WithoutPriorKill_IsIgnored()
    {
        var composer = new ComposerController();
        await Assert.That(composer.HandleKey(CharKey('y', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Ignored);
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();
    }

    [Test]
    public async Task CtrlU_Then_CtrlY_RestoresDraftLinePrefix()
    {
        var composer = new ComposerController();
        foreach (var c in "keep\ndrop-me")
        {
            _ = composer.HandleKey(CharKey(c));
        }

        _ = composer.HandleKey(CharKey('u', KeyModifiers.Ctrl));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("keep\n");

        await Assert.That(composer.HandleKey(CharKey('y', KeyModifiers.Ctrl))).IsEqualTo(ComposerAction.Edited);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("keep\ndrop-me");
    }
}

public class ComposerKeyContextTests
{
    private static UiKey.KeyEvent Char(char c, UiKey.KeyModifiers mods = UiKey.KeyModifiers.None) =>
        UiKey.KeyEvent.Char(new Rune(c), mods);

    [Test]
    public async Task HandleKey_Uses_ComposerKeyContext_TryMatch_ForChordFamilies()
    {
        var composer = new ComposerController();
        var engineKey = KeyEvent.Char(new Rune('c'), KeyModifiers.Ctrl);

        await Assert.That(composer.ComposerKeyContext.TryMatch(engineKey, out var hit)).IsTrue();
        await Assert.That(hit.ActionId).IsEqualTo("char.ctrl-c");

        await Assert.That(composer.HandleKey(Char('c', UiKey.KeyModifiers.Ctrl)))
            .IsEqualTo(ComposerAction.Aborted);
    }

    [Test]
    public async Task ComposerKeyContext_Rebind_HasPriority_ForMatchedGesture()
    {
        var composer = new ComposerController();
        composer.ComposerKeyContext.Bind(KeyGesture.ForChar('a', KeyModifiers.Ctrl), "char.ctrl-e");

        _ = composer.Buffer.InsertText("abc");
        _ = composer.HandleKey(Char('a', UiKey.KeyModifiers.Ctrl));

        // Rebound to ctrl-e action: moves caret to line end, text unchanged.
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("abc");
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(3);
    }

    [Test]
    public async Task ComposerKeyContext_BindNoAction_ConsumesMatchedGesture()
    {
        var composer = new ComposerController();
        composer.ComposerKeyContext.BindNoAction(KeyGesture.ForKey(KeyCode.Left, KeyModifiers.None));

        _ = composer.Buffer.InsertText("ab");
        var action = composer.HandleKey(UiKey.KeyEvent.Simple(UiKey.KeyCode.Left));

        await Assert.That(action).IsEqualTo(ComposerAction.Ignored);
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(2);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("ab");
    }
}
