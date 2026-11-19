using System.Collections.Frozen;
using System.Text;
using Harbor.Abstractions.Speech;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Ui.Framework.Rendering.Input;
using Engine = Harbor.Tui.CellForge.Input;

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

    /// <summary>Ctrl+D — start or stop dictation (host runs the async toggle).</summary>
    DictationToggle = 4,
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
/// CF-B-005 history contract: Up/Down recall walks the
/// <see cref="PromptHistory"/> rail, so the in-flight draft is saved on the
/// first Up and restored exactly once by the final Down (readline semantics
/// owned by <see cref="PromptHistory"/>). The store applies the same walk on
/// its own <c>InputMsg.HistoryUp/HistoryDown</c> (see <c>InputModel.cs</c>) —
/// this engine names the direction as <see cref="HistoryStep"/> rather than the
/// store's marker records, which is what let #435 delete the State edge.
/// Text and cursor stay store-owned: the sync mirrors
/// <c>ChatScreenProjector</c> (issue #1202; reached via
/// <c>CellForgeTuiRenderer.ProjectStateIntoWidgets</c>) read-only (text change
/// pins the caret to the end of the text); the renderer itself is untouched.
/// </summary>
public sealed class ComposerController
{
    public PromptBuffer Buffer { get; } = new();

    /// <summary>
    ///     Which way a history-recall walk steps (epic #33 T2). A local tag, not a
    ///     port of a port: it replaces <c>InputMsg.HistoryUp</c> /
    ///     <c>InputMsg.HistoryDown</c>, which are empty marker records whose whole
    ///     content is the direction they name. They carried no payload and no policy,
    ///     so naming the direction directly loses nothing — and the walk they drove
    ///     (<see cref="PromptHistory" />) has always been engine-local.
    /// </summary>
    private enum HistoryStep : byte
    {
        /// <summary>Walk back towards the oldest entry.</summary>
        Previous = 0,

        /// <summary>Walk forward towards the newest entry.</summary>
        Next = 1,
    }

    /// <summary>Readline-style submitted-prompt history owned by the composer.</summary>
    public PromptHistory History { get; } = new();

    // ── Queued prompts (opencode steal O7, #1176) ─────────────────────────
    // Submit-while-busy never interrupts the running turn: the payload waits
    // here (oldest-first) until the pipeline drains it when idle. The queue
    // is composer-owned so undo/cancel can hand the text straight back to
    // the buffer (opencode's queued_prompt.undo + cancel→projectedPromptInput)
    // and the dock-counter reads its depth from one place.

    /// <summary>Waiting prompts, oldest-first.</summary>
    private readonly List<string> _queued = [];

    /// <summary>
    /// Cached materialization of <see cref="_queued"/>, rebuilt only on mutation so
    /// a render-time read (<c>QueuedPromptDock.Open</c>) allocates nothing (#2653).
    /// </summary>
    private IReadOnlyList<string> _queuedSnapshot = Array.Empty<string>();

    /// <summary>Number of prompts waiting for the agent to idle (dock-counter source).</summary>
    public int QueuedCount => _queued.Count;

    /// <summary>Oldest-first snapshot of the waiting prompts (queue-dialog source).</summary>
    public IReadOnlyList<string> QueuedPrompts => _queuedSnapshot;

    /// <summary>Rebuilds the cached view after any change to <see cref="_queued"/>.</summary>
    private void RefreshQueuedSnapshot() => _queuedSnapshot = _queued.ToArray();

    /// <summary>Dock-counter line for the prompt rail; null when the queue is empty.</summary>
    public string? QueuedCounterText() =>
        _queued.Count > 0 ? $"⏳ {_queued.Count} queued" : null;

    /// <summary>Queues a busy-time submit: trims, drops empties, keeps every
    /// deliberate submit (no dupe-collapse — each stroke is intentional).</summary>
    public void EnqueueQueued(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        _queued.Add(trimmed);
        RefreshQueuedSnapshot();
    }

