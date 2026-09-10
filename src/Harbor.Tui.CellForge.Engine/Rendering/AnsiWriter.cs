using System.Buffers;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Frame assembler (celldiff §3): every frame is accumulated in one reusable
/// byte buffer and leaves through a single backend write inside the
/// synchronized-update wrapper <c>CSI ?2026 h … l</c> (only when the DECRQM
/// probe confirmed support).
///
/// Built-in minimizations:
/// <list type="bullet">
///   <item><description>SGR automaton — only the delta between the current
///     and target style is emitted; palette colors come from precomputed
///     interned sequences; ≥3 changed groups collapse into
///     <c>SGR 0</c> + reapply.</description></item>
///   <item><description>Cursor elision — absolute addressing is skipped when
///     the tracked pen position already matches.</description></item>
/// </list>
/// </summary>
public sealed class AnsiWriter
{
    // Attribute on/off code pairs (§3.2). Bold and dim share the off code 22,
    // which is why turning one of them off may re-emit the other's on code.
    private static readonly (StyleAttr Flag, int On, int Off)[] AttrTable =
    [
        (StyleAttr.Bold, 1, 22),
        (StyleAttr.Dim, 2, 22),
        (StyleAttr.Italic, 3, 23),
        (StyleAttr.Underline, 4, 24),
        (StyleAttr.Blink, 5, 25),
        (StyleAttr.Reverse, 7, 27),
        (StyleAttr.Hidden, 8, 28),
        (StyleAttr.Strike, 9, 29),
    ];

    // Interned palette sequences (btop-style interning): \x1b[38;5;Nm / \x1b[48;5;Nm ×256.
    private static readonly byte[][] PaletteFg = BuildPalette(38);
    private static readonly byte[][] PaletteBg = BuildPalette(48);

    private const byte Esc = 0x1B;

    private readonly ITerminalBackend _backend;
    private readonly bool _syncUpdates;
    private byte[] _buf = new byte[16 * 1024];
    private int _len;

    // SGR automaton state — invalidated at BeginFrame (styles are not carried
    // across frames because non-frame writers may touch the terminal too).
    private PackedColor _fg;
    private PackedColor _bg;
    private StyleAttr _attrs;
    private bool _styleKnown;

    // Tracked pen position for elision (-1 = unknown).
    private int _posX = -1;
    private int _posY = -1;

    // Pending-SGR accumulation: consecutive numeric params merge into one CSI.
    private int _sgrParams;
    private readonly int[] _sgrParamValues = new int[16];

