// #1620 adapted slice: same body as the Chat leaf
// (Harbor.Tui.CellForge/Chat/Widgets/FloatingTerminalSession.cs, now deleted)
// except the key vocabulary. The engine is BCL-only and compiles without the
// Chat project's global `KeyEvent`/`KeyCode` aliases (those pin the Rendering
// vocabulary), so those two names are now spelled with an explicit using of
// the engine's own input namespace. SendKey/FeedKey therefore take the engine
// KeyEvent; the headless test drains VirtualInputSource keys into them without
// the old engine→rendering conversion. Namespace is unchanged.
using System.Text;
using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     One attachable terminal-pane session behind the floating overlay
///     (issue #1491). The object identity IS the session: detaching never
///     recreates it, so scrollback and the "live process" flag survive the
///     detach/attach cycle. This is deliberately the headless, state-only half
///     of a terminal pane — it owns no OS handle and no terminal emulation, so
///     a real PTY byte transport can be plugged in later without touching the
///     overlay/focus axes or adding a seam. Keys fed while attached are echoed
///     into <see cref="Lines"/>, which is what the headless attach/detach test
///     observes (VirtualInputSource-style, no real PTY).
/// </summary>
public sealed class FloatingTerminalSession
{
    private const string Prompt = "$ ";
    private readonly List<string> _lines = [Prompt];
    private readonly StringBuilder _current = new();

    /// <summary>Creates a session. <paramref name="title"/> is display-only.</summary>
    public FloatingTerminalSession(string title = "terminal")
    {
        Title = title;
    }

    /// <summary>Display title for the pane header.</summary>
    public string Title { get; }

    /// <summary>False once the pane has been closed; a closed session is never re-attached.</summary>
    public bool IsAlive { get; private set; } = true;

    /// <summary>Rendered scrollback (oldest first). The last line is the live input line.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Total lines ever rendered — a cheap identity signal for attach/detach tests.</summary>
    public int LineCount => _lines.Count;

    /// <summary>Text of the live input line (without the prompt).</summary>
    public string CurrentInput => _current.ToString();

    /// <summary>
    ///     Feeds one decoded key into the session, echoing typed characters.
    ///     Enter commits the input line and starts a fresh prompt; Backspace
    ///     edits the live line. Non-text keys are ignored by this headless
    ///     model — a real PTY would translate them to byte sequences instead.
    /// </summary>
    public void SendKey(in KeyEvent key)
    {
        if (!IsAlive)
        {
            return;
        }

        if (key.Key == KeyCode.Enter)
        {
            _lines.Add(_current.ToString());
            _current.Clear();
            _lines.Add(Prompt);
            return;
        }

        if (key.Key == KeyCode.Backspace)
        {
            if (_current.Length > 0)
            {
                _current.Length--;
            }

            return;
        }

        if (key.Key == KeyCode.Char)
        {
            _current.Append((char)key.Character.Value);
        }
    }

    /// <summary>
    ///     Appends raw output (a PTY chunk in a live host, scripted output in a
    ///     test) to the scrollback. Newlines start new lines so the painted pane
    ///     reflects the session's history rather than a single blob.
    /// </summary>
    public void AppendOutput(string? text)
    {
        if (!IsAlive || string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (string part in text!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            _lines.Add(part);
        }
    }

    /// <summary>Marks the session closed. The owning controller drops it on close.</summary>
    public void Close() => IsAlive = false;
}

/// <summary>
///     Attach/detach state machine for the floating terminal pane (issue #1491).
///     One controller per screen, owned by <c>ChatScreenLayout</c>; it holds the
///     current session across detaches and reports whether input focus belongs
///     to the pane. Focus is realised by the host's hand-written key cascade:
///     while <see cref="IsAttached"/> is true, keys go to <see cref="FeedKey"/>
///     and the composer starves; detaching returns the next key to the composer.
///     No focus-stack or plugin type is introduced — the existing input/focus
///     axes are reused as-is.
/// </summary>
public sealed class FloatingTerminalController
{
    private FloatingTerminalSession? _session;

    /// <summary>The current session, or null before the first attach.</summary>
    public FloatingTerminalSession? Session => _session;

    /// <summary>True while the pane owns input focus.</summary>
    public bool IsAttached { get; private set; }

    /// <summary>
    ///     Attaches the current session, creating it once on first use. A
    ///     detach/attach cycle returns the SAME live session (same object, same
    ///     scrollback). False when already attached.
    /// </summary>
    public bool Attach()
    {
        if (IsAttached)
        {
            return false;
        }

        _session ??= new FloatingTerminalSession();
        if (!_session.IsAlive)
        {
            _session = new FloatingTerminalSession();
        }

        IsAttached = true;
        return true;
    }

    /// <summary>
    ///     Detaches the pane without touching the session — the process/history
    ///     are still there for the next attach and focus returns to the composer.
    ///     False when already detached.
    /// </summary>
    public bool Detach()
    {
        if (!IsAttached)
        {
            return false;
        }

        IsAttached = false;
        return true;
    }

    /// <summary>Toggles attach state; the session is created lazily and retained.</summary>
    public bool Toggle() => IsAttached ? Detach() : Attach();

    /// <summary>
    ///     Feeds a key to the attached session. False (no consumption) when
    ///     detached, so the host cascade can hand the key to the composer.
    /// </summary>
    public bool FeedKey(in KeyEvent key)
    {
        if (!IsAttached || _session is null || !_session.IsAlive)
        {
            return false;
        }

        _session.SendKey(key);
        return true;
    }

    /// <summary>Closes the pane for good: kills focus and drops the session.</summary>
    public void Close()
    {
        IsAttached = false;
        _session?.Close();
        _session = null;
    }
}