    /// <summary>FIFO take for the idle drain; false when the queue is empty.</summary>
    public bool TryDequeueQueued(out string text)
    {
        if (_queued.Count == 0)
        {
            text = string.Empty;
            return false;
        }

        text = _queued[0];
        _queued.RemoveAt(0);
        RefreshQueuedSnapshot();
        return true;
    }

    /// <summary>Undo: drops the NEWEST queued prompt and hands its text back
    /// (caller restores it via <see cref="RestoreToComposer"/>).</summary>
    public bool TryUndoQueued(out string text)
    {
        if (_queued.Count == 0)
        {
            text = string.Empty;
            return false;
        }

        int last = _queued.Count - 1;
        text = _queued[last];
        _queued.RemoveAt(last);
        RefreshQueuedSnapshot();
        return true;
    }

    /// <summary>Drops every waiting prompt (session switch: the queue never survives it).</summary>
    public void ClearQueued()
    {
        _queued.Clear();
        RefreshQueuedSnapshot();
    }

    /// <summary>Abort path: drops the queue but hands the newest payload back
    /// instead of losing it (opencode's cancel→composer); false when empty.</summary>
    public bool ClearQueuedReturningLast(out string? last)
    {
        if (_queued.Count == 0)
        {
            last = null;
            return false;
        }

        last = _queued[^1];
        _queued.Clear();
        RefreshQueuedSnapshot();
        return true;
    }

    /// <summary>Returns an undo/cancel payload to the composer (caret lands at
    /// the end of the restored text, per the <see cref="PromptBuffer"/> contract).</summary>
    public void RestoreToComposer(string text)
    {
        _ = Buffer.InsertText(text);
    }

    // ── Dictation (#2541) ───────────────────────────────────────────────────
    // The buffer from #2504 owns the text rules; this controller owns the
    // session lifetime and mirrors Dictation.Text into PromptBuffer so every
    // existing submit/paint path reads the same draft. The backend is a seam:
    // production uses the offline placeholder until a real one is wired.
    private readonly ISpeechToText _speechToText;
    private readonly DictationBuffer _dictation = new();
    private CancellationTokenSource? _dictationCts;
    private Task? _dictationTask;

    /// <summary>Live transcript buffer; status chrome reads <see cref="DictationBuffer.StatusLabel"/>.</summary>
    public DictationBuffer Dictation => _dictation;

    /// <summary>The backend driving dictation. Exposed so the host can report "offline".</summary>
    public ISpeechToText SpeechToText => _speechToText;

    /// <summary>Refusal reason from the last <see cref="ToggleDictationAsync"/> start attempt; null on success.</summary>
    public string? DictationRejection { get; private set; }

    /// <summary>
    ///     Gesture→opaque-action-id table for the composer's key families (#3621).
    ///     Chords are data here; <see cref="ComposerAction"/> stays the host contract
    ///     and the <see cref="PromptBuffer"/> methods stay the effects.
    /// </summary>
    private readonly Engine.KeyContext _composerKeyContext = BuildKeyContext();

    /// <summary>
    ///     The composer's key context. Bindings are data, so a host may rebind or
    ///     unbind a gesture; the action ids stay opaque to the engine primitive.
    /// </summary>
    public Engine.KeyContext ComposerKeyContext => _composerKeyContext;

    /// <summary>Composer with the offline (no-audio) dictation placeholder.</summary>
    public ComposerController() : this(new OfflineSpeechToText())
    {
    }

