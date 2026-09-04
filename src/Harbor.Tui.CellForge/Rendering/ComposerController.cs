using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Rendering;

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
/// kill/yank chords (Ctrl+K/U/W + Ctrl+Y), Ctrl+C semantics, everything else
/// ignored.
///
/// CF-B-005 history-through-store contract: Up/Down recall is a store
/// transition — the keys map to <see cref="InputMsg.HistoryUp"/> /
/// <see cref="InputMsg.HistoryDown"/> (see <c>InputModel.cs</c>) and are
/// applied to the <see cref="PromptHistory"/> walk, so the in-flight draft is
/// saved on the first Up and restored exactly once by the final Down
/// (readline semantics owned by <see cref="PromptHistory"/>). Text and cursor
/// stay store-owned: the sync mirrors
/// <c>CellForgeTuiRenderer.SyncInputFromState</c> read-only (text change pins
/// the caret to the end of the text); the renderer itself is untouched.
/// </summary>
public sealed class ComposerController
{
    public PromptBuffer Buffer { get; } = new();

    /// <summary>Readline-style submitted-prompt history owned by the composer.</summary>
    public PromptHistory History { get; } = new();

    /// <summary>Whether a history-recall walk is in flight (Up without the final Down yet).</summary>
    public bool IsRecalling => History.IsWalking;

    /// <summary>
    /// Records a store-submitted line into the MRU rail (CF-B-005 choke point
    /// for the submit path: trims, drops empties, collapses consecutive dupes,
    /// evicts the oldest past <see cref="PromptHistory.DefaultCapacity"/>).
    /// </summary>
    public void PushSubmitted(string entry) => History.Push(entry);

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

                History.PushSubmitted(Buffer.SnapshotText());
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

            case KeyCode.Char when key.Character == new Rune('z') && mods == KeyModifiers.Ctrl:
                // No history ⇒ ignored, matching the Ctrl+Y dead-yank contract.
                return Buffer.Undo().Kind == EditOutcomeKind.Unchanged
                    ? ComposerAction.Ignored
                    : ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('Z') && mods == (KeyModifiers.Ctrl | KeyModifiers.Shift):
                // Kitty CSI-u reports the shifted codepoint; legacy terminals
                // cannot express C-S-z distinctly and stay on undo-only.
                return Buffer.Redo().Kind == EditOutcomeKind.Unchanged
                    ? ComposerAction.Ignored
                    : ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('k') && mods == KeyModifiers.Ctrl:
                _ = Buffer.DeleteToLineEnd();
                return ComposerAction.Edited;

            // Readline yank: Ctrl+Y pastes the last kill recorded on the
            // buffer (Ctrl+U/W/K, Alt+D) at the caret; nothing killed ⇒ ignored.
            case KeyCode.Char when key.Character == new Rune('y') && mods == KeyModifiers.Ctrl:
                if (Buffer.LastKill is not { Length: > 0 } kill)
                {
                    return ComposerAction.Ignored;
                }

                _ = Buffer.InsertText(kill);
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

            case KeyCode.Char when key.Character == new Rune('h') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = MarkdownEditOps.ToggleHeading(Buffer);
                return ComposerAction.Edited;

            case KeyCode.Char when key.Character == new Rune('l') && (mods & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (mods & KeyModifiers.Ctrl) == 0:
                _ = MarkdownEditOps.ToggleListItem(Buffer);
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
                // CF-B-005: history recall is a store transition — Up arrives as
                // InputMsg.HistoryUp (UiStore → InputMsg.Update). The in-flight
                // draft is saved on this first Up; PromptHistory owns the walk.
                // First logical line + available history ⇒ recall instead of caret movement.
                if (Buffer.LineIndexOf(Buffer.Cursor) == 0 && TryRecallViaStore(new InputMsg.HistoryUp(), Buffer.SnapshotText(), out var previous))
                {
                    Recall(previous);
                    return ComposerAction.Edited;
                }

                _ = Buffer.MoveUp();
                return ComposerAction.Edited;

            case KeyCode.Down when (mods & (KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) == 0:
                // CF-B-005: Down arrives as InputMsg.HistoryDown; the final step
                // restores the saved draft exactly once (readline), then the
                // walk ends and Down is plain caret movement again.
                if (Buffer.LineIndexOf(Buffer.Cursor) == Buffer.LineCount - 1 && TryRecallViaStore(new InputMsg.HistoryDown(), Buffer.SnapshotText(), out var next))
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
        // Cursor-from-store contract (CF-B-005): mirrors
        // CellForgeTuiRenderer.SyncInputFromState read-only — a text change
        // pins the caret to the end of the text, and the composer follows via
        // MoveTo. End-of-text keeps composer and store caret coherent; the
        // renderer itself is untouched.
        _ = Buffer.MoveTo(Buffer.Length);
    }

    /// <summary>
    /// Store-message entry point for history recall (CF-B-005): maps
    /// <see cref="InputMsg.HistoryUp"/> / <see cref="InputMsg.HistoryDown"/>
    /// onto the <see cref="PromptHistory"/> walk. HistoryUp captures
    /// <paramref name="draft"/> on the first step; the final HistoryDown
    /// restores it exactly once. Returns false at the walk boundaries (caller
    /// falls back to caret movement) and for any other message.
    /// </summary>
    private bool TryRecallViaStore(InputMsg message, string draft, out string entry)
    {
        switch (message)
        {
            case InputMsg.HistoryUp:
                return History.TryRecallPrevious(draft, out entry);
            case InputMsg.HistoryDown:
                return History.TryRecallNext(out entry);
            default:
                entry = string.Empty;
                return false;
        }
    }
}
