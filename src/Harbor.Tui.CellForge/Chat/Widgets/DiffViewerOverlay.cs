using System.Text;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Diff body layout. <see cref="Unified"/> renders one column with
/// <c>-</c>/<c>+</c> prefixes (codex <c>/diff</c> style);
/// <see cref="Split"/> renders removed/context on the left and
/// added/context on the right with a <c>│</c> gutter (opencode diff-viewer style).
/// </summary>
public enum DiffViewerMode
{
    Unified,
    Split,
}

/// <summary>Kind of one parsed unified-diff content line.</summary>
public enum DiffViewerLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>One parsed unified-diff content line (prefix stripped).</summary>
public sealed record DiffViewerLine(DiffViewerLineKind Kind, string Text);

/// <summary>
/// One parsed hunk: an optional <c>@@</c> header plus its content lines.
/// </summary>
public sealed class DiffViewerHunk
{
    private readonly List<DiffViewerLine> _lines = new();

    public DiffViewerHunk(string header)
    {
        Header = header ?? string.Empty;
    }

    /// <summary>Raw <c>@@ -a,b +c,d @@</c> header (empty for synthesized hunks).</summary>
    public string Header { get; }

    /// <summary>Content lines in diff order.</summary>
    public IReadOnlyList<DiffViewerLine> Lines => _lines;

    internal void Add(DiffViewerLineKind kind, string text) => _lines.Add(new DiffViewerLine(kind, text));
}

/// <summary>
/// One paint-ready body row. Unified rows carry <see cref="FullText"/> /
/// <see cref="FullStyle"/>; split content rows carry <see cref="LeftText"/> /
/// <see cref="RightText"/> with their styles and paint in three spans.
/// Hunk headers always use the full-width form in both modes.
/// </summary>
internal sealed record DiffBodyRow(
    int HunkIndex,
    bool IsHeader,
    string? FullText,
    CellStyle FullStyle,
    string? LeftText = null,
    CellStyle? LeftStyle = null,
    string? RightText = null,
    CellStyle? RightStyle = null);

// ── The diff viewer's command table (#1544) ─────────────────────────────────
//
// The three key dispatches this file used to carry — the legacy named-key
// switch, its rune switch, and the kitty pair — were ONE table written three
// times, and nothing held the copies to each other. It is one table now: a
// gesture (a named key or a rune) resolves to a command (an object that
// carries the action), and both HandleKey overloads normalise their key into
// the viewer's own vocabulary before the lookup.
//
// The shape is the repo's Command convention (docs/PATTERNS.md §4) at widget
// scale: the action is an object in a table, not a switch arm, so a new key is
// ONE row and cannot be added to one copy of the table and forgotten in the
// other two.

/// <summary>
/// The gesture a diff-viewer command fires on: a named key, or an exact rune.
/// </summary>
/// <param name="Key">The key code that fires the command.</param>
/// <param name="Rune">The exact rune that fires it (null = a named key).</param>
internal readonly record struct DiffViewerGesture(KeyCode Key, char? Rune = null);

/// <summary>
/// One action of the diff viewer, as an object (GoF Command — the repo's §4
/// convention: a command is an object, not a <c>switch</c> arm).
/// </summary>
internal interface IDiffViewerCommand
{
    /// <summary>Runs the command against the viewer.</summary>
    void Execute(DiffViewerOverlay viewer);
}

/// <summary>Closes the viewer (Esc, q).</summary>
internal sealed record DiffViewerHideCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.Hide();
}

/// <summary>Moves the selection to the next hunk (Down, n, j).</summary>
internal sealed record DiffViewerNextHunkCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.NextHunk();
}

/// <summary>Moves the selection to the previous hunk (Up, p, k).</summary>
internal sealed record DiffViewerPreviousHunkCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.PreviousHunk();
}

/// <summary>Jumps to the first hunk (Home, g).</summary>
internal sealed record DiffViewerFirstHunkCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.FirstHunk();
}

/// <summary>Jumps to the last hunk (End, G).</summary>
internal sealed record DiffViewerLastHunkCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.LastHunk();
}

/// <summary>Toggles split/unified (s, t).</summary>
internal sealed record DiffViewerToggleModeCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.ToggleMode();
}

/// <summary>Toggles line wrapping (w).</summary>
internal sealed record DiffViewerToggleWrapCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.ToggleWrap();
}

/// <summary>Scrolls the body up by one page (PgUp).</summary>
internal sealed record DiffViewerScrollUpCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.ScrollPage(-1);
}