    /// <summary>Composer with an injected recognition backend.</summary>
    public ComposerController(ISpeechToText speechToText)
    {
        _speechToText = speechToText ?? throw new ArgumentNullException(nameof(speechToText));
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
    public ComposerAction HandleKey(in KeyEvent key) => HandleKeyCore(ToEngineKey(key));

    /// <summary>Routes one decoded engine key event (same behavior as the UI-framework overload).</summary>
    public ComposerAction HandleKey(in Engine.KeyEvent key) => HandleKeyCore(key);

    private ComposerAction HandleKeyCore(in Engine.KeyEvent key)
    {
        if (key.EventType != Engine.KeyEventType.Press && key.EventType != Engine.KeyEventType.Repeat)
        {
            return ComposerAction.Ignored;
        }

        // Ctrl+D is a command chord, not text: exact Ctrl, press-only so a held key
        // cannot toggle twice. The host performs the async toggle; the state machine
        // only names the gesture. Kept in the controller (not KeyContext) because it
        // is press-only and returns a host action distinct from buffer edits.
        if (key.EventType == Engine.KeyEventType.Press
            && key.Key == Engine.KeyCode.Char
            && (key.Character.Value == 'd' || key.Character.Value == 'D')
            && key.Modifiers == Engine.KeyModifiers.Ctrl)
        {
            return ComposerAction.DictationToggle;
        }

        // Enter split is handled by EnterPolicy (#359 kitty-disambiguate) and stays here.
        if (key.Key == Engine.KeyCode.Enter)
        {
            return HandleEnter((KeyModifiers)(byte)key.Modifiers);
        }

        // Plain text insertion: AcceptsTypedChar split stays in the controller (not KeyContext).
        if (key.Key == Engine.KeyCode.Char && Engine.KeyModifierGate.AcceptsTypedChar(key.Modifiers))
        {
            _ = Buffer.Insert(key.Character);
            return ComposerAction.Edited;
        }

        // Gesture→opaque action id for char-chords, edit-chords and history keys (#3621).
        if (!_composerKeyContext.TryMatch(key, out var hit))
        {
            return ComposerAction.Ignored;
        }

        // A no-action binding deliberately swallows the gesture; an action id the
        // host does not implement is the same outcome, not a fall-through.
        return hit.ActionId is { } actionId && ComposerActions.TryGetValue(actionId, out var run)
            ? run(this)
            : ComposerAction.Ignored;
    }

    private static Engine.KeyEvent ToEngineKey(in KeyEvent key) =>
        new((Engine.KeyCode)(byte)key.Key, key.Character, (Engine.KeyModifiers)(byte)key.Modifiers,
            (Engine.KeyEventType)(byte)key.EventType, key.IsKittyEncoded, key.Codepoint);

    /// <summary>
    ///     Action-id dispatch for <see cref="_composerKeyContext"/> (#3621). The ids
    ///     are the opaque strings bound in <see cref="BuildKeyContext"/>; each arm is
    ///     one of the PromptBuffer/PromptHistory primitives the old tables called. A
    ///     frozen table keeps the ids data instead of a string-switch ladder, and the
    ///     static lambdas are cached once, so dispatch allocates nothing per key.
    /// </summary>
    private static readonly FrozenDictionary<string, Func<ComposerController, ComposerAction>> ComposerActions =
        new Dictionary<string, Func<ComposerController, ComposerAction>>(StringComparer.Ordinal)
        {
            // Char chords
            ["char.ctrl-c"] = static c => c.Buffer.IsEmpty ? ComposerAction.Aborted : c.ClearAll(),
            ["char.ctrl-u"] = static c => { _ = c.Buffer.DeleteToLineStart(); return ComposerAction.Edited; },
            ["char.ctrl-w"] = static c => { _ = c.Buffer.DeleteWordBackward(); return ComposerAction.Edited; },
            ["char.ctrl-a"] = static c => { _ = c.Buffer.MoveToLineStart(); return ComposerAction.Edited; },
            ["char.ctrl-e"] = static c => { _ = c.Buffer.MoveToLineEnd(); return ComposerAction.Edited; },
            ["char.ctrl-z"] = static c => c.Buffer.Undo().Kind == EditOutcomeKind.Unchanged
                ? ComposerAction.Ignored
                : ComposerAction.Edited,
            ["char.ctrl-shift-z"] = static c => c.Buffer.Redo().Kind == EditOutcomeKind.Unchanged
                ? ComposerAction.Ignored
                : ComposerAction.Edited,
            ["char.ctrl-k"] = static c => { _ = c.Buffer.DeleteToLineEnd(); return ComposerAction.Edited; },
            ["char.ctrl-y"] = static c => c.Yank(),
            ["char.meta-b"] = static c => { _ = c.Buffer.MoveWordLeft(); return ComposerAction.Edited; },
            ["char.meta-f"] = static c => { _ = c.Buffer.MoveWordRight(); return ComposerAction.Edited; },
            ["char.meta-d"] = static c => { _ = c.Buffer.DeleteWordForward(); return ComposerAction.Edited; },
            ["char.meta-s"] = static c => { _ = MarkdownEditOps.ToggleWrap(c.Buffer, "**"); return ComposerAction.Edited; },
            ["char.meta-i"] = static c => { _ = MarkdownEditOps.ToggleWrap(c.Buffer, "*"); return ComposerAction.Edited; },
            ["char.meta-c"] = static c => { _ = MarkdownEditOps.ToggleWrap(c.Buffer, "`"); return ComposerAction.Edited; },
            ["char.meta-h"] = static c => { _ = MarkdownEditOps.ToggleHeading(c.Buffer); return ComposerAction.Edited; },
            ["char.meta-l"] = static c => { _ = MarkdownEditOps.ToggleListItem(c.Buffer); return ComposerAction.Edited; },

            // Edit chords
            ["edit.word-left"] = static c => { _ = c.Buffer.MoveWordLeft(); return ComposerAction.Edited; },
            ["edit.word-right"] = static c => { _ = c.Buffer.MoveWordRight(); return ComposerAction.Edited; },
            ["edit.backspace"] = static c => { _ = c.Buffer.Backspace(); return ComposerAction.Edited; },
            ["edit.delete"] = static c => { _ = c.Buffer.DeleteForward(); return ComposerAction.Edited; },
            ["edit.left"] = static c => { _ = c.Buffer.MoveLeft(); return ComposerAction.Edited; },
            ["edit.right"] = static c => { _ = c.Buffer.MoveRight(); return ComposerAction.Edited; },
            ["edit.home"] = static c => { _ = c.Buffer.MoveToLineStart(); return ComposerAction.Edited; },
            ["edit.end"] = static c => { _ = c.Buffer.MoveToLineEnd(); return ComposerAction.Edited; },

            // History
            ["history.up"] = static c => c.HandleHistoryUp(),
            ["history.down"] = static c => c.HandleHistoryDown(),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private ComposerAction Yank()
    {
        if (Buffer.LastKill is not { Length: > 0 } kill)
        {
            return ComposerAction.Ignored;
        }

        _ = Buffer.InsertText(kill);
        return ComposerAction.Edited;
    }

    private ComposerAction HandleHistoryUp()
    {
        // CF-B-005: history recall is a store transition — the store applies
        // the same walk on its own InputMsg.Update. The in-flight draft is
        // saved on this first Up; PromptHistory owns the walk.
        // First logical line + available history ⇒ recall instead of caret movement.
        if (Buffer.LineIndexOf(Buffer.Cursor) == 0 && TryRecall(HistoryStep.Previous, Buffer.SnapshotText(), out var previous))
        {
            Recall(previous);
            return ComposerAction.Edited;
        }

        _ = Buffer.MoveUp();
        return ComposerAction.Edited;
    }

    private ComposerAction HandleHistoryDown()
    {
        // CF-B-005: the final step restores the saved draft exactly once
        // (readline), then the walk ends and Down is plain caret movement again.
        if (Buffer.LineIndexOf(Buffer.Cursor) == Buffer.LineCount - 1 && TryRecall(HistoryStep.Next, Buffer.SnapshotText(), out var next))
        {
            Recall(next);
            return ComposerAction.Edited;
        }

        _ = Buffer.MoveDown();
        return ComposerAction.Edited;
    }

    /// <summary>
    ///     Enter split, executed from the shared decision (#359, #33/T2):
    ///     <see cref="EnterPolicy"/> maps the modifiers to an
    ///     <see cref="EnterDecision" /> (Ctrl+Enter → ignore, Shift/Alt+Enter →
    ///     newline, plain Enter → submit) and the composer only applies the
    ///     buffer effect — so key behaviour cannot diverge per renderer. The
    ///     policy lives in <c>Harbor.Ui.Framework.Rendering.Input</c> rather than
    ///     State because this engine cannot reference State: asking the store for
    ///     the decision was the last thing holding that edge open (#435). The
    ///     store-side <c>EnterKeyPolicy</c> delegates to the same
    ///     <see cref="EnterPolicy" />, so there is still exactly one decision and
    ///     this is a pure executor of it.
    /// </summary>
    private ComposerAction HandleEnter(KeyModifiers mods)
    {
        return EnterPolicy.Resolve(
            (mods & KeyModifiers.Ctrl) != 0,
            (mods & KeyModifiers.Shift) != 0,
            (mods & KeyModifiers.Alt) != 0,
            (mods & KeyModifiers.Meta) != 0) switch
        {
            EnterDecision.InsertNewline => InsertNewline(),
            EnterDecision.Submit => SubmitDraft(),
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
        // ChatScreenProjector (issue #1202) read-only — a text change
        // pins the caret to the end of the text, and the composer follows via
        // MoveTo. End-of-text keeps composer and store caret coherent; the
        // renderer itself is untouched.
        _ = Buffer.MoveTo(Buffer.Length);
    }

    /// <summary>
    /// History-recall entry point (CF-B-005, #33/T2): maps a walk
    /// <see cref="HistoryStep" /> onto the <see cref="PromptHistory"/> walk.
    /// Previous captures <paramref name="draft"/> on the first step; the final
    /// Next restores it exactly once. Returns false at the walk boundaries
    /// (caller falls back to caret movement).
    /// <para>
    /// This was <c>TryRecallViaStore</c> and took an <c>InputMsg</c>; it now takes
    /// the direction itself. The old name had already become a lie — the store is
    /// not in this call path — and the rename makes the boundary visible: what the
    /// engine owns is the walk, and the store runs its own walk over the same
    /// <see cref="PromptHistory"/> semantics on its own messages.
    /// </para>
    /// </summary>
    private bool TryRecall(HistoryStep step, string draft, out string entry)
    {
        switch (step)
        {
            case HistoryStep.Previous:
                return History.TryRecallPrevious(draft, out entry);
            case HistoryStep.Next:
                return History.TryRecallNext(out entry);
            default:
                entry = string.Empty;
                return false;
        }
    }

    /// <summary>
    ///     Builds the composer gesture→action-id context (#3621). Gestures are
    ///     registered as data; the ids are resolved by <see cref="ComposerActions"/>.
    /// </summary>
    /// <remarks>
    ///     <c>KeyGesture.Matches</c> is EXACT-modifier by contract, so the
    ///     old mask predicates are spelled as the modifier sets they admitted:
    ///     Ctrl+Arrow and Meta+Arrow are both word jumps, and an Alt/Meta letter is
    ///     bound twice because legacy terminals encode M-x as an ESC prefix (Meta)
    ///     while kitty reports the Alt bit. A chord carrying an EXTRA command
    ///     modifier (Ctrl+Alt+C) is a different gesture and stays unbound; the old
    ///     tables let it ride into Ctrl+C.
    /// </remarks>
    private static Engine.KeyContext BuildKeyContext()
    {
        var keys = new Engine.KeyContext("composer");

        // ── Char chords ─────────────────────────────────────────────────────
        BindChar(keys, 'c', Engine.KeyModifiers.Ctrl, "char.ctrl-c");
        BindChar(keys, 'u', Engine.KeyModifiers.Ctrl, "char.ctrl-u");
        BindChar(keys, 'w', Engine.KeyModifiers.Ctrl, "char.ctrl-w");
        BindChar(keys, 'a', Engine.KeyModifiers.Ctrl, "char.ctrl-a");
        BindChar(keys, 'e', Engine.KeyModifiers.Ctrl, "char.ctrl-e");
        BindChar(keys, 'z', Engine.KeyModifiers.Ctrl, "char.ctrl-z");
        // Kitty CSI-u reports the shifted codepoint; legacy terminals cannot express C-S-z distinctly.
        BindChar(keys, 'Z', Engine.KeyModifiers.Ctrl | Engine.KeyModifiers.Shift, "char.ctrl-shift-z");
        BindChar(keys, 'k', Engine.KeyModifiers.Ctrl, "char.ctrl-k");
        BindChar(keys, 'y', Engine.KeyModifiers.Ctrl, "char.ctrl-y");

        // Alt/Meta word + markdown chords.
        BindCharAltOrMeta(keys, 'b', "char.meta-b");   // M-b word left
        BindCharAltOrMeta(keys, 'f', "char.meta-f");   // M-f word right
        BindCharAltOrMeta(keys, 'd', "char.meta-d");   // M-d kill word
        BindCharAltOrMeta(keys, 's', "char.meta-s");   // M-s bold
        BindCharAltOrMeta(keys, 'i', "char.meta-i");   // M-i italic
        BindCharAltOrMeta(keys, 'c', "char.meta-c");   // M-c inline code
        BindCharAltOrMeta(keys, 'h', "char.meta-h");   // M-h heading
        BindCharAltOrMeta(keys, 'l', "char.meta-l");   // M-l list item

        // ── Edit chords ─────────────────────────────────────────────────────
        BindWordJump(keys, Engine.KeyCode.Left, "edit.word-left");
        BindWordJump(keys, Engine.KeyCode.Right, "edit.word-right");

        // Backspace is text-editing, not a chord: Shift is a case-shaper and stays admitted.
        BindKey(keys, Engine.KeyCode.Backspace, Engine.KeyModifiers.None, "edit.backspace");
        BindKey(keys, Engine.KeyCode.Backspace, Engine.KeyModifiers.Shift, "edit.backspace");

        // Delete/Home/End are unmodified commands.
        BindKey(keys, Engine.KeyCode.Delete, Engine.KeyModifiers.None, "edit.delete");
        BindKey(keys, Engine.KeyCode.Home, Engine.KeyModifiers.None, "edit.home");
        BindKey(keys, Engine.KeyCode.End, Engine.KeyModifiers.None, "edit.end");

        // Plain arrows move by rune; Shift and Alt are gestures AT THIS SITE.
        BindPlainArrow(keys, Engine.KeyCode.Left, "edit.left");
        BindPlainArrow(keys, Engine.KeyCode.Right, "edit.right");

        // ── History ─────────────────────────────────────────────────────────
        // Unmodified only, deliberately NOT AcceptsTypedChar: Shift+Up has no
        // meaning in a history, and admitting it would make Shift+Up walk the draft.
        BindKey(keys, Engine.KeyCode.Up, Engine.KeyModifiers.None, "history.up");
        BindKey(keys, Engine.KeyCode.Down, Engine.KeyModifiers.None, "history.down");

        return keys;
    }

    private static void BindChar(Engine.KeyContext keys, char c, Engine.KeyModifiers mods, string actionId) =>
        keys.Bind(Engine.KeyGesture.ForChar(c, mods), actionId);

    private static void BindKey(Engine.KeyContext keys, Engine.KeyCode key, Engine.KeyModifiers mods, string actionId) =>
        keys.Bind(Engine.KeyGesture.ForKey(key, mods), actionId);

    private static void BindCharAltOrMeta(Engine.KeyContext keys, char c, string actionId)
    {
        BindChar(keys, c, Engine.KeyModifiers.Alt, actionId);
        BindChar(keys, c, Engine.KeyModifiers.Meta, actionId);
    }

    private static void BindWordJump(Engine.KeyContext keys, Engine.KeyCode key, string actionId)
    {
        BindKey(keys, key, Engine.KeyModifiers.Ctrl, actionId);
        BindKey(keys, key, Engine.KeyModifiers.Meta, actionId);
        BindKey(keys, key, Engine.KeyModifiers.Ctrl | Engine.KeyModifiers.Meta, actionId);
    }

    private static void BindPlainArrow(Engine.KeyContext keys, Engine.KeyCode key, string actionId)
    {
        BindKey(keys, key, Engine.KeyModifiers.None, actionId);
        BindKey(keys, key, Engine.KeyModifiers.Shift, actionId);
        BindKey(keys, key, Engine.KeyModifiers.Alt, actionId);
        BindKey(keys, key, Engine.KeyModifiers.Shift | Engine.KeyModifiers.Alt, actionId);
    }

    // ── Dictation session (#2541) ───────────────────────────────────────────

    /// <summary>
    /// Ctrl+D entry point: starts a session while idle, stops the running one
    /// otherwise. Returns true when the session changed state, false when the
    /// start was refused (see <see cref="DictationRejection"/>). A start does not
    /// await the backend; a stop awaits it so a late final cannot be lost.
    /// </summary>
    /// <param name="agentStreaming">True while the agent streams; dictation is refused then.</param>
    /// <param name="callerToken">Outer cancellation, e.g. host shutdown.</param>
    public async Task<bool> ToggleDictationAsync(bool agentStreaming, CancellationToken callerToken = default)
    {
        DictationRejection = null;

        if (_dictation.IsActive)
        {
            await RetireSessionAsync().ConfigureAwait(false);
            // A stop without a final is a half utterance: restore the draft.
            if (_dictation.IsActive)
            {
                _dictation.Cancel();
                SyncDictationText();
            }

            return true;
        }

        // A backend may have delivered its final and gone idle while its task is
        // still unwinding. Retire it before a new session overwrites the fields.
        await RetireSessionAsync().ConfigureAwait(false);

        if (!_dictation.TryStart(Buffer.SnapshotText(), agentStreaming, out var rejection))
        {
            DictationRejection = rejection;
            return false;
        }

        _dictationCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _dictationTask = RunSessionAsync(_dictationCts.Token);
        SyncDictationText();
        return true;
    }

    /// <summary>
    /// Cancel: drop the in-flight partial and restore the exact pre-dictation
    /// text. The buffer is cancelled BEFORE the backend is signalled, so a final
    /// delivered during shutdown is ignored rather than committed.
    /// </summary>
    public async Task CancelDictationAsync()
    {
        if (!_dictation.IsActive && _dictation.Status != DictationStatus.Error)
        {
            return;
        }

        _dictation.Cancel();
        SyncDictationText();
        await RetireSessionAsync().ConfigureAwait(false);
    }

    private async Task RetireSessionAsync()
    {
        var cts = _dictationCts;
        if (cts is null)
        {
            return;
        }

        var task = _dictationTask;
        _dictationCts = null;
        _dictationTask = null;
        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A backend ending on its cancellation token is normal.
            }
        }

        cts.Dispose();
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        try
        {
            await _speechToText.StartAsync(OnDictationPartial, OnDictationFinal, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop / cancel asked for this; the buffer decides commit vs restore.
        }
        catch (Exception ex)
        {
            _dictation.Fail(ex.Message);
            SyncDictationText();
        }
    }

    private void OnDictationPartial(string transcript)
    {
        _dictation.AcceptPartial(transcript);
        SyncDictationText();
    }

    private void OnDictationFinal(string transcript)
    {
        _dictation.AcceptFinal(transcript);
        SyncDictationText();
    }

    /// <summary>
    /// The buffer is the single source of truth; the composer buffer is a mirror
    /// so submit, history and painting keep reading the same text.
    /// </summary>
    private void SyncDictationText()
    {
        Buffer.Clear();
        _ = Buffer.InsertText(_dictation.Text);
        _ = Buffer.MoveTo(Buffer.Length);
    }
}
