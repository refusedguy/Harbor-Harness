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
    /// Routes a legacy console key. <c>Esc</c>/<c>q</c> closes; <c>n</c>/<c>j</c>
    /// and <c>↓</c> go to the next hunk, <c>p</c>/<c>k</c> and <c>↑</c> to the
    /// previous one; <c>s</c>/<c>t</c> toggles split/unified, <c>w</c> toggles
    /// wrap; <c>g</c>/<c>Home</c> jumps first, <c>G</c>/<c>End</c> last;
    /// <c>PgUp</c>/<c>PgDn</c> scroll. Ctrl/Alt chords pass through untouched.
    /// </summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (!Visible)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                Hide();
                return true;
            case ConsoleKey.UpArrow:
                PreviousHunk();
                return true;
            case ConsoleKey.DownArrow:
                NextHunk();
                return true;
            case ConsoleKey.PageUp:
                ScrollBy(-Math.Max(1, _lastBodyH));
                return true;
            case ConsoleKey.PageDown:
                ScrollBy(Math.Max(1, _lastBodyH));
                return true;
            case ConsoleKey.Home:
                FirstHunk();
                return true;
            case ConsoleKey.End:
                LastHunk();
                return true;
        }

        if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0)
        {
            return false;
        }

        switch (key.KeyChar)
        {
            case 'q':
                Hide();
                return true;
            case 'n':
            case 'j':
                NextHunk();
                return true;
            case 'p':
            case 'k':
                PreviousHunk();
                return true;
            case 's':
            case 't':
                ToggleMode();
                return true;
            case 'w':
                ToggleWrap();
                return true;
            case 'g':
                FirstHunk();
                return true;
            case 'G':
                LastHunk();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Routes a decoded key (kitty-capable hosts). Mirrors the
    /// <see cref="ConsoleKeyInfo"/> contract; Shift/Alt/Ctrl/Meta character
    /// chords pass through untouched (the host keymap owns them).
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Escape:
                Hide();
                return true;
            case KeyCode.Up:
                PreviousHunk();
                return true;
            case KeyCode.Down:
                NextHunk();
                return true;
            case KeyCode.PageUp:
                ScrollBy(-Math.Max(1, _lastBodyH));
                return true;
            case KeyCode.PageDown:
                ScrollBy(Math.Max(1, _lastBodyH));
                return true;
            case KeyCode.Home:
                FirstHunk();
                return true;
            case KeyCode.End:
                LastHunk();
                return true;
            case KeyCode.Char:
                if ((key.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
                {
                    return false;
                }

                switch (key.Character.ToString())
                {
                    case "q":
                        Hide();
                        return true;
                    case "n":
                    case "j":
                        NextHunk();
                        return true;
                    case "p":
                    case "k":
                        PreviousHunk();
                        return true;
                    case "s":
                    case "t":
                        ToggleMode();
                        return true;
                    case "w":
                        ToggleWrap();
                        return true;
                    case "g":
                        FirstHunk();
                        return true;
                    case "G":
                        LastHunk();
                        return true;
                    default:
                        return false;
                }
            default:
                return false;
        }
    }

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
        ArgumentNullException.ThrowIfNull(buffer);
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

        DrawBox(buffer, box);

        int innerW = box.Width - 2;
        int bodyH = Math.Max(0, box.Height - ChromeRows);
        _lastBodyH = Math.Max(1, bodyH);

        string modeTag = Mode == DiffViewerMode.Unified ? "unified" : "split";
        string wrapTag = Wrap ? "wrap" : "nowrap";
        string header = $"Diff — {FilePath}  hunk {Math.Min(_selectedHunk + 1, Math.Max(1, _hunks.Count))}/{Math.Max(1, _hunks.Count)}  [{modeTag}]  [{wrapTag}]";
        buffer.SetText(box.X + 1, box.Y + 1, Truncate(header, innerW), new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold));

        var rows = BuildRows(innerW);
        _lastRows = rows.Count;
        EnsureVisible(rows);

        int bodyTop = box.Y + 2;
        for (int i = 0; i < bodyH && _scroll + i < rows.Count; i++)
        {
            PaintRow(buffer, box.X + 1, bodyTop + i, innerW, rows[_scroll + i]);
        }

        const string Hints = "n/p hunk | s split | w wrap | q close";
        buffer.SetText(box.X + 1, box.Bottom - 2, Truncate(Hints, innerW), ChatPalette.Dim);
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

        out_.Add(Truncate(text, width));
        return out_;
    }

    private static string Truncate(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= width)
        {
            return text;
        }

        return text[..Math.Max(0, width - 1)] + "…";
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

    private static void DrawBox(ScreenBuffer buffer, Rect rect)
    {
        var fillStyle = new CellStyle(ChatPalette.Panel);
        var borderStyle = new CellStyle(ChatPalette.Border);
        buffer.Fill(rect, Cell.From(new Rune(' '), fillStyle));
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        int x1 = rect.X, y1 = rect.Y, x2 = rect.Right - 1, y2 = rect.Bottom - 1;
        buffer.At(x1, y1) = Cell.From(new Rune('╭'), borderStyle);
        buffer.At(x2, y1) = Cell.From(new Rune('╮'), borderStyle);
        buffer.At(x1, y2) = Cell.From(new Rune('╰'), borderStyle);
        buffer.At(x2, y2) = Cell.From(new Rune('╯'), borderStyle);
        for (int x = x1 + 1; x < x2; x++)
        {
            buffer.At(x, y1) = Cell.From(new Rune('─'), borderStyle);
            buffer.At(x, y2) = Cell.From(new Rune('─'), borderStyle);
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buffer.At(x1, y) = Cell.From(new Rune('│'), borderStyle);
            buffer.At(x2, y) = Cell.From(new Rune('│'), borderStyle);
        }
    }
}
