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
/// <see cref="PromptBuffer"/>, Ctrl+C semantics, everything else ignored.
/// </summary>
public sealed class ComposerController
{
    public PromptBuffer Buffer { get; } = new();

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

                return ComposerAction.Submitted;

            case KeyCode.Char when (mods & (KeyModifiers.Ctrl | KeyModifiers.Meta)) == 0:
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
                _ = Buffer.MoveUp();
                return ComposerAction.Edited;

            case KeyCode.Down when (mods & (KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) == 0:
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
        return ComposerAction.Edited;
    }
}
