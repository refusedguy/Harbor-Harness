// TR9 (#1228, epic #1195): scripted input queue over the engine vocabulary.
//
// Termina-side VirtualInputSource.SendKey ported onto cells: tests enqueue
// decoded input (keys, text, pastes, resizes) and the scripted host drains
// them through the same HandleKey path a live terminal would take. The
// queue holds ENGINE events (Harbor.Tui.CellForge.Input) — the BCL-only
// leaf vocabulary — so this file imports no Harbor namespace (see
// CellForgeEngineAtomicityRules) and judges no runes: SendText splits text
// into per-rune presses locally, never by reading a key's character back,
// so this producer is deliberately NOT a KeyGateFamilyRules site (it
// receives keys but never routes one).
//
// Threading: single-threaded scripted use only. Unlike TerminalInputSource
// (stdin reader thread plus channel), this queue has no background work and
// no locking — enqueue from the test, drain into the screen, assert.

using System.Text;

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Scripted input queue for tests and gallery goldens (TR9): enqueue input
/// the way Termina's <c>VirtualInputSource.SendKey</c> does, then drain the
/// queued <see cref="InputEvent"/> values into the screen under test.
/// </summary>
public sealed class VirtualInputSource
{
    private readonly Queue<InputEvent> _events = new();

    /// <summary>Queued event count.</summary>
    public int Count => _events.Count;

    /// <summary>True when no event is queued.</summary>
    public bool IsEmpty => _events.Count == 0;

    /// <summary>Enqueues a fully-formed key press.</summary>
    /// <param name="key">Key event to replay on drain.</param>
    public void SendKey(KeyEvent key) => _events.Enqueue(InputEvent.FromKey(key));

    /// <summary>Enqueues a named (non-text) key press.</summary>
    /// <param name="code">Logical key (Enter, Tab, Down, ...).</param>
    /// <param name="modifiers">Modifiers held during the press.</param>
    public void SendSimple(KeyCode code, KeyModifiers modifiers = KeyModifiers.None) =>
        SendKey(KeyEvent.Simple(code, modifiers));

    /// <summary>Enqueues one printable press for <paramref name="rune"/>.</summary>
    /// <param name="rune">Rune to type.</param>
    /// <param name="modifiers">Modifiers held during the press (Shift types a capital).</param>
    public void SendChar(Rune rune, KeyModifiers modifiers = KeyModifiers.None) =>
        SendKey(KeyEvent.Char(rune, modifiers));

    /// <summary>
    /// Enqueues <paramref name="text"/> as one press per rune
    /// (<c>\r\n</c>, <c>\r</c> and <c>\n</c> each become a single Enter).
    /// </summary>
    /// <param name="text">Text to type (null coerces to empty).</param>
    public void SendText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Normalize first: a CRLF pair is one Enter, never two submits.
        string normalized = text!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                SendKey(KeyEvent.Simple(KeyCode.Enter));
            }
            else
            {
                SendKey(KeyEvent.Char(rune));
            }
        }
    }

    /// <summary>Enqueues a viewport resize.</summary>
    /// <param name="width">New width in columns (must be positive).</param>
    /// <param name="height">New height in rows (must be positive).</param>
    public void SendResize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _events.Enqueue(InputEvent.FromResize(new ResizeSignal(width, height)));
    }

    /// <summary>Enqueues one atomic paste payload (null coerces to empty).</summary>
    /// <param name="text">Pasted text, carried verbatim.</param>
    public void SendPaste(string? text) =>
        _events.Enqueue(InputEvent.FromPaste(new PasteEvent(text ?? string.Empty, false)));

    /// <summary>Takes the next queued event.</summary>
    /// <param name="evt">Next event, when one is queued.</param>
    /// <returns>True when an event was queued.</returns>
    public bool TryTake(out InputEvent evt)
    {
        if (_events.TryDequeue(out InputEvent next))
        {
            evt = next;
            return true;
        }

        evt = InputEvent.Unknown();
        return false;
    }

    /// <summary>Drains every queued event into <paramref name="handle"/> in order.</summary>
    /// <param name="handle">Receiver for each queued event.</param>
    public void DrainTo(Action<InputEvent> handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        while (_events.TryDequeue(out InputEvent evt))
        {
            handle(evt);
        }
    }

    /// <summary>Drops every queued event.</summary>
    public void Clear() => _events.Clear();
}
