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

    /// <summary>Key-family dispatch table (#197): a new key family adds a handler, never an edit here.</summary>
    private readonly IKeyHandler[] _handlers;

    public ComposerController()
    {
        _handlers =
        [
            new EnterHandler(this),
            new CharHandler(this),
            new HistoryHandler(this),
            new EditHandler(this),
        ];
    }

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

        foreach (var handler in _handlers)
        {
            if (handler.Matches(key))
            {
                return handler.Handle(key);
            }
        }

        return ComposerAction.Ignored;
    }

    /// <summary>Strategy contract for one key family (#197).</summary>
    private interface IKeyHandler
    {
        bool Matches(in KeyEvent key);
        ComposerAction Handle(in KeyEvent key);
    }

    private sealed class EnterHandler(ComposerController owner) : IKeyHandler
    {
        public bool Matches(in KeyEvent key) => key.Key == KeyCode.Enter;
        public ComposerAction Handle(in KeyEvent key) => owner.HandleEnter(key.Modifiers);
    }

    private sealed class CharHandler(ComposerController owner) : IKeyHandler
    {
        public bool Matches(in KeyEvent key) => key.Key == KeyCode.Char;
        public ComposerAction Handle(in KeyEvent key) => owner.HandleCharKey(key);
    }

    private sealed class HistoryHandler(ComposerController owner) : IKeyHandler
    {
        public bool Matches(in KeyEvent key) => key.Key is KeyCode.Up or KeyCode.Down;
        public ComposerAction Handle(in KeyEvent key) => owner.HandleHistoryKey(key);
    }

    private sealed class EditHandler(ComposerController owner) : IKeyHandler
    {
        public bool Matches(in KeyEvent key) => key.Key is KeyCode.Left or KeyCode.Right
            or KeyCode.Backspace or KeyCode.Delete or KeyCode.Home or KeyCode.End;
        public ComposerAction Handle(in KeyEvent key) => owner.HandleEditKey(key);
    }

    /// <summary>One key binding: predicate + effect. Tables below preserve the original order exactly.</summary>
    private readonly record struct KeyBinding(
        Func<KeyEvent, bool> Matches,
        Func<ComposerController, KeyEvent, ComposerAction> Run);

    /// <summary>
    ///     Enter split, executed from the store-owned decision (#359):
    ///     <see cref="EnterKeyPolicy"/> maps the modifiers to
    ///     <see cref="ChatAction"/> (Ctrl+Enter → ignore, Shift/Alt+Enter →
    ///     newline, plain Enter → submit) and the composer only applies the
    ///     buffer effect — the same transition <see cref="ChatAppReducer.Update"/>
    ///     performs for <see cref="AppMsg.KeyInput"/> so key behavior cannot
    ///     diverge per renderer.
    /// </summary>
    private ComposerAction HandleEnter(KeyModifiers mods)
    {
        return EnterKeyPolicy.Resolve(
            (mods & KeyModifiers.Ctrl) != 0,
            (mods & KeyModifiers.Shift) != 0,
            (mods & KeyModifiers.Alt) != 0,
            (mods & KeyModifiers.Meta) != 0) switch
        {
            ChatAction.InsertNewline => InsertNewline(),
            ChatAction.Submit => SubmitDraft(),
            _ => ComposerAction.Ignored,
        };
    }

    private ComposerAction InsertNewline()
    {
        _ = Buffer.Insert(new Rune('\n'));
        return ComposerAction.Edited;
    }

    private ComposerAction SubmitDraft()
    {
        History.PushSubmitted(Buffer.SnapshotText());
        return ComposerAction.Submitted;
    }

    /// <summary>Character keys: plain insertion, Ctrl chords, Alt/Meta word + markdown chords.</summary>
    private ComposerAction HandleCharKey(KeyEvent key)
    {
        var mods = key.Modifiers;

        // Plain text never arrives with Alt set: legacy terminals encode
        // M-x as ESC-prefix (Meta), kitty/CSI-u set the Alt bit. Routing
        // those to insertion made readline chords unreachable.
        if (mods.AcceptsTypedChar())
        {
            _ = Buffer.Insert(key.Character);
            return ComposerAction.Edited;
        }

        foreach (var binding in CharBindings)
        {
            if (binding.Matches(key))
            {
                return binding.Run(this, key);
            }
        }

        return ComposerAction.Ignored;
    }

    /// <summary>
    ///     Character-chord table (#197): plain insertion, Ctrl chords, Alt/Meta
    ///     word + markdown chords. Row order matches the original if-chain exactly.
    /// </summary>
    private static readonly KeyBinding[] CharBindings =
    [
        new(static k => k.Character == new Rune('c') && (k.Modifiers & KeyModifiers.Ctrl) != 0,
            static (c, key) => c.Buffer.IsEmpty ? ComposerAction.Aborted : c.ClearAll()),
        new(static k => k.Character == new Rune('u') && (k.Modifiers & KeyModifiers.Ctrl) != 0,
            static (c, key) => { _ = c.Buffer.DeleteToLineStart(); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('w') && (k.Modifiers & KeyModifiers.Ctrl) != 0,
            static (c, key) => { _ = c.Buffer.DeleteWordBackward(); return ComposerAction.Edited; }),
        // Readline kill/yank-lite family: Ctrl+A/E line jumps, Ctrl+K
        // kill-to-line-end, Alt+B/D/F word-wise move/delete.
        new(static k => k.Character == new Rune('a') && k.Modifiers == KeyModifiers.Ctrl,
            static (c, key) => { _ = c.Buffer.MoveToLineStart(); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('e') && k.Modifiers == KeyModifiers.Ctrl,
            static (c, key) => { _ = c.Buffer.MoveToLineEnd(); return ComposerAction.Edited; }),
        // No history ⇒ ignored, matching the Ctrl+Y dead-yank contract.
        new(static k => k.Character == new Rune('z') && k.Modifiers == KeyModifiers.Ctrl,
            static (c, key) => c.Buffer.Undo().Kind == EditOutcomeKind.Unchanged
                ? ComposerAction.Ignored
                : ComposerAction.Edited),
        // Kitty CSI-u reports the shifted codepoint; legacy terminals
        // cannot express C-S-z distinctly and stay on undo-only.
        new(static k => k.Character == new Rune('Z') && k.Modifiers == (KeyModifiers.Ctrl | KeyModifiers.Shift),
            static (c, key) => c.Buffer.Redo().Kind == EditOutcomeKind.Unchanged
                ? ComposerAction.Ignored
                : ComposerAction.Edited),
        new(static k => k.Character == new Rune('k') && k.Modifiers == KeyModifiers.Ctrl,
            static (c, key) => { _ = c.Buffer.DeleteToLineEnd(); return ComposerAction.Edited; }),
        // Readline yank: Ctrl+Y pastes the last kill recorded on the
        // buffer (Ctrl+U/W/K, Alt+D) at the caret; nothing killed ⇒ ignored.
        new(static k => k.Character == new Rune('y') && k.Modifiers == KeyModifiers.Ctrl,
            static (c, key) =>
            {
                if (c.Buffer.LastKill is not { Length: > 0 } kill)
                {
                    return ComposerAction.Ignored;
                }

                _ = c.Buffer.InsertText(kill);
                return ComposerAction.Edited;
            }),
        new(static k => k.Character == new Rune('b') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = c.Buffer.MoveWordLeft(); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('f') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = c.Buffer.MoveWordRight(); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('d') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = c.Buffer.DeleteWordForward(); return ComposerAction.Edited; }),
        // Markdown composer chords: M-s bold, M-i italic, M-c inline code —
        // they toggle around the word at the caret via MarkdownEditOps.
        new(static k => k.Character == new Rune('s') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = MarkdownEditOps.ToggleWrap(c.Buffer, "**"); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('i') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = MarkdownEditOps.ToggleWrap(c.Buffer, "*"); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('c') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = MarkdownEditOps.ToggleWrap(c.Buffer, "`"); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('h') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = MarkdownEditOps.ToggleHeading(c.Buffer); return ComposerAction.Edited; }),
        new(static k => k.Character == new Rune('l') && (k.Modifiers & (KeyModifiers.Meta | KeyModifiers.Alt)) != 0 && (k.Modifiers & KeyModifiers.Ctrl) == 0,
            static (c, key) => { _ = MarkdownEditOps.ToggleListItem(c.Buffer); return ComposerAction.Edited; }),
    ];

    /// <summary>Caret movement + deletion keys (word jumps, Backspace/Delete, Home/End).</summary>
    private ComposerAction HandleEditKey(KeyEvent key)
    {
        foreach (var binding in EditBindings)
        {
            if (binding.Matches(key))
            {
                return binding.Run(this, key);
            }
        }

        return ComposerAction.Ignored;
    }

    /// <summary>Edit-key table (#197). Row order matches the original if-chain exactly.</summary>
    private static readonly KeyBinding[] EditBindings =
    [
        new(static k => k.Key == KeyCode.Left && (k.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta)) != 0 && (k.Modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0,
            static (c, key) => { _ = c.Buffer.MoveWordLeft(); return ComposerAction.Edited; }),
        new(static k => k.Key == KeyCode.Right && (k.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta)) != 0 && (k.Modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0,
            static (c, key) => { _ = c.Buffer.MoveWordRight(); return ComposerAction.Edited; }),
        new(static k => k.Key == KeyCode.Backspace && k.Modifiers.AcceptsTypedChar(),
            static (c, key) => { _ = c.Buffer.Backspace(); return ComposerAction.Edited; }),
        new(static k => k.Key == KeyCode.Delete && k.Modifiers.IsUnmodified(),
            static (c, key) => { _ = c.Buffer.DeleteForward(); return ComposerAction.Edited; }),
        new(static k => k.Key == KeyCode.Left && (k.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta)) == 0,
            static (c, key) => { _ = c.Buffer.MoveLeft(); return ComposerAction.Edited; }),
        new(static k => k.Key == KeyCode.Right && (k.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta)) == 0,
            static (c, key) => { _ = c.Buffer.MoveRight(); return ComposerAction.Edited; }),
        new(static k => k.Key == KeyCode.Home && k.Modifiers.IsUnmodified(),
            static (c, key) => { _ = c.Buffer.MoveToLineStart(); return ComposerAction.Edited; }),
        new(static k => k.Key == KeyCode.End && k.Modifiers.IsUnmodified(),
            static (c, key) => { _ = c.Buffer.MoveToLineEnd(); return ComposerAction.Edited; }),
    ];

    /// <summary>Up/Down: history recall at the buffer edges, caret movement otherwise.</summary>
    private ComposerAction HandleHistoryKey(KeyEvent key)
    {
        var mods = key.Modifiers;

        // Deliberately NOT KeyModifierGate.AcceptsTypedChar: history recall
        // refuses Shift as well, because Shift+Up has no meaning here — it is not
        // a case anyone can type into a history. That is the OTHER half of the
        // rule (the "unmodified command" family) rather than the typing half, and
        // collapsing the two would make Shift+Up walk the draft.
        if (mods != KeyModifiers.None)
        {
            return ComposerAction.Ignored;
        }

        if (key.Key == KeyCode.Up)
        {
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
        }

        if (key.Key == KeyCode.Down)
        {
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
        }

        return ComposerAction.Ignored;
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
