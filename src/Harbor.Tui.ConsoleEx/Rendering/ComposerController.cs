using System.Text;
using Harbor.Tui.ConsoleEx.Input;

namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>What the composer did with the key.</summary>
public enum ComposerAction : byte
{
    /// <summary>Key ignored / not handled.</summary>
    Ignored = 0,

    /// <summary>Buffer or caret changed — repaint the prompt.</summary>
    Edited = 1,

    /// <summary>Enter without modifiers — submit requested.</summary>
    Submitted = 2,

    /// <summary>Ctrl+C on empty buffer — abort signal (caller quits/cancels).</summary>
    Aborted = 3,
}

/// <summary>
/// Keyboard routing for the inline composer: kitty-modifier aware Enter split
/// (plain Enter submits; Shift+Enter / Alt+Enter insert a newline — the whole
/// reason CE-0 pushed disambiguate flags), navigation/editing keys into
/// <see cref="PromptBuffer"/>, prompt-history recall (<see cref="History"/>:
/// Up from the first line walks back, Down from the last line forward),
/// Ctrl+C semantics, everything else ignored.
/// </summary>
public sealed class ComposerController
{
    public PromptBuffer Buffer { get; } = new();

    /// <summary>Readline-style submitted-prompt history owned by the composer.</summary>
    public PromptHistory History { get; } = new();

    /// <summary>Routes one decoded key event. Pure state machine over the buffer.</summary>
    public ComposerAction HandleKey(in KeyEvent key)
    {
        if (key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
        {
            return ComposerAction.Ignored;
        }

        var mods = key.Modifiers;

        switch (key.Key)
        {
            case KeyCode.Enter:
                if ((mods & KeyModifiers.Ctrl) != 0)
                {
                    return ComposerAction.Ignored;
                }

                if ((mods & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0)
                {
                    _ = Buffer.Insert(new Rune('\n'));
                    return ComposerAction.Edited;
                }

                History.Push(Buffer.SnapshotText());
                return ComposerAction.Submitted;

            // Plain text never arrives with Alt set: legacy terminals encode
            // M-x as ESC-prefix (Meta), kitty/CSI-u set the Alt bit. Routing
            // those to insertion made readline chords unreachable.
            case KeyCode.Char when (mods & (KeyModifiers.Ctrl | KeyModifiers.Meta | KeyModifiers.Alt)) == 0:
                _ = Buffer.Insert(key.Character);
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('c') && (mods & KeyModifiers.Ctrl) != 0:
                return Buffer.IsEmpty ? ComposerAction.Aborted : ClearAll();

            case KeyCode.Char when key.Character == new Rune('u') && (mods & KeyModifiers.Ctrl) != 0:
            {
                _ = Buffer.DeleteToLineStart();
                return ComposerAction.Edited;
            }

            case KeyCode.Char when key.Character == new Rune('w') && (mods & KeyModifiers.Ctrl) != 0:
            {
                _ = Buffer.DeleteWordBackward();
                return ComposerAction.Edited;
            }

            // Readline kill/yank-lite family: Ctrl+A/E line jumps, Ctrl+K
            // kill-to-line-end, Alt+B/D/F word-wise move/delete.
            case KeyCode.Char when key.Character == new Rune('a') && mods == KeyModifiers.Ctrl:
                _ = Buffer.MoveToLineStart();
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('e') && mods == KeyModifiers.Ctrl:
                _ = Buffer.MoveToLineEnd();
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('k') && mods == KeyModifiers.Ctrl:
                _ = Buffer.DeleteToLineEnd();
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('b') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = Buffer.MoveWordLeft();
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('f') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = Buffer.MoveWordRight();
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('d') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = Buffer.DeleteWordForward();
                return ComposerAction.Edited;

            // Markdown composer chords: M-s bold, M-i italic, M-c inline code —
            // they toggle around the word at the caret via MarkdownEditOps.
            case KeyCode.Char when key.Character == new Rune('s') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = MarkdownEditOps.ToggleWrap(Buffer, "**");
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('i') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = MarkdownEditOps.ToggleWrap(Buffer, "*");
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('c') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = MarkdownEditOps.ToggleWrap(Buffer, "`");
                return ComposerAction.Edited;

            case KeyCode.Left when (mods & (KeyModifiers.Ctrl | KeyModifiers.Meta)) != 0 && (mods & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0:
                _ = Buffer.MoveWordLeft();
                return ComposerAction.Edited;

            case KeyCode.Right when (mods & (KeyModifiers.Ctrl | KeyModifiers.Meta)) != 0 && (mods & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0:
                _ = Buffer.MoveWordRight();
                return ComposerAction.Edited;

            case KeyCode.Backspace when mods == KeyModifiers.None || mods == KeyModifiers.Shift:
                _ = Buffer.Backspace();
                return ComposerAction.Edited;

            case KeyCode.Delete when mods == KeyModifiers.None:
                _ = Buffer.DeleteForward();
                return ComposerAction.Edited;

            case KeyCode.Left when (mods & (KeyModifiers.Ctrl | KeyModifiers.Meta)) == 0:
                _ = Buffer.MoveLeft();
                return ComposerAction.Edited;

            case KeyCode.Right when (mods & (KeyModifiers.Ctrl | KeyModifiers.Meta)) == 0:
                _ = Buffer.MoveRight();
                return ComposerAction.Edited;

            case KeyCode.Up when (mods & (KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) == 0:
                // First logical line + available history ⇒ recall instead of caret movement.
                if (Buffer.LineIndexOf(Buffer.Cursor) == 0 && History.TryRecallPrevious(Buffer.SnapshotText(), out var previous))
                {
                    Recall(previous);
                    return ComposerAction.Edited;
                }

                _ = Buffer.MoveUp();
                return ComposerAction.Edited;

            case KeyCode.Down when (mods & (KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) == 0:
                if (Buffer.LineIndexOf(Buffer.Cursor) == Buffer.LineCount - 1 && History.TryRecallNext(out var next))
                {
                    Recall(next);
                    return ComposerAction.Edited;
                }

                _ = Buffer.MoveDown();
                return ComposerAction.Edited;

            case KeyCode.Home when mods == KeyModifiers.None:
                _ = Buffer.MoveToLineStart();
                return ComposerAction.Edited;

            case KeyCode.End when mods == KeyModifiers.None:
                _ = Buffer.MoveToLineEnd();
                return ComposerAction.Edited;

            default:
                return ComposerAction.Ignored;
        }
    }

    private ComposerAction ClearAll()
    {
        Buffer.Clear();
        History.Reset();
        return ComposerAction.Edited;
    }

    private void Recall(string text)
    {
        Buffer.Clear();
        _ = Buffer.InsertText(text);
    }
}