    public AnsiWriter(ITerminalBackend backend, bool syncUpdates = false)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _syncUpdates = syncUpdates;
    }

    /// <summary>Current tracked pen column (-1 when unknown).</summary>
    public int TrackedX => _posX;

    /// <summary>Current tracked pen row (-1 when unknown).</summary>
    public int TrackedY => _posY;

    /// <summary>Starts a new frame: buffer reuse + state invalidation + sync-on.</summary>
    public void BeginFrame()
    {
        FlushPendingSgr();
        _len = 0;
        _styleKnown = false;
        _posX = -1;
        _posY = -1;
        if (_syncUpdates)
        {
            AppendAscii("\x1B[?2026h");
        }
    }

    /// <summary>Flushes the frame atomically: sync-off + one backend write.
    /// A frame that carried no content at all is dropped entirely.</summary>
    public async ValueTask EndFrameAsync(CancellationToken cancellationToken = default)
    {
        int wrapperBytes = _syncUpdates ? 8 : 0;
        if (_len <= wrapperBytes)
        {
            _len = 0;
            return;
        }

        if (_syncUpdates)
        {
            AppendAscii("\x1B[?2026l");
        }

        await _backend.WriteAsync(_buf.AsMemory(0, _len), cancellationToken).ConfigureAwait(false);
        _len = 0;
    }

    /// <summary>
    /// Synchronous twin of <see cref="EndFrameAsync"/> for sync render
    /// contexts (backends implementing <see cref="ITerminalBackend.Write"/>):
    /// identical empty-frame and sync-update semantics, no async machinery.
    /// </summary>
    public void EndFrame()
    {
        int wrapperBytes = _syncUpdates ? 8 : 0;
        if (_len <= wrapperBytes)
        {
            _len = 0;
            return;
        }

        if (_syncUpdates)
        {
            AppendAscii("\x1B[?2026l");
        }

        _backend.Write(_buf.AsSpan(0, _len));
        _len = 0;
    }

    /// <summary>Absolute cursor positioning with elision.</summary>
    public void MoveTo(int x, int y)
    {
        FlushPendingSgr();
        if (_posX == x && _posY == y)
        {
            return;
        }

        EnsureCapacity(20);
        AppendCsi();
        AppendDigits(y + 1);
        _buf[_len++] = (byte)';';
        AppendDigits(x + 1);
        _buf[_len++] = (byte)'H';
        _posX = x;
        _posY = y;
    }

    /// <summary>Emits the SGR delta between the current and the target style.</summary>
    public void SetStyle(in CellStyle target)
    {
        bool fgChanged = !_styleKnown || _fg != target.Fg;
        bool bgChanged = !_styleKnown || _bg != target.Bg;
        bool attrsChanged = !_styleKnown || _attrs != target.Attrs;
        if (!fgChanged && !bgChanged && !attrsChanged)
        {
            return;
        }

        // Plain target is always cheapest expressed as a single reset.
        if (target.IsPlain)
        {
            ResetStyle();
            return;
        }

        int groups = (fgChanged ? 1 : 0) + (bgChanged ? 1 : 0) + (attrsChanged ? 1 : 0);
        if (!_styleKnown || groups >= 3)
        {
            // Reset + reapply beats three independent deltas. SGR 0 already
            // restores both colors to default — re-emit only explicit ones.
            ResetStyle();
            AppendAttrs(target.Attrs);
            if (!target.Fg.IsDefault)
            {
                AppendColor(target.Fg, isForeground: true);
            }

            if (!target.Bg.IsDefault)
            {
                AppendColor(target.Bg, isForeground: false);
            }
        }
        else
        {
            if (attrsChanged)
            {
                // Off-params and on-params share one SGR (tcell-style pairing).
                var turnedOff = _attrs & ~target.Attrs;
                var turnedOn = target.Attrs & ~_attrs;
                if (turnedOff != StyleAttr.None)
                {
                    QueueAttrOff(turnedOff, keep: target.Attrs);
                }

                if (turnedOn != StyleAttr.None)
                {
                    QueueAttrs(turnedOn);
                }

                FlushPendingSgr();
            }

            if (fgChanged)
            {
                AppendColor(target.Fg, isForeground: true);
            }

            if (bgChanged)
            {
                AppendColor(target.Bg, isForeground: false);
            }
        }

        _styleKnown = true;
        _fg = target.Fg;
        _bg = target.Bg;
        _attrs = target.Attrs;
    }

    /// <summary>Resets to the plain style unconditionally (one SGR 0).</summary>
    public void ResetStyle()
    {
        AppendSgrParam(0);
        FlushPendingSgr();
        _styleKnown = true;
        _fg = default;
        _bg = default;
        _attrs = StyleAttr.None;
    }

    /// <summary>Writes one rune at the pen position; the pen advances by display width.</summary>
    public void PutRune(Rune rune)
    {
        PutRuneWidth(rune, UnicodeWidth.Width(rune));
    }

    /// <summary>
    /// Writes one rune with an explicit advance (cell-diff passes the grid
    /// width so wide runes advance exactly 2 columns).
    /// </summary>
    public void PutRuneWidth(Rune rune, int advance)
    {
        EnsureCapacity(8);
        _len += rune.EncodeToUtf8(_buf.AsSpan(_len));
        AdvancePen(advance);
    }

    /// <summary>Writes a text run at the pen position, advancing by measured widths.</summary>
    public void WriteText(ReadOnlySpan<char> text)
    {
        var slice = text;
        while (!slice.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(slice, out var rune, out int consumed) == OperationStatus.Done)
            {
                PutRune(rune);
                slice = slice[consumed..];
            }
            else
            {
                PutRuneWidth(Rune.ReplacementChar, 1);
                slice = slice[1..];
            }
        }
    }

    /// <summary>Writes styled text: one style transition before, optional reset after.</summary>
    public void WriteStyledText(ReadOnlySpan<char> text, in CellStyle style, bool resetAfter = true)
    {
        SetStyle(in style);
        WriteText(text);
        if (resetAfter)
        {
            ResetStyle();
        }
    }

    // ── Structural sequences (inline mode / resize policy) ─────────────────

    /// <summary>Appends an arbitrary escape payload without cursor accounting.</summary>
    public void Raw(string sequence) => AppendAscii(sequence);

    /// <summary>Moves up n lines and restarts the pen at column 0 (CUU + CR).</summary>
    public void MoveUpToColumnStart(int lines)
    {
        FlushPendingSgr();
        if (lines == 1)
        {
            AppendAscii("\x1B[A\r");
        }
        else
        {
            EnsureCapacity(12);
            AppendCsi();
            AppendDigits(lines);
            AppendAscii("A\r");
        }

        _posY = _posY >= 0 ? Math.Max(0, _posY - lines) : -1;
        _posX = 0;
    }

    /// <summary>
    /// Erase-in-display ED 0 — from cursor to end of screen. Emits SGR 0 first
    /// so background-color-erase terminals fill with the default bg.
    /// </summary>
    public void EraseFromCursorDown()
    {
        ResetStyle();
        FlushPendingSgr();
        AppendAscii("\x1B[0J");
    }

    /// <summary>Erase-in-display with explicit mode (2 = whole screen; resize policy §4).</summary>
    public void EmitEraseInDisplay(int mode)
    {
        ResetStyle();
        FlushPendingSgr();
        EnsureCapacity(6);
        AppendCsi();
        AppendDigits(mode);
        _buf[_len++] = (byte)'J';
    }

    /// <summary>Erase-in-line EL 2 — wipes the current line (with default bg).</summary>
    public void EraseEntireLine()
    {
        ResetStyle();
        FlushPendingSgr();
        AppendAscii("\x1B[2K");
    }

    /// <summary>Carriage return — pen column becomes 0.</summary>
    public void CarriageReturn()
    {
        EnsureCapacity(1);
        _buf[_len++] = (byte)'\r';
        _posX = 0;
    }

    /// <summary>Newline (CR+LF) — pen moves to column 0 of the next row.</summary>
    public void WriteLineBreak()
    {
        EnsureCapacity(2);
        _buf[_len++] = (byte)'\r';
        _buf[_len++] = (byte)'\n';
        _posX = 0;
        if (_posY >= 0)
        {
            _posY++;
        }
    }

    public void HideCursor() => AppendAscii("\x1B[?25l");

    public void ShowCursor() => AppendAscii("\x1B[?25h");

    /// <summary>Marks the tracked position unknown (after external cursor motion).</summary>
    public void InvalidateCursorPosition()
    {
        _posX = -1;
        _posY = -1;
    }

    /// <summary>Flushes whatever is buffered right now (outside frame pairing).</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_len == 0)
        {
            return;
        }

        await _backend.WriteAsync(_buf.AsMemory(0, _len), cancellationToken).ConfigureAwait(false);
        _len = 0;
    }

    /// <summary>
    /// Synchronous flush twin of <see cref="FlushAsync"/> for sync render
    /// contexts (CellForgeTuiRenderer adapter — renderer-unification sprint).
    /// Purely additive: routes through <see cref="ITerminalBackend.Write"/> so
    /// the SGR automaton and its buffer management remain untouched.
    /// </summary>
    public void FlushSync()
    {
        if (_len == 0)
        {
            return;
        }

        _backend.Write(_buf.AsSpan(0, _len));
        _len = 0;
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private void AdvancePen(int cells)
    {
        if (_posX >= 0)
        {
            _posX += cells;
        }
    }

    private void AppendAttrs(StyleAttr set)
    {
        QueueAttrs(set);
        FlushPendingSgr();
    }

    private void QueueAttrs(StyleAttr set)
    {
        foreach (var (flag, on, _) in AttrTable)
        {
            if ((set & flag) != 0)
            {
                AppendSgrParam(on);
            }
        }
    }

    private void QueueAttrOff(StyleAttr set, StyleAttr keep)
    {
        bool reapplyBold = false;
        bool reapplyDim = false;
        foreach (var (flag, _, off) in AttrTable)
        {
            if ((set & flag) == 0)
            {
                continue;
            }

            AppendSgrParam(off);
            if (off == 22)
            {
                // 22 clears both bold and dim — re-apply whichever survives.
                reapplyBold = (keep & StyleAttr.Bold) != 0;
                reapplyDim = (keep & StyleAttr.Dim) != 0;
            }
        }

        if (reapplyBold)
        {
            AppendSgrParam(1);
        }

        if (reapplyDim)
        {
            AppendSgrParam(2);
        }
    }

    private void AppendSgrParam(int value)
    {
        if (_sgrParams == 0)
        {
            EnsureCapacity(4);
            _buf[_len++] = Esc;
            _buf[_len++] = (byte)'[';
        }

        _sgrParamValues[_sgrParams++] = value;
    }

    private void FlushPendingSgr()
    {
        if (_sgrParams == 0)
        {
            return;
        }

        EnsureCapacity(_sgrParams * 4 + 2);
        for (int i = 0; i < _sgrParams; i++)
        {
            if (i > 0)
            {
                _buf[_len++] = (byte)';';
            }

            AppendDigits(_sgrParamValues[i]);
        }

        _buf[_len++] = (byte)'m';
        _sgrParams = 0;
    }

    private void AppendColor(PackedColor color, bool isForeground)
    {
        if (color.IsDefault)
        {
            AppendSgrParam(isForeground ? 39 : 49);
            FlushPendingSgr();
            return;
        }

        if (!color.IsRgb)
        {
            var interned = (isForeground ? PaletteFg : PaletteBg)[color.Index];
            FlushPendingSgr();
            EnsureCapacity(interned.Length);
            interned.CopyTo(_buf, _len);
            _len += interned.Length;
            return;
        }

        var (r, g, b) = color.RgbChannels;
        FlushPendingSgr();
        EnsureCapacity(20);
        _buf[_len++] = Esc;
        _buf[_len++] = (byte)'[';
        AppendDigits(isForeground ? 38 : 48);
        AppendAscii(";2;");
        AppendDigits(r);
        _buf[_len++] = (byte)';';
        AppendDigits(g);
        _buf[_len++] = (byte)';';
        AppendDigits(b);
        _buf[_len++] = (byte)'m';
    }

    private void AppendCsi()
    {
        _buf[_len++] = Esc;
        _buf[_len++] = (byte)'[';
    }

    private void AppendAscii(string s)
    {
        EnsureCapacity(s.Length);
        foreach (var c in s)
        {
            _buf[_len++] = (byte)c;
        }
    }

    private void AppendDigits(int value)
    {
        if (value == 0)
        {
            _buf[_len++] = (byte)'0';
            return;
        }

        int start = _len;
        while (value > 0)
        {
            _buf[_len++] = (byte)('0' + value % 10);
            value /= 10;
        }

        Array.Reverse(_buf, start, _len - start);
    }

    private void EnsureCapacity(int additional)
    {
        if (_len + additional <= _buf.Length)
        {
            return;
        }

        int target = _buf.Length;
        while (target < _len + additional)
        {
            target *= 2;
        }

        Array.Resize(ref _buf, target);
    }

    private static byte[][] BuildPalette(int selector) =>
        Enumerable.Range(0, 256)
            .Select(i => Encoding.ASCII.GetBytes($"\x1B[{selector};5;{i}m"))
            .ToArray();
}
