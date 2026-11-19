using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Vim composer layer: toggle contract, normal-mode chords over readline
/// primitives, insert/normal transitions, history recall via j/k.
/// </summary>
public class VimComposerModeTests
{
    private static (VimComposerMode Vim, ComposerController Composer) New(bool enabled)
    {
        var vim = new VimComposerMode { Enabled = enabled };
        return (vim, new ComposerController());
    }

    private static KeyEvent Char(char c, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Char(new Rune(c), mods);

    [Test]
    public async Task Disabled_IsPurePassThrough()
    {
        (var vim, var composer) = New(enabled: false);
        _ = vim.HandleKey(Char('h'), composer);

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("h");
        await Assert.That(vim.NormalMode).IsFalse();
    }

    [Test]
    public async Task Enabled_StartsInInsert_UntilEsc()
    {
        (var vim, var composer) = New(enabled: true);
        _ = vim.HandleKey(Char('h'), composer); // insert-mode typing

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("h");

        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        await Assert.That(vim.NormalMode).IsTrue();
    }

    [Test]
    public async Task NormalMode_H_MovesCaret_WithoutInsertingOrEditing()
    {
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("ab");
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(2);

        _ = vim.HandleKey(Char('h'), composer); // caret back over 'b'

        await Assert.That(composer.Buffer.Cursor).IsEqualTo(1);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("ab"); // nothing inserted
    }

    [Test]
    public async Task NormalMode_UnboundLetter_FallsThroughToInsertAtCaret()
    {
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("ab");
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        _ = vim.HandleKey(Char('h'), composer); // caret between a and b

        _ = vim.HandleKey(Char('X'), composer); // unbound chord → insert-mode typing

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("aXb");
    }

    [Test]
    public async Task NormalMode_X_DeletesForward()
    {
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("ab");
        _ = composer.Buffer.MoveLeft(); // over 'b'
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);

        _ = vim.HandleKey(Char('x'), composer);

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("a");
    }

    [Test]
    public async Task NormalMode_I_A_ReturnToInsert()
    {
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("bc");
        _ = composer.Buffer.MoveLeft(); // over 'c' → between b and c
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);

        _ = vim.HandleKey(Char('a'), composer); // append: caret after 'c'
        _ = vim.HandleKey(Char('d'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("bcd");
        await Assert.That(vim.NormalMode).IsFalse();

        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        _ = vim.HandleKey(Char('I'), composer); // line start
        _ = vim.HandleKey(Char('a'), composer); // now inserts at start
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("abcd");
    }

    [Test]
    public async Task NormalMode_A_MovesPastEnd_Clamped()
    {
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("ab");
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer); // caret at end

        _ = vim.HandleKey(Char('a'), composer);
        _ = vim.HandleKey(Char('!'), composer);

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("ab!");
    }

    [Test]
    public async Task NormalMode_JK_RecallHistory()
    {
        (var vim, var composer) = New(enabled: true);
        composer.History.Push("older draft");
        composer.History.Push("newest draft");
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);

        _ = vim.HandleKey(Char('k'), composer); // recall previous
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("newest draft");

        _ = vim.HandleKey(Char('k'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("older draft");

        _ = vim.HandleKey(Char('j'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("newest draft");
    }

    [Test]
    public async Task NormalMode_Enter_Submits()
    {
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("do it");
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);

        var action = vim.HandleKey(KeyEvent.Simple(KeyCode.Enter), composer);

        await Assert.That(action).IsEqualTo(ComposerAction.Submitted);
    }

    [Test]
    public async Task NormalMode_LineJumps_0_Dollar()
    {
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("one\ntwo");
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer); // caret at end
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(7);

        _ = vim.HandleKey(Char('0'), composer);
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(4); // start of "two"

        _ = vim.HandleKey(Char('$'), composer);
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(7);
    }

    [Test]
    public async Task KeyContext_EscEntersNormal_AndDisabledIsPurePassThrough()
    {
        // Disabled: no chords are matched, not even Escape.
        (var vim, var composer) = New(enabled: false);
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        await Assert.That(vim.NormalMode).IsFalse();
        _ = vim.HandleKey(Char('h'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("h");

        // Enabled: Escape enters NORMAL, and an unbound letter still types through.
        (vim, composer) = New(enabled: true);
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        await Assert.That(vim.NormalMode).IsTrue();
        _ = vim.HandleKey(Char('X'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("X");
    }

    [Test]
    public async Task KeyContext_NormalChords_DriveEveryBoundPrimitive()
    {
        // #3437: every normal chord resolves through the composer.vim KeyContext.
        // Walk each action family (motion, line jump, delete, history, insert-exit)
        // and assert the observable buffer/story result.
        (var vim, var composer) = New(enabled: true);
        _ = composer.Buffer.InsertText("one two");
        _ = composer.Buffer.MoveToLineStart(); // caret 0
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        await Assert.That(vim.NormalMode).IsTrue();

        // motions: w/b/h/l move the caret, text untouched
        _ = vim.HandleKey(Char('w'), composer); // 0 -> 3 (past "one")
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(3);
        _ = vim.HandleKey(Char('w'), composer); // 3 -> 7 (past "two")
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(7);
        _ = vim.HandleKey(Char('b'), composer); // 7 -> 4 (start of "two")
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(4);
        _ = vim.HandleKey(Char('h'), composer); // 4 -> 3
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(3);
        _ = vim.HandleKey(Char('l'), composer); // 3 -> 4
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(4);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one two");

        // line jumps: 0 / $
        _ = vim.HandleKey(Char('0'), composer); // -> 0 (start of current line)
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(0);
        _ = vim.HandleKey(Char('$'), composer); // -> 7
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(7);

        // delete: x removes the rune under the caret
        _ = vim.HandleKey(Char('h'), composer); // 7 -> 6 (on 'o' of "two")
        _ = vim.HandleKey(Char('x'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one tw");

        // insert-exit chords: i / a / A / I all leave NORMAL
        _ = vim.HandleKey(Char('i'), composer);
        await Assert.That(vim.NormalMode).IsFalse();
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        _ = vim.HandleKey(Char('a'), composer);
        await Assert.That(vim.NormalMode).IsFalse();
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        _ = vim.HandleKey(Char('A'), composer);
        await Assert.That(vim.NormalMode).IsFalse();
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        _ = vim.HandleKey(Char('I'), composer);
        await Assert.That(vim.NormalMode).IsFalse();

        // history: j/k delegate to the composer's own Up/Down path
        (vim, composer) = New(enabled: true);
        composer.History.Push("older draft");
        composer.History.Push("newest draft");
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), composer);
        _ = vim.HandleKey(Char('k'), composer); // recall previous
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("newest draft");
        _ = vim.HandleKey(Char('k'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("older draft");
        _ = vim.HandleKey(Char('j'), composer);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("newest draft");
    }
}
