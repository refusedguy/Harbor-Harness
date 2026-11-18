using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Byte-emission surface the SGR automaton writes through. Implemented
/// explicitly by <see cref="AnsiWriter"/> so moving the automaton out of that
/// class does not add a single public member to it.
/// </summary>
internal interface ISgrByteSink
{
    void EnsureCapacity(int additional);

    void AppendByte(byte value);

    void AppendAscii(string value);

    void AppendDigits(int value);

    void AppendBytes(ReadOnlySpan<byte> value);
}

/// <summary>
/// SGR automaton: the current pen style (foreground, background, attributes,
/// underline color) plus the pending numeric parameter queue. Emits only the
/// delta between the current and target style; three or more changed groups
/// collapse into <c>SGR 0</c> plus a reapply. Palette sequences are interned
/// once per process.
///
/// Extracted from <see cref="AnsiWriter"/> (#2644) together with its own
/// invariants; <see cref="AnsiWriter"/> keeps the frame buffer, pen tracking
/// and backend I/O.
/// </summary>
internal sealed class SgrAutomaton
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

    private readonly ISgrByteSink _sink;

    // Invalidated at BeginFrame (styles are not carried across frames because
    // non-frame writers may touch the terminal too).
    private PackedColor _fg;
    private PackedColor _bg;
    private StyleAttr _attrs;
    private PackedColor _underlineColor;
    private bool _styleKnown;

    // Pending-SGR accumulation: consecutive numeric params merge into one CSI.
    private int _sgrParams;
    private readonly int[] _sgrParamValues = new int[16];

    public SgrAutomaton(ISgrByteSink sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>Frame boundary: drop pending params and forget the current style.</summary>
    public void Reset()
    {
        _sgrParams = 0;
        _styleKnown = false;
        _underlineColor = default;
    }

    /// <summary>
    /// Emits the SGR delta between the current pen and <paramref name="target"/>.
    /// <paramref name="underlineColor"/> lives in <see cref="UnderlineColorTable"/>
    /// (a side-table, not a Cell field) and is folded into the same automaton:
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
                    AppendSgrParam(24);
                }

                if (targetUnderline)
                {
                    AppendUnderlineOn(targetStyle);
                }
            }

            FlushPending();

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
        FlushPending();
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
        FlushPending();
        _styleKnown = true;
        _fg = default;
        _bg = default;
        _attrs = StyleAttr.None;
        _underlineColor = default; // SGR 0 clears the underline color too
    }

    /// <summary>Emits the queued params as one CSI (no-op when nothing is queued).</summary>
    public void FlushPending()
    {
        if (_sgrParams == 0)
        {
            return;
        }

        // Colon params render as at most "NNN:NNN" (7 bytes); 8 is a safe cap.
        _sink.EnsureCapacity((_sgrParams * 8) + 2);
        for (int i = 0; i < _sgrParams; i++)
        {
            if (i > 0)
            {
                _sink.AppendByte((byte)';');
            }

            int value = _sgrParamValues[i];
            if ((value & ColonParamFlag) != 0)
            {
                _sink.AppendDigits((value >> 8) & 0xFF);
                _sink.AppendByte((byte)':');
                _sink.AppendDigits(value & 0xFF);
            }
            else
            {
                _sink.AppendDigits(value);
            }
        }

        _sink.AppendByte((byte)'m');
        _sgrParams = 0;
    }

    private void AppendAttrs(StyleAttr set)
    {
        QueueAttrs(set);
        FlushPending();
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
            _sink.EnsureCapacity(4);
            _sink.AppendByte(Esc);
            _sink.AppendByte((byte)'[');
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

    private void AppendColor(PackedColor color, bool isForeground)
    {
        if (color.IsDefault)
        {
            AppendSgrParam(isForeground ? 39 : 49);
            FlushPending();
            return;
        }

        if (!color.IsRgb)
        {
            var interned = (isForeground ? PaletteFg : PaletteBg)[color.Index];
            FlushPending();
            _sink.AppendBytes(interned);
            return;
        }

        var (r, g, b) = color.RgbChannels;
        FlushPending();
        _sink.EnsureCapacity(20);
        _sink.AppendByte(Esc);
        _sink.AppendByte((byte)'[');
        _sink.AppendDigits(isForeground ? 38 : 48);
        _sink.AppendAscii(";2;");
        _sink.AppendDigits(r);
        _sink.AppendByte((byte)';');
        _sink.AppendDigits(g);
        _sink.AppendByte((byte)';');
        _sink.AppendDigits(b);
        _sink.AppendByte((byte)'m');
    }

    // One-time cost: runs once per process in the static initializers above
    // (PaletteFg/PaletteBg), never per frame — the ToArray here builds the
    // interned palette, it is not a snapshot copy.
    private static byte[][] BuildPalette(int selector) =>
        Enumerable.Range(0, 256)
            .Select(i => Encoding.ASCII.GetBytes($"\x1B[{selector};5;{i}m"))
            .ToArray();
}
