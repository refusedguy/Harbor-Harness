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

        var buffer = composer.Buffer;
        switch (hit.ActionId)
        {
            case "move-left": _ = buffer.MoveLeft(); return ComposerAction.Edited;
            case "move-right": _ = buffer.MoveRight(); return ComposerAction.Edited;
            case "move-word-right": _ = buffer.MoveWordRight(); return ComposerAction.Edited;
            case "move-word-left": _ = buffer.MoveWordLeft(); return ComposerAction.Edited;
            case "line-start": _ = buffer.MoveToLineStart(); return ComposerAction.Edited;
            case "line-end": _ = buffer.MoveToLineEnd(); return ComposerAction.Edited;
            case "delete-forward": _ = buffer.DeleteForward(); return ComposerAction.Edited;
            case "history-next": return composer.HandleKey(KeyEvent.Simple(KeyCode.Down));
            case "history-prev": return composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
            case "insert-end": _ = buffer.MoveToLineEnd(); NormalMode = false; return ComposerAction.Edited;
            case "insert-start": _ = buffer.MoveToLineStart(); NormalMode = false; return ComposerAction.Edited;
            case "insert-append": _ = buffer.MoveRight(); NormalMode = false; return ComposerAction.Edited;
            case "insert": NormalMode = false; return ComposerAction.Edited;
            default: return ComposerAction.Ignored;
        }
    }
}