/// <summary>Scrolls the body down by one page (PgDn).</summary>
internal sealed record DiffViewerScrollDownCommand : IDiffViewerCommand
{
    /// <inheritdoc/>
    public void Execute(DiffViewerOverlay viewer) => viewer.ScrollPage(1);
}

/// <summary>
/// The diff viewer's command table: gesture → command (#1544). The ONE place
/// that says what a key does.
/// </summary>
/// <remarks>
/// The rows SHARE their command objects: <c>n</c> and <c>j</c> are the same
/// move behind two gestures, exactly as <c>Down</c> is. That sharing is what
/// the old hand-written switches could not express — they spelled the same
/// move out three times, once per copy of the table — and it is what the
/// table's own test asserts.
/// <para>
/// A gesture with no row is not claimed by the viewer and falls through to
/// the host.
/// </para>
/// </remarks>
internal static class DiffViewerCommandTable
{
    /// <summary>Closes the viewer.</summary>
    private static readonly IDiffViewerCommand Hide = new DiffViewerHideCommand();

    /// <summary>Moves the selection to the next hunk.</summary>
    private static readonly IDiffViewerCommand NextHunk = new DiffViewerNextHunkCommand();

    /// <summary>Moves the selection to the previous hunk.</summary>
    private static readonly IDiffViewerCommand PreviousHunk = new DiffViewerPreviousHunkCommand();

    /// <summary>Jumps to the first hunk.</summary>
    private static readonly IDiffViewerCommand FirstHunk = new DiffViewerFirstHunkCommand();

    /// <summary>Jumps to the last hunk.</summary>
    private static readonly IDiffViewerCommand LastHunk = new DiffViewerLastHunkCommand();

    /// <summary>Toggles split/unified.</summary>
    private static readonly IDiffViewerCommand ToggleMode = new DiffViewerToggleModeCommand();

    /// <summary>Toggles line wrapping.</summary>
    private static readonly IDiffViewerCommand ToggleWrap = new DiffViewerToggleWrapCommand();

    /// <summary>Scrolls the body up by one page.</summary>
    private static readonly IDiffViewerCommand ScrollUp = new DiffViewerScrollUpCommand();

    /// <summary>Scrolls the body down by one page.</summary>
    private static readonly IDiffViewerCommand ScrollDown = new DiffViewerScrollDownCommand();

    /// <summary>
    /// The table: gesture → command. A named key and a rune are different
    /// gestures, so <c>Down</c> and <c>n</c> are two rows that happen to run
    /// the same command.
    /// </summary>
    internal static readonly IReadOnlyDictionary<DiffViewerGesture, IDiffViewerCommand> Commands =
        new Dictionary<DiffViewerGesture, IDiffViewerCommand>
        {
            // Named keys. Modifiers are ignored here, exactly as they were in
            // the switch: Ctrl+Up still moves the selection.
            [new DiffViewerGesture(KeyCode.Escape)] = Hide,
            [new DiffViewerGesture(KeyCode.Up)] = PreviousHunk,
            [new DiffViewerGesture(KeyCode.Down)] = NextHunk,
            [new DiffViewerGesture(KeyCode.PageUp)] = ScrollUp,
            [new DiffViewerGesture(KeyCode.PageDown)] = ScrollDown,
            [new DiffViewerGesture(KeyCode.Home)] = FirstHunk,
            [new DiffViewerGesture(KeyCode.End)] = LastHunk,

            // Runes. Reached only behind the command-modifier gate, so Shift
            // is admitted (a capital is a case-shaper) and Ctrl/Alt/Meta are
            // not (a chord belongs to the host).
            [new DiffViewerGesture(KeyCode.Char, 'q')] = Hide,
            [new DiffViewerGesture(KeyCode.Char, 'n')] = NextHunk,
            [new DiffViewerGesture(KeyCode.Char, 'j')] = NextHunk,
            [new DiffViewerGesture(KeyCode.Char, 'p')] = PreviousHunk,
            [new DiffViewerGesture(KeyCode.Char, 'k')] = PreviousHunk,
            [new DiffViewerGesture(KeyCode.Char, 's')] = ToggleMode,
            [new DiffViewerGesture(KeyCode.Char, 't')] = ToggleMode,
            [new DiffViewerGesture(KeyCode.Char, 'w')] = ToggleWrap,
            [new DiffViewerGesture(KeyCode.Char, 'g')] = FirstHunk,
            [new DiffViewerGesture(KeyCode.Char, 'G')] = LastHunk,
        };
}

