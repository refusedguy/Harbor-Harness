using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
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
public sealed class AnsiWriter : ITerminalControlBackend
{
    // Attribute on/off code pairs (§3.2). Bold and dim share the off code 22,
    // which is why turning one of them off may re-emit the other's on code.
    // Underline is deliberately absent: its ON code depends on the 3-bit style
    // in StyleAttr bits 8..10 (single/double/curly/dotted/dashed), so SetStyle
    // emits it through AppendUnderlineOn instead (#2219).
    private static readonly (StyleAttr Flag, int On, int Off)[] AttrTable =
    [
        (StyleAttr.Bold, 1, 22),
        (StyleAttr.Dim, 2, 22),
        (StyleAttr.Italic, 3, 23),
        (StyleAttr.Blink, 5, 25),
        (StyleAttr.Reverse, 7, 27),
        (StyleAttr.Hidden, 8, 28),
        (StyleAttr.Strike, 9, 29),
    ];

    // A colon-form SGR parameter (4:3 curly underline, 4:4 dotted, 4:5 dashed)
    // is stored in the int queue with this high bit set: bits 15..8 hold the
    // first number, bits 7..0 the second. Keeps the queue allocation-free.
    private const int ColonParamFlag = 1 << 16;

    // Interned palette sequences (btop-style interning): \x1b[38;5;Nm / \x1b[48;5;Nm ×256.
    private static readonly byte[][] PaletteFg = BuildPalette(38);
    private static readonly byte[][] PaletteBg = BuildPalette(48);

    private const byte Esc = 0x1B;

    private readonly ITerminalBackend _backend;

    // Non-null iff the backend also implements ISyncTerminalBackend (#468).
    // Cached so the sync path costs a null check, not a type test per frame.
    private readonly ISyncTerminalBackend? _syncBackend;

    private readonly bool _syncUpdates;
    private byte[] _buf = new byte[16 * 1024];
    private int _len;

