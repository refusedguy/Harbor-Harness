using System.Collections.Frozen;
using System.Text;
using Harbor.Ui.Framework.Rendering.Input;
// File-level alias: the engine owns KeyContext/KeyGesture (Harbor.Tui.CellForge.Input)
// while KeyEvent/KeyCode stay the shared Rendering types from GlobalUsings.cs.
using EngineInput = Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Vim editing layer over <see cref="ComposerController" /> (optional power-
/// user mode): Esc leaves insert for NORMAL, i/a/A/I return to insert; normal
/// chords map onto readline primitives already owned by the buffer (h/l/w/b
/// moves, 0/$ line jumps, x delete, j/k history recall via the composer's own
/// Up/Down path). When disabled the layer is pure pass-through.
/// </summary>
public sealed class VimComposerMode
{
    // #3437: one owner for the opaque chord id -> host behavior map. A table
    // (not a string switch) keeps the repository's string-switch ratchet
    // intact: the engine names actions, this host gives them meaning.
    private static readonly FrozenDictionary<string, Func<VimComposerMode, ComposerController, ComposerAction>> ChordHandlers =
        FrozenDictionary.ToFrozenDictionary(
            new Dictionary<string, Func<VimComposerMode, ComposerController, ComposerAction>>(StringComparer.Ordinal)
            {
                ["move-left"] = static (mode, c) => { _ = c.Buffer.MoveLeft(); return ComposerAction.Edited; },
                ["move-right"] = static (mode, c) => { _ = c.Buffer.MoveRight(); return ComposerAction.Edited; },
                ["move-word-right"] = static (mode, c) => { _ = c.Buffer.MoveWordRight(); return ComposerAction.Edited; },
                ["move-word-left"] = static (mode, c) => { _ = c.Buffer.MoveWordLeft(); return ComposerAction.Edited; },
                ["line-start"] = static (mode, c) => { _ = c.Buffer.MoveToLineStart(); return ComposerAction.Edited; },
                ["line-end"] = static (mode, c) => { _ = c.Buffer.MoveToLineEnd(); return ComposerAction.Edited; },
                ["delete-forward"] = static (mode, c) => { _ = c.Buffer.DeleteForward(); return ComposerAction.Edited; },

                // j/k reuse the composer's own Up/Down path: history recall from the
                // first/last line, caret movement inside multi-line drafts.
                ["history-next"] = static (mode, c) => c.HandleKey(KeyEvent.Simple(KeyCode.Down)),
                ["history-prev"] = static (mode, c) => c.HandleKey(KeyEvent.Simple(KeyCode.Up)),

                ["insert-end"] = static (mode, c) => { _ = c.Buffer.MoveToLineEnd(); mode.NormalMode = false; return ComposerAction.Edited; },
                ["insert-start"] = static (mode, c) => { _ = c.Buffer.MoveToLineStart(); mode.NormalMode = false; return ComposerAction.Edited; },
                ["insert-append"] = static (mode, c) => { _ = c.Buffer.MoveRight(); mode.NormalMode = false; return ComposerAction.Edited; },
                ["insert"] = static (mode, c) => { mode.NormalMode = false; return ComposerAction.Edited; },
            },
            StringComparer.Ordinal);

    // #3437: normal-mode chords are a declarative KeyContext ("composer.vim"),
    // a child of the composer context. Opaque action ids keep the engine free
    // of host behavior; HandleNormal gives them meaning. The type is BCL-only.
    private readonly EngineInput.KeyContext vimContext;

    /// <summary>True once the user turned vim mode on (palette/leader toggle).</summary>
    public bool Enabled { get; set; }

    /// <summary>True while in NORMAL mode (only meaningful when <see cref="Enabled" />).</summary>
    public bool NormalMode { get; private set; }

    /// <summary>Creates a vim layer in the default (disabled, insert) state.</summary>
    public VimComposerMode()
    {
        vimContext = new EngineInput.KeyContext("composer.vim");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('h'), "move-left");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('l'), "move-right");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('w'), "move-word-right");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('b'), "move-word-left");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('0'), "line-start");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('$'), "line-end");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('x'), "delete-forward");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('j'), "history-next");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('k'), "history-prev");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('A'), "insert-end");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('I'), "insert-start");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('a'), "insert-append");
        vimContext.Bind(EngineInput.KeyGesture.ForChar('i'), "insert");
    }

    /// <summary>Mode toggle: turns the layer off and resets to insert semantics.</summary>
    public void Reset()
    {
        NormalMode = false;
    }

    /// <summary>Routes one key: normal-mode chords first, everything else reaches the composer.</summary>
    public ComposerAction HandleKey(in KeyEvent key, ComposerController composer)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(composer);

        if (!Enabled)
        {
            return composer.HandleKey(key);
        }

        if (NormalMode && key.Key == KeyCode.Char && key.Modifiers.IsUnmodified())
        {
            var handled = HandleNormal(key, composer);
            if (handled != ComposerAction.Ignored)
            {
                return handled;
            }
        }

        var action = composer.HandleKey(key);
        if (key.Key == KeyCode.Escape && Enabled)
        {
            NormalMode = true;
        }

        return action;
    }

    private ComposerAction HandleNormal(KeyEvent key, ComposerController composer)
    {
        // The composer speaks the shared Rendering KeyEvent; the engine leaf
        // KeyContext matches its own same-named type. Convert at this boundary.
        // HandleKey only routes unmodified char keys here.
        var chord = EngineInput.KeyEvent.Char(key.Character);
        if (!vimContext.TryMatch(chord, out var hit))
        {
            return ComposerAction.Ignored;
        }

        var actionId = hit.ActionId;
        if (actionId is null || !ChordHandlers.TryGetValue(actionId, out var handler))
        {
            return ComposerAction.Ignored;
        }

        return handler(this, composer);
    }
}