/// <summary>
/// Fullscreen diff viewer overlay (PRIM12, #308 — opencode diff-viewer /
/// codex <c>/diff</c> competitor pattern, needed by UX7). Takes over the whole
/// viewport on the PRIM2a z-stack (see <see cref="DiffViewerOverlayLayer"/>):
/// hunk navigation (<c>n</c>/<c>p</c>, arrows), split/unified toggle
/// (<c>s</c>) and wrap toggle (<c>w</c>). <c>Esc</c>/<c>q</c> closes.
///
/// Parses unified diffs (<c>@@</c> hunks, <c>---</c>/<c>+++</c> preamble);
/// plain content without hunks becomes one synthesized hunk. Hidden paint is
/// a no-op so steady-state frames and goldens stay byte-identical until the
/// host shows it.
/// Not thread-safe: show/hide/paint on the render thread only.
/// </summary>
public sealed class DiffViewerOverlay
{
    public const int MinWidth = 20;
    public const int MinHeight = 6;
    private const int ChromeRows = 4;

    private readonly List<DiffViewerHunk> _hunks = new();
    private readonly List<string> _preamble = new();
    private int _selectedHunk;
    private int _scroll;
    private int _lastRows;
    private int _lastBodyH = 10;

    /// <summary>True while the viewer owns the screen.</summary>
    public bool Visible { get; private set; }

    /// <summary>File path shown in the header (display only).</summary>
    public string FilePath { get; private set; } = "(diff)";

    /// <summary>Parsed hunks in diff order.</summary>
    public IReadOnlyList<DiffViewerHunk> Hunks => _hunks;

    /// <summary><c>---</c>/<c>+++</c> file-header lines above the first hunk.</summary>
    public IReadOnlyList<string> Preamble => _preamble;

    /// <summary>Selected hunk index (hunk navigation target).</summary>
    public int SelectedHunk => _selectedHunk;

    /// <summary>Body layout (persists across <see cref="Show"/> calls).</summary>
    public DiffViewerMode Mode { get; private set; } = DiffViewerMode.Unified;

    /// <summary>Wrap long lines instead of truncating (persists across <see cref="Show"/> calls).</summary>
    public bool Wrap { get; private set; }

    /// <summary>First visible body row (page scroll offset).</summary>
    public int ScrollOffset => _scroll;

