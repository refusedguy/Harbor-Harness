using System.Text;
using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// Tab strip / pages header (ratatui <c>Tabs</c> + Terminal.Gui <c>TabView</c>
/// pattern): a single row of padded titles joined by a divider, with an
/// underline marker beneath the selected title. Needed by panel switching
/// (UX1: one strip per dock, the arbiter winner is the selected tab) and the
/// onboarding wizard (UX8: hosts prefix step numbers into the titles, e.g.
/// <c>"1. Model"</c> — numbering stays host-side on purpose).
///
/// Host-agnostic and renderer-agnostic: selection state lives here
/// (<see cref="Select"/>, <see cref="Next"/>, <see cref="Prev"/>,
/// <see cref="HandleKey"/>), page CONTENT stays with the host — this block
/// never paints page bodies, only the header (titles row + underline row).
/// Implements <see cref="IFocusTarget"/> so the host focus router can
/// traverse it via Tab; focus only brightens the underline (accent vs dim),
/// the selected title itself is always accent-bold.
/// </summary>
public sealed class Tabs : IChatBlock, IFocusTarget
{
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);

    private readonly List<string> _titles = [];
    private readonly int _budgetBytes;
    private int _selected;
    private int _start;
    private bool _focused;
    private string _divider = "│";

    /// <summary>Empty strip (hosts add pages via titles only — see remarks).</summary>
    public Tabs()
    {
        _budgetBytes = 64;
    }

    /// <summary>Strip over the given page titles (sanitized, never empty).</summary>
    public Tabs(IEnumerable<string>? titles)
    {
        int bytes = 64;
        if (titles is not null)
        {
            foreach (var t in titles)
            {
                var clean = Clean(t);
                _titles.Add(clean);
                bytes += clean.Length * 2;
            }
        }

        _budgetBytes = bytes;
    }

    /// <summary>Stable router id — unique per strip instance.</summary>
    public string Id => $"tabs:{_id}";

    public void OnFocusChanged(bool focused) => _focused = focused;

    /// <summary>True while this strip holds keyboard focus.</summary>
    public bool Focused => _focused;

    public string Kind => "tabs";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => _budgetBytes;

    /// <summary>Page titles (sanitized single-line, never empty).</summary>
    public IReadOnlyList<string> Titles => _titles;

    /// <summary>Number of pages.</summary>
    public int Count => _titles.Count;

    /// <summary>Selected page index (clamped to the title list; 0 when empty).</summary>
    public int Selected => _titles.Count == 0 ? 0 : Math.Clamp(_selected, 0, _titles.Count - 1);

    /// <summary>Selected page title (<c>"?"</c> when empty).</summary>
    public string SelectedTitle => _titles.Count == 0 ? "?" : _titles[Selected];

    /// <summary>
    /// Glyph painted between padded titles (default <c>│</c>, ratatui Tabs
    /// divider). Blank/whitespace assignments fall back to the default.
    /// </summary>
    public string Divider
    {
        get => _divider;
        set => _divider = string.IsNullOrWhiteSpace(value) ? "│" : value.Trim();
    }

    /// <summary>First visible tab in the scroll window (0 until overflow).</summary>
    public int FirstVisible => _start;

    /// <summary>Screen-space clip rect from the last <see cref="Paint"/> pass.</summary>
    internal Rect? LastPaintRect { get; private set; }

    /// <summary>Raised on selection changes so the host can mark the slot dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Selects a page (clamped). Returns true when the selection changed.
    /// </summary>
    public bool Select(int index)
    {
        if (_titles.Count == 0)
        {
            return false;
        }

        int next = Math.Clamp(index, 0, _titles.Count - 1);
        if (next == _selected)
        {
            return false;
        }

        _selected = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Selects the next page (wraps). Returns true when the selection changed.</summary>
    public bool Next() => _titles.Count > 1 && Select(_selected + 1 >= _titles.Count ? 0 : _selected + 1);

    /// <summary>Selects the previous page (wraps). Returns true when the selection changed.</summary>
    public bool Prev() => _titles.Count > 1 && Select(_selected - 1 < 0 ? _titles.Count - 1 : _selected - 1);

    public BlockMeasure Measure(int width) => BlockMeasure.Exact(_titles.Count == 0 ? 1 : 2);

    public int CheapEstimate(int width) => _titles.Count == 0 ? 1 : 2;

    /// <summary>
    /// Route one key event (press/repeat only, no modifiers). Left/Right and
    /// h/l move, Home/End jump, Tab cycles forward (the TabView reason to
    /// exist), digits 1..9 jump straight to a page. Up/Down/Enter/Space are
    /// NOT consumed — activating a page belongs to the host. Returns true
    /// when the key was consumed.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if ((key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
            || key.Modifiers != KeyModifiers.None)
        {
            return false;
        }

        if (_titles.Count == 0)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Left:
                return Prev();
            case KeyCode.Right:
                return Next();
            case KeyCode.Home:
                return Select(0);
            case KeyCode.End:
                return Select(_titles.Count - 1);
            case KeyCode.Tab:
                return Next();
            case KeyCode.Char:
                return HandleChar(key.Character);
            default:
                return false;
        }
    }

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        int width = ctx.Rect.Width;
        int height = ctx.Rect.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        LastPaintRect = ctx.Rect;

        if (_titles.Count == 0)
        {
            if (ctx.SkipRows == 0)
            {
                buffer.SetText(ctx.Rect.X, ctx.Rect.Y, "(empty)", ChatPalette.Dim);
            }

            return;
        }

        EnsureSelectedVisible(width);

        int start = Math.Min(ctx.SkipRows, 2);
        int rows = Math.Min(height, 2 - start);
        int right = ctx.Rect.Right;

        // Row layout first (paint rows may be skipped, geometry must not).
        int selX0 = ctx.Rect.X;
        int selTitleCells = 0;
        {
            int x = ctx.Rect.X;
            int dividerCells = CellWidth(_divider);
            for (int i = _start; i < _titles.Count && x < right; i++)
            {
                if (i > _start)
                {
                    x += dividerCells;
                }

                if (i == Selected)
                {
                    selX0 = x;
                    selTitleCells = CellWidth(_titles[i]);
                }

                x += 2 + CellWidth(_titles[i]);
            }
        }

        for (int r = 0; r < rows; r++)
        {
            int y = ctx.Rect.Y + r;
            if (start + r == 0)
            {
                PaintTitlesRow(buffer, ctx.Rect.X, y, right);
            }
            else
            {
                PaintUnderlineRow(buffer, ctx.Rect.X, y, right, selX0, selTitleCells);
            }
        }
    }

    public string RawText()
    {
        if (_titles.Count == 0)
        {
            return "(empty)";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < _titles.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" │ ");
            }

            if (i == Selected)
            {
                sb.Append('[').Append(_titles[i]).Append(']');
            }
            else
            {
                sb.Append(_titles[i]);
            }
        }

        return sb.ToString();
    }

    private bool HandleChar(Rune c)
    {
        // Vim mirrors (case-insensitive); digits jump to pages 1..9.
        switch (Rune.ToUpperInvariant(c).Value)
        {
            case 'H':
                return Prev();
            case 'L':
                return Next();
            default:
                break;
        }

        if (c.Value is >= '1' and <= '9')
        {
            int index = (int)c.Value - '1';
            return index < _titles.Count && Select(index);
        }

        return false;
    }

    private void PaintTitlesRow(ScreenBuffer buffer, int x0, int y, int right)
    {
        var dividerStyle = ChatPalette.Dim;
        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var plainStyle = ChatPalette.ToolArgs;

        int x = x0;
        for (int i = _start; i < _titles.Count && x < right; i++)
        {
            if (i > _start)
            {
                int avail = right - x;
                if (avail <= 0)
                {
                    break;
                }

                var div = _divider.AsSpan(0, Math.Min(_divider.Length, avail));
                buffer.SetText(x, y, div, in dividerStyle);
                x += CellWidth(_divider);
                if (x >= right)
                {
                    break;
                }
            }

            int segAvail = right - x;
            if (segAvail <= 0)
            {
                break;
            }

            // Padded segment: " title " — truncated char-wise past the edge
            // (BlockMath precedent: SetText clips, never throws).
            string segment = " " + _titles[i] + " ";
            if (segment.Length > segAvail)
            {
                segment = segment[..segAvail];
            }

            bool selected = i == Selected;
            if (selected)
            {
                buffer.SetText(x, y, segment, in selectedStyle);
            }
            else
            {
                buffer.SetText(x, y, segment, in plainStyle);
            }

            x += 2 + CellWidth(_titles[i]);
        }
    }

    private void PaintUnderlineRow(
        ScreenBuffer buffer, int x0, int y, int right, int selX0, int selTitleCells)
    {
        if (selTitleCells <= 0)
        {
            return;
        }

        var style = _focused
            ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold)
            : ChatPalette.Dim;
        var glyph = new Rune('─');

        // Underline the title text only (not the padding): +1 past the
        // segment start, clipped to the rect.
        int from = Math.Max(selX0 + 1, x0);
        int to = Math.Min(selX0 + 1 + selTitleCells, right);
        for (int cx = from; cx < to; cx++)
        {
            buffer.SetRune(cx, y, glyph, in style);
        }
    }

    /// <summary>
    /// Scrolls the window so the selected tab is visible: clamps back when
    /// the selection moves left, advances <see cref="_start"/> while the run
    /// [<see cref="_start"/> .. selected] overflows <paramref name="width"/>.
    /// </summary>
    private void EnsureSelectedVisible(int width)
    {
        _selected = Math.Clamp(_selected, 0, _titles.Count - 1);
        if (_selected < _start)
        {
            _start = _selected;
        }

        while (_start < _selected && RowWidth(_start, _selected + 1) > width)
        {
            _start++;
        }
    }

    /// <summary>Cell width of tabs [first .. lastExclusive] incl. dividers.</summary>
    private int RowWidth(int first, int lastExclusive)
    {
        int total = 0;
        for (int i = first; i < lastExclusive; i++)
        {
            if (i > first)
            {
                total += CellWidth(_divider);
            }

            total += 2 + CellWidth(_titles[i]);
        }

        return total;
    }

    private static int CellWidth(string text) => UnicodeWidth.Width(text.AsSpan());

    private static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "?";
        }

        return title.Trim()
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');
    }
}
