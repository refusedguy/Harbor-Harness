using System.Text;
using Harbor.Tui.ConsoleEx.Input;

namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
/// Vim editing layer over <see cref="ComposerController" /> (optional power-
/// user mode): Esc leaves insert for NORMAL, i/a/A/I return to insert; normal
/// chords map onto readline primitives already owned by the buffer (h/l/w/b
/// moves, 0/$ line jumps, x delete, j/k history recall via the composer's own
/// Up/Down path). When disabled the layer is pure pass-through.
/// </summary>
public sealed class VimComposerMode
{
    /// <summary>True once the user turned vim mode on (palette/leader toggle).</summary>
    public bool Enabled { get; set; }

    /// <summary>True while in NORMAL mode (only meaningful when <see cref="Enabled" />).</summary>
    public bool NormalMode { get; private set; }

    /// <summary>Routes one key: normal-mode chords first, everything else reaches the composer.</summary>
    public ComposerAction HandleKey(in KeyEvent key, ComposerController composer)
    {
        ArgumentNullException.ThrowIfNull(composer);

        if (!Enabled)
        {
            return composer.HandleKey(key);
        }

        if (NormalMode && key.Key == KeyCode.Char && key.Modifiers == KeyModifiers.None)
        {
            var handled = HandleNormal(key.Character, composer);
            if (handled != ComposerAction.Ignored)
            {
                return handled;
            }

            if (key.Character.Value == 'i')
            {
                NormalMode = false;
                return ComposerAction.Edited;
            }
        }

        var action = composer.HandleKey(key);
        if (key.Key == KeyCode.Escape && Enabled)
        {
            NormalMode = true;
        }

        return action;
    }

    private ComposerAction HandleNormal(Rune c, ComposerController composer)
    {
        var buffer = composer.Buffer;
        switch (c.Value)
        {
            case 'h': _ = buffer.MoveLeft(); return ComposerAction.Edited;
            case 'l': _ = buffer.MoveRight(); return ComposerAction.Edited;
            case 'w': _ = buffer.MoveWordRight(); return ComposerAction.Edited;
            case 'b': _ = buffer.MoveWordLeft(); return ComposerAction.Edited;
            case '0': _ = buffer.MoveToLineStart(); return ComposerAction.Edited;
            case '$': _ = buffer.MoveToLineEnd(); return ComposerAction.Edited;
            case 'x': _ = buffer.DeleteForward(); return ComposerAction.Edited;

            // j/k reuse the composer's own Up/Down path: history recall from the
            // first/last line, caret movement inside multi-line drafts.
            case 'j': return composer.HandleKey(KeyEvent.Simple(KeyCode.Down));
            case 'k': return composer.HandleKey(KeyEvent.Simple(KeyCode.Up));

            case 'A': _ = buffer.MoveToLineEnd(); NormalMode = false; return ComposerAction.Edited;
            case 'I': _ = buffer.MoveToLineStart(); NormalMode = false; return ComposerAction.Edited;
            case 'a':
                _ = buffer.MoveRight(); // clamp no-op at end of line
                NormalMode = false;
                return ComposerAction.Edited;

            default:
                return ComposerAction.Ignored; // 'i' handled by caller; rest falls through
        }
    }
}