    /// <summary>
    /// Shows the viewer with a unified diff. Resets hunk selection and scroll;
    /// <see cref="Mode"/> and <see cref="Wrap"/> are user prefs and persist.
    /// </summary>
    public void Show(string? filePath, string diffText)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? "(diff)" : filePath;
        Parse(diffText ?? string.Empty);
        _selectedHunk = 0;
        _scroll = 0;
        Visible = true;
    }

    /// <summary>Closes the viewer (overlay-local dismissal).</summary>
    public void Hide()
    {
        Visible = false;
    }

    /// <summary>Moves selection to the next hunk (clamped).</summary>
    public void NextHunk()
    {
        if (_selectedHunk < _hunks.Count - 1)
        {
            _selectedHunk++;
        }
    }

    /// <summary>Moves selection to the previous hunk (clamped).</summary>
    public void PreviousHunk()
    {
        if (_selectedHunk > 0)
        {
            _selectedHunk--;
        }
    }

    /// <summary>Moves selection to the first hunk.</summary>
    public void FirstHunk() => _selectedHunk = 0;

    /// <summary>Moves selection to the last hunk.</summary>
    public void LastHunk() => _selectedHunk = Math.Max(0, _hunks.Count - 1);

    /// <summary>Toggles <see cref="DiffViewerMode.Unified"/> ⇄ <see cref="DiffViewerMode.Split"/>.</summary>
    public void ToggleMode() => Mode = Mode == DiffViewerMode.Unified ? DiffViewerMode.Split : DiffViewerMode.Unified;

    /// <summary>Toggles line wrapping.</summary>
    public void ToggleWrap() => Wrap = !Wrap;

    /// <summary>Scrolls the body by <paramref name="delta"/> rows (page keys).</summary>
    public void ScrollBy(int delta)
    {
        _scroll += delta;
        ClampScroll();
    }

    /// <summary>
    /// Scrolls the body by one page in <paramref name="direction"/> (-1 up,
    /// +1 down). The page height is the widget's own, so the command table
    /// reaches it through this and not through a private field (#1544).
    /// </summary>
    internal void ScrollPage(int direction) => ScrollBy(direction * Math.Max(1, _lastBodyH));

    /// <summary>
    /// Routes a legacy console key. <c>Esc</c>/<c>q</c> closes; <c>n</c>/<c>j</c>
    /// and <c>↓</c> go to the next hunk, <c>p</c>/<c>k</c> and <c>↑</c> to the
    /// previous one; <c>s</c>/<c>t</c> toggles split/unified, <c>w</c> toggles
    /// wrap; <c>g</c>/<c>Home</c> jumps first, <c>G</c>/<c>End</c> last;
    /// <c>PgUp</c>/<c>PgDn</c> scroll. Ctrl/Alt chords pass through untouched.
    /// </summary>
    /// <remarks>
    /// The key is normalised into the viewer's own vocabulary and resolved
    /// against the ONE command table, so this overload and the
    /// <see cref="KeyEvent" /> one cannot drift apart: there is a single table
    /// to disagree with (#1544).
    /// </remarks>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (!Visible)
        {
            return false;
        }

        // Named keys are resolved BEFORE the modifier gate, exactly as the
        // switch this replaced did: Ctrl+Up still moves the selection.
        if (LegacyKeyCodes.TryGetValue(key.Key, out KeyCode named))
        {
            return Run(new DiffViewerGesture(named));
        }

        // Ctrl/Alt chords pass through untouched — the host owns them.
        if (key.Modifiers.IsCommandModifier())
        {
            return false;
        }

        return Run(new DiffViewerGesture(KeyCode.Char, key.KeyChar));
    }

    /// <summary>
    /// Routes a decoded key (kitty-capable hosts). Mirrors the
    /// <see cref="ConsoleKeyInfo"/> contract; Ctrl/Alt/Meta character chords
    /// pass through untouched (the host keymap owns them), while Shift is
    /// admitted — it is a case-shaper, and <c>G</c> is a bound command here.
    /// </summary>
    /// <remarks>
    /// The same table the legacy overload resolves against, reached through
    /// the same normalisation (#1544).
    /// </remarks>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return false;
        }

        if (key.Key == KeyCode.Char)
        {
            // Ctrl/Alt/Meta chords pass through untouched — the host owns them.
            if (key.Modifiers.IsCommandModifier())
            {
                return false;
            }

            // A rune outside the BMP stringifies to TWO characters, so the
            // switch this replaced matched nothing for it either.
            if (!key.Character.IsBmp)
            {
                return false;
            }

            return Run(new DiffViewerGesture(KeyCode.Char, (char)key.Character.Value));
        }

        return Run(new DiffViewerGesture(key.Key));
    }

    /// <summary>
    /// Runs the command a gesture resolves to. False when the gesture has no
    /// row: the viewer does not claim the key, and the host keeps it.
    /// </summary>
    private bool Run(DiffViewerGesture gesture)
    {
        if (!DiffViewerCommandTable.Commands.TryGetValue(gesture, out IDiffViewerCommand? command))
        {
            return false;
        }

        command.Execute(this);
        return true;
    }

    /// <summary>
    /// The legacy console vocabulary translated into the kitty one, so the
    /// legacy overload can resolve the SAME command table. Seven rows: the
    /// named keys the viewer binds. A legacy key that is not named here is a
    /// rune, and reaches the table as one.
    /// </summary>
    private static readonly IReadOnlyDictionary<ConsoleKey, KeyCode> LegacyKeyCodes =
        new Dictionary<ConsoleKey, KeyCode>
        {
            [ConsoleKey.Escape] = KeyCode.Escape,
            [ConsoleKey.UpArrow] = KeyCode.Up,
            [ConsoleKey.DownArrow] = KeyCode.Down,
            [ConsoleKey.PageUp] = KeyCode.PageUp,
            [ConsoleKey.PageDown] = KeyCode.PageDown,
            [ConsoleKey.Home] = KeyCode.Home,
            [ConsoleKey.End] = KeyCode.End,
        };

    /// <summary>
    /// Fullscreen box: the whole viewport when it fits the minimums, default
    /// (empty) otherwise. Single source of truth for <see cref="Paint"/> and
    /// the layer seating (<see cref="DiffViewerOverlayLayer"/> reports this as
    /// its bounds).
    /// </summary>
    public Rect ComputeBox(Rect viewport)
    {
        if (viewport.Width < MinWidth || viewport.Height < MinHeight)
        {
            return default;
        }

        return new Rect(viewport.X, viewport.Y, viewport.Width, viewport.Height);
    }

    /// <summary>
    /// Paints the fullscreen viewer inside <paramref name="viewport"/>
    /// (typically the full screen). No-op when hidden or too small.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect viewport)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(buffer);
        if (!Visible)
        {
            return;
        }

        var box = ComputeBox(viewport);
        if (box.Width < MinWidth || box.Height < MinHeight)
        {
            return;
        }

        if (box.X >= buffer.Cols || box.Y >= buffer.Rows)
        {
            return;
        }

        PanelChrome.PaintBorderBox(buffer, box);

        int innerW = box.Width - 2;
        int bodyH = Math.Max(0, box.Height - ChromeRows);
        _lastBodyH = Math.Max(1, bodyH);

        string modeTag = Mode == DiffViewerMode.Unified ? "unified" : "split";
        string wrapTag = Wrap ? "wrap" : "nowrap";
        string header = $"Diff — {FilePath}  hunk {Math.Min(_selectedHunk + 1, Math.Max(1, _hunks.Count))}/{Math.Max(1, _hunks.Count)}  [{modeTag}]  [{wrapTag}]";
        buffer.SetText(box.X + 1, box.Y + 1, Harbor.Abstractions.Extensions.StringTruncation.Truncate(header, innerW), new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold));

        var rows = BuildRows(innerW);
        _lastRows = rows.Count;
        EnsureVisible(rows);

        int bodyTop = box.Y + 2;
        for (int i = 0; i < bodyH && _scroll + i < rows.Count; i++)
        {
            PaintRow(buffer, box.X + 1, bodyTop + i, innerW, rows[_scroll + i]);
        }

        const string Hints = "n/p hunk | s split | w wrap | q close";
        buffer.SetText(box.X + 1, box.Bottom - 2, Harbor.Abstractions.Extensions.StringTruncation.Truncate(Hints, innerW), ChatPalette.Dim);
    }

    internal IReadOnlyList<DiffBodyRow> BuildRows(int innerW)
    {
        var rows = new List<DiffBodyRow>();
        var dim = ChatPalette.Dim;
        foreach (string line in _preamble)
        {
            foreach (string chunk in Flow(line, innerW))
            {
                rows.Add(new DiffBodyRow(-1, false, "  " + chunk, dim));
            }
        }

        for (int h = 0; h < _hunks.Count; h++)
        {
            var hunk = _hunks[h];
            bool selected = h == _selectedHunk;
            string title = hunk.Header.Length > 0 ? hunk.Header : $"(hunk {h + 1})";
            var titleStyle = selected
                ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold)
                : new CellStyle(ChatPalette.Text, attrs: StyleAttr.Bold);
            foreach (string chunk in Flow((selected ? "› " : "  ") + title, innerW))
            {
                rows.Add(new DiffBodyRow(h, true, chunk, titleStyle));
            }

            if (hunk.Lines.Count == 0)
            {
                rows.Add(new DiffBodyRow(h, false, "  (no changes)", dim));
                continue;
            }

            if (Mode == DiffViewerMode.Unified)
            {
                foreach (var line in hunk.Lines)
                {
                    string prefix = line.Kind switch
                    {
                        DiffViewerLineKind.Added => "+ ",
                        DiffViewerLineKind.Removed => "- ",
                        _ => "  ",
                    };
                    var style = line.Kind switch
                    {
                        DiffViewerLineKind.Added => ChatPalette.ToolOk,
                        DiffViewerLineKind.Removed => ChatPalette.ToolError,
                        _ => ChatPalette.ToolBody,
                    };
                    foreach (string chunk in Flow(prefix + line.Text, innerW))
                    {
                        rows.Add(new DiffBodyRow(h, false, chunk, style));
                    }
                }
            }
            else
            {
                int leftW = (innerW - 1) / 2;
                int rightW = innerW - 1 - leftW;
                var left = new List<(string Text, CellStyle Style)>();
                var right = new List<(string Text, CellStyle Style)>();
                foreach (var line in hunk.Lines)
                {
                    if (line.Kind != DiffViewerLineKind.Added)
                    {
                        var style = line.Kind == DiffViewerLineKind.Removed ? ChatPalette.ToolError : ChatPalette.ToolBody;
                        foreach (string chunk in Flow(line.Text, leftW))
                        {
                            left.Add((chunk, style));
                        }
                    }

                    if (line.Kind != DiffViewerLineKind.Removed)
                    {
                        var style = line.Kind == DiffViewerLineKind.Added ? ChatPalette.ToolOk : ChatPalette.ToolBody;
                        foreach (string chunk in Flow(line.Text, rightW))
                        {
                            right.Add((chunk, style));
                        }
                    }
                }

                int paired = Math.Max(left.Count, right.Count);
                for (int i = 0; i < paired; i++)
                {
                    string leftText = i < left.Count ? left[i].Text : string.Empty;
                    string rightText = i < right.Count ? right[i].Text : string.Empty;
                    var leftStyle = i < left.Count ? left[i].Style : ChatPalette.ToolBody;
                    var rightStyle = i < right.Count ? right[i].Style : ChatPalette.ToolBody;
                    rows.Add(new DiffBodyRow(h, false, null, dim, leftText, leftStyle, rightText, rightStyle));
                }
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new DiffBodyRow(0, false, "  (no changes)", dim));
        }

        return rows;
    }

    private void PaintRow(ScreenBuffer buffer, int x, int y, int innerW, DiffBodyRow row)
    {
        if (Mode == DiffViewerMode.Split && !row.IsHeader && row.LeftText is not null)
        {
            int leftW = (innerW - 1) / 2;
            buffer.SetText(x, y, PadRight(row.LeftText, leftW), row.LeftStyle ?? ChatPalette.ToolBody);
            buffer.SetText(x + leftW, y, "│", ChatPalette.Dim);
            buffer.SetText(x + leftW + 1, y, PadRight(row.RightText ?? string.Empty, innerW - 1 - leftW), row.RightStyle ?? ChatPalette.ToolBody);
            return;
        }

        buffer.SetText(x, y, PadRight(row.FullText ?? string.Empty, innerW), row.FullStyle);
    }

    private void EnsureVisible(IReadOnlyList<DiffBodyRow> rows)
    {
        int target = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].HunkIndex == _selectedHunk && rows[i].IsHeader)
            {
                target = i;
                break;
            }
        }

        if (target < _scroll)
        {
            _scroll = target;
        }
        else if (target >= _scroll + _lastBodyH)
        {
            _scroll = target - _lastBodyH + 1;
        }

        ClampScroll();
    }

    private void ClampScroll()
    {
        if (_scroll < 0)
        {
            _scroll = 0;
        }

        int max = Math.Max(0, _lastRows - _lastBodyH);
        if (_scroll > max)
        {
            _scroll = max;
        }
    }

    private void Parse(string diffText)
    {
        _hunks.Clear();
        _preamble.Clear();
        DiffViewerHunk? current = null;
        var fallback = new List<DiffViewerLine>();

        string[] lines = diffText.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Replace("\t", "  ");
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = new DiffViewerHunk(line);
                _hunks.Add(current);
                continue;
            }

            if (current is null && (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal) || line is "---" or "+++"))
            {
                _preamble.Add(line);
                continue;
            }

            DiffViewerLineKind kind;
            string text;
            if (line.Length > 0 && line[0] == '+')
            {
                kind = DiffViewerLineKind.Added;
                text = line[1..];
            }
            else if (line.Length > 0 && line[0] == '-')
            {
                kind = DiffViewerLineKind.Removed;
                text = line[1..];
            }
            else if (line.Length > 0 && line[0] == '\\')
            {
                kind = DiffViewerLineKind.Context;
                text = line;
            }
            else
            {
                kind = DiffViewerLineKind.Context;
                text = line.Length > 0 && line[0] == ' ' ? line[1..] : line;
            }

            if (current is not null)
            {
                current.Add(kind, text);
            }
            else
            {
                fallback.Add(new DiffViewerLine(kind, text));
            }
        }

        if (_hunks.Count == 0)
        {
            var single = new DiffViewerHunk(string.Empty);
            foreach (var line in fallback)
            {
                single.Add(line.Kind, line.Text);
            }

            _hunks.Add(single);
        }
    }

    private List<string> Flow(string text, int width)
    {
        var out_ = new List<string>(1);
        if (width <= 0)
        {
            out_.Add(string.Empty);
            return out_;
        }

        if (Wrap)
        {
            for (int pos = 0; pos < text.Length; pos += width)
            {
                out_.Add(text.Substring(pos, Math.Min(width, text.Length - pos)));
            }

            if (out_.Count == 0)
            {
                out_.Add(string.Empty);
            }

            return out_;
        }

        out_.Add(Harbor.Abstractions.Extensions.StringTruncation.Truncate(text, width));
        return out_;
    }

    private static string PadRight(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (text.Length >= width)
        {
            return text[..width];
        }

        return text.PadRight(width);
    }
}