    // SGR automaton state — invalidated at BeginFrame (styles are not carried
    // across frames because non-frame writers may touch the terminal too).
    private PackedColor _fg;
    private PackedColor _bg;
    private StyleAttr _attrs;
    private PackedColor _underlineColor;
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
        _syncBackend = backend as ISyncTerminalBackend;
        _syncUpdates = syncUpdates;
    }

    /// <summary>Backend this writer frames through.</summary>
    public ITerminalBackend Backend => _backend;

    /// <summary>
    /// True when the backend implements <see cref="ISyncTerminalBackend"/>, i.e.
    /// when <see cref="FlushSync"/> / <see cref="EndFrame"/> can ship a frame.
    /// Check this BEFORE painting a frame that would have to flush
    /// synchronously (#468).
    /// </summary>
    public bool SupportsSyncWrites => _syncBackend is not null;

    /// <summary>
    /// The backend as a synchronous sink, or throws when it is not one. Use
    /// after <see cref="SupportsSyncWrites"/> when the cost of the type test is
    /// worth avoiding.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The backend does not implement <see cref="ISyncTerminalBackend"/>.
    /// </exception>
    public ISyncTerminalBackend SyncBackend => _syncBackend ?? throw new InvalidOperationException(
        $"{_backend.GetType().Name} implements ITerminalBackend but not ISyncTerminalBackend; " +
        "it cannot back a synchronous flush path (AnsiWriter.FlushSync / AnsiWriter.EndFrame).");

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
        _underlineColor = default;
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

        // #409 write stage — past the empty-frame drop above, so this counts
        // frames that reached the backend, not frames that were asked for.
        EngineStageCounters.CountTerminalWrite();
        await _backend.WriteAsync(_buf.AsMemory(0, _len), cancellationToken).ConfigureAwait(false);
        _len = 0;
    }

    /// <summary>
    /// Synchronous twin of <see cref="EndFrameAsync"/> for sync render
    /// contexts (backends implementing <see cref="ISyncTerminalBackend"/>):
    /// identical empty-frame and sync-update semantics, no async machinery.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The backend is async-only. Check <see cref="SupportsSyncWrites"/> before
    /// painting the frame so FRONT is not advanced for a frame that never
    /// shipped (#468).
    /// </exception>
    public void EndFrame()
    {
        int wrapperBytes = _syncUpdates ? 8 : 0;
        if (_len <= wrapperBytes)
        {
            _len = 0;
            return;
        }

        // Resolved BEFORE the wrapper is appended: on an async-only backend
        // the throw must leave the buffer exactly as it was, so the caller can
        // fall back to EndFrameAsync without shipping a half-frame.
        ISyncTerminalBackend sink = SyncBackend;

        if (_syncUpdates)
        {
            AppendAscii("\x1B[?2026l");
        }

        EngineStageCounters.CountTerminalWrite(); // #409 — same site as the async twin above
        sink.Write(_buf.AsSpan(0, _len));
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

    /// <summary>Emits the SGR delta between the current and the target style (no underline color).</summary>
    public void SetStyle(in CellStyle target) => SetStyle(in target, default);

    /// <summary>
    /// Emits the SGR delta between the current pen and <paramref name="target"/>.
    /// <paramref name="underlineColor"/> lives in <see cref="UnderlineColorTable"/>
    /// (a side-table, not a Cell field) and is folded into the same SGR automaton:
    /// SGR 58 selects it, SGR 59 restores the default. Both are zero-width (#2219).
    /// </summary>
    public void SetStyle(in CellStyle target, PackedColor underlineColor)
    {
        bool fgChanged = !_styleKnown || _fg != target.Fg;
        bool bgChanged = !_styleKnown || _bg != target.Bg;
        bool attrsChanged = !_styleKnown || _attrs != target.Attrs;
        bool ulColorChanged = !_styleKnown || _underlineColor != underlineColor;
        if (!fgChanged && !bgChanged && !attrsChanged && !ulColorChanged)
        {
            return;
        }

        // The generic table only knows the eight SGR bits (byte 0); the
        // underline-style bits keep their own on/off codes below.
        const StyleAttr NonUnderline = (StyleAttr)0xFF;

        bool targetUnderline = (target.Attrs & StyleAttr.Underline) != 0;
        bool prevUnderline = (_attrs & StyleAttr.Underline) != 0;
        var targetStyle = UnderlineStyle.Decode(target.Attrs);
        var prevStyle = UnderlineStyle.Decode(_attrs);
        bool underlineChanged = targetUnderline != prevUnderline
            || (targetUnderline && targetStyle != prevStyle);

        // Plain target is always cheapest expressed as a single reset. SGR 0
        // also clears the underline color, so this is only valid for a default one.
        if (target.IsPlain && underlineColor.IsDefault)
        {
            ResetStyle();
            return;
        }

        // Threshold stays at 3 (pin the pre-existing collapse contract): the
        // underline-color group only participates when it actually changes, so
        // the classic three-group transition still takes the reset path.
        int groups = (fgChanged ? 1 : 0) + (bgChanged ? 1 : 0) + (attrsChanged ? 1 : 0);
        if (!_styleKnown || groups >= 3)
        {
            // Reset + reapply beats three independent deltas. SGR 0 already
            // restores both colors to default — re-emit only explicit ones.
            ResetStyle();
            AppendAttrs(target.Attrs);
            if (targetUnderline)
            {
                AppendUnderlineOn(targetStyle);
            }

            if (!target.Fg.IsDefault)
            {
                AppendColor(target.Fg, isForeground: true);
            }

            if (!target.Bg.IsDefault)
            {
                AppendColor(target.Bg, isForeground: false);
            }

            if (!underlineColor.IsDefault)
            {
                AppendUnderlineColor(underlineColor);
            }
        }
        else
        {
            if (attrsChanged)
            {
                // Off-params and on-params share one SGR (tcell-style pairing).
                var turnedOff = (_attrs & NonUnderline) & ~(target.Attrs & NonUnderline);
                var turnedOn = (target.Attrs & NonUnderline) & ~(_attrs & NonUnderline);
                if (turnedOff != StyleAttr.None)
                {
                    QueueAttrOff(turnedOff, keep: target.Attrs & NonUnderline);
                }

                if (turnedOn != StyleAttr.None)
                {
                    QueueAttrs(turnedOn);
                }
            }

            if (underlineChanged)
            {
                if (prevUnderline)
                {
                    // SGR 24 is the single off code for every underline style.
                    QueueSgrParam(24);
                }

                if (targetUnderline)
                {
                    AppendUnderlineOn(targetStyle);
                }
            }

            FlushPendingSgr();

            if (fgChanged)
            {
                AppendColor(target.Fg, isForeground: true);
            }

            if (bgChanged)
            {
                AppendColor(target.Bg, isForeground: false);
            }

            if (ulColorChanged)
            {
                AppendUnderlineColor(underlineColor);
            }
        }

        // A trailing underline on-code or 58/59 param must not survive into a
        // later sequence: MoveTo/EndFrame do not flush pending SGR (#2219).
        FlushPendingSgr();
        _styleKnown = true;
        _fg = target.Fg;
        _bg = target.Bg;
        _attrs = target.Attrs;
        _underlineColor = underlineColor;
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
        _underlineColor = default; // SGR 0 clears the underline color too
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
        // R1 steal (epic #1155, ratatui VS16 backend rule): terminals disagree
        // on the advance of VS16 presentation sequences (1 vs 2 columns), so
        // after emitting one the tracked pen is unknowable — mark it unknown
        // so the next MoveTo is always emitted instead of inheriting a drifted
        // column. Certain-width runs keep the tracked pen exactly as before.
        // PF1 (#1237): the VS16 probe is a vectorized '\uFE0F' scan (BCL
        // Vector{ushort}), bit-identical to UnicodeWidth.HasUncertainWidth.
        bool uncertain = ContainsVs16(text);
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

        if (uncertain)
        {
            _posX = -1;
            _posY = -1;
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

    /// <summary>
    /// Span twin of <see cref="Raw(string)" /> for payloads that are already
    /// bytes — inline-image escape sequences (kitty APC / OSC 1337) ride the
    /// same frame buffer and the same single backend write, never a
    /// <c>Console.Write</c> side channel. No intermediate string, so a
    /// multi-megabyte base64 payload costs one copy and nothing else.
    /// </summary>
    public void RawBytes(ReadOnlySpan<byte> sequence)
    {
        EnsureCapacity(sequence.Length);
        sequence.CopyTo(_buf.AsSpan(_len));
        _len += sequence.Length;
    }

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

    /// <summary>Slim-trait cursor positioning (issue #1160): 0-based CUP via <see cref="MoveTo"/>.</summary>
    public void SetCursorPosition(int x, int y) => MoveTo(x, y);

    /// <summary>
    /// Slim-trait cursor save (issue #1160 audit): DECSC (<c>ESC 7</c>),
    /// emit-only — never a terminal query (ratatui #2691 race lesson).
    /// </summary>
    public void SaveCursor()
    {
        FlushPendingSgr();
        EnsureCapacity(2);
        _buf[_len++] = Esc;
        _buf[_len++] = (byte)'7';
    }

    /// <summary>
    /// Slim-trait cursor restore (issue #1160 audit): DECRC (<c>ESC 8</c>),
    /// emit-only — never a terminal query (ratatui #2691 race lesson).
    /// </summary>
    public void RestoreCursor()
    {
        FlushPendingSgr();
        EnsureCapacity(2);
        _buf[_len++] = Esc;
        _buf[_len++] = (byte)'8';
    }

    /// <summary>Slim-trait whole-screen erase (issue #1160): ED 2, pen unchanged.</summary>
    public void ClearScreen() => EmitEraseInDisplay(2);

    /// <summary>Slim-trait scroll-region set (issue #1160): DECSTBM, not a <c>Raw</c> escape insert.</summary>
    public void SetScrollRegion(int topRow, int bottomRow)
    {
        FlushPendingSgr();
        EnsureCapacity(16);
        AppendCsi();
        AppendDigits(Math.Max(0, topRow) + 1);
        _buf[_len++] = (byte)';';
        AppendDigits(Math.Max(Math.Max(0, topRow) + 1, bottomRow + 1));
        _buf[_len++] = (byte)'r';
        InvalidateCursorPosition();
    }

    /// <summary>Slim-trait scroll-region reset (issue #1160): full screen, not a <c>Raw</c> escape insert.</summary>
    public void ResetScrollRegion()
    {
        FlushPendingSgr();
        AppendAscii("\x1B[r");
        InvalidateCursorPosition();
    }

    /// <summary>Slim-trait scroll-region up (issue #1160): SU, not a <c>Raw</c> escape insert.</summary>
    public void ScrollRegionUp(int lines)
    {
        FlushPendingSgr();
        EnsureCapacity(12);
        AppendCsi();
        AppendDigits(Math.Max(1, lines));
        _buf[_len++] = (byte)'S';
        InvalidateCursorPosition();
    }

    /// <summary>Slim-trait scroll-region down (issue #1160): SD, not a <c>Raw</c> escape insert.</summary>
    public void ScrollRegionDown(int lines)
    {
        FlushPendingSgr();
        EnsureCapacity(12);
        AppendCsi();
        AppendDigits(Math.Max(1, lines));
        _buf[_len++] = (byte)'T';
        InvalidateCursorPosition();
    }

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
    /// Purely additive: routes through <see cref="ISyncTerminalBackend.Write"/>
    /// so the SGR automaton and its buffer management remain untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The backend is async-only — see <see cref="SupportsSyncWrites"/> (#468).
    /// </exception>
    public void FlushSync()
    {
        if (_len == 0)
        {
            return;
        }

        SyncBackend.Write(_buf.AsSpan(0, _len));
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

    /// <summary>Queues a colon-form SGR parameter such as <c>4:3</c> (curly underline).</summary>
    private void AppendSgrColon(int first, int second) =>
        AppendSgrParam(ColonParamFlag | (first << 8) | second);

    /// <summary>
    /// ON code for an underline style. Single is the classic SGR 4; the four
    /// extended styles use SGR 21 and the <c>4:n</c> colon form understood by
    /// kitty, iTerm2, WezTerm, Ghostty and Windows Terminal (#2219).
    /// </summary>
    private void AppendUnderlineOn(UnderlineKind kind)
    {
        switch (kind)
        {
            case UnderlineKind.Double:
                AppendSgrParam(21);
                break;
            case UnderlineKind.Curly:
                AppendSgrColon(4, 3);
                break;
            case UnderlineKind.Dotted:
                AppendSgrColon(4, 4);
                break;
            case UnderlineKind.Dashed:
                AppendSgrColon(4, 5);
                break;
            default:
                AppendSgrParam(4);
                break;
        }
    }

    /// <summary>
    /// SGR 58;5;n / 58;2;r;g;b selects the underline color; SGR 59 restores the
    /// terminal default. Params join the pending SGR so one CSI carries the
    /// whole style delta (#2219).
    /// </summary>
    private void AppendUnderlineColor(PackedColor color)
    {
        if (color.IsDefault)
        {
            AppendSgrParam(59);
            return;
        }

        AppendSgrParam(58);
        if (!color.IsRgb)
        {
            AppendSgrParam(5);
            AppendSgrParam(color.Index);
        }
        else
        {
            var (r, g, b) = color.RgbChannels;
            AppendSgrParam(2);
            AppendSgrParam(r);
            AppendSgrParam(g);
            AppendSgrParam(b);
        }
    }

    private void FlushPendingSgr()
    {
        if (_sgrParams == 0)
        {
            return;
        }

        // Colon params render as at most "NNN:NNN" (7 bytes); 8 is a safe cap.
        EnsureCapacity((_sgrParams * 8) + 2);
        for (int i = 0; i < _sgrParams; i++)
        {
            if (i > 0)
            {
                _buf[_len++] = (byte)';';
            }

            int value = _sgrParamValues[i];
            if ((value & ColonParamFlag) != 0)
            {
                AppendDigits((value >> 8) & 0xFF);
                _buf[_len++] = (byte)':';
                AppendDigits(value & 0xFF);
            }
            else
            {
                AppendDigits(value);
            }
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

    /// <summary>
    /// Vectorized VS16 probe (PF1, #1237): true when <paramref name="text"/>
    /// contains U+FE0F. Same result as
    /// <see cref="UnicodeWidth.HasUncertainWidth"/> — VS16 is BMP, so a
    /// UTF-16 code-unit scan is exact. BCL <c>Vector{ushort}</c> only.
    /// </summary>
    private static bool ContainsVs16(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<ushort> units = MemoryMarshal.Cast<char, ushort>(text);
        int width = Vector<ushort>.Count;
        int limit = units.Length - (units.Length % width);
        var target = new Vector<ushort>(0xFE0F);
        int i = 0;
        for (; i < limit; i += width)
        {
            var v = new Vector<ushort>(units.Slice(i, width));
            if (Vector.EqualsAny(v, target))
            {
                return true;
            }
        }

        for (; i < units.Length; i++)
        {
            if (units[i] == 0xFE0F)
            {
                return true;
            }
        }

        return false;
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

    // One-time cost: runs once per process in the static initializers above
    // (PaletteFg/PaletteBg), never per frame — the ToArray here builds the
    // interned palette, it is not a snapshot copy.
    private static byte[][] BuildPalette(int selector) =>
        Enumerable.Range(0, 256)
            .Select(i => Encoding.ASCII.GetBytes($"\x1B[{selector};5;{i}m"))
            .ToArray();
}
