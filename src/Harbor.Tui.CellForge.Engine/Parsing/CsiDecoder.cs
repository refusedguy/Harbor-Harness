using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Parsing;

/// <summary>
/// CSI final-byte decoder (design §2–§3), extracted from
/// <see cref="EscapeSequenceParser"/> (#95, one extract per PR).
/// Verbatim move — the parser's <c>DecodeCsiFinal</c> family, unchanged apart
/// from threading the owning parser explicitly (instance state such as the
/// SGR-mouse press context stays owned by the parser: <c>ref</c> parameters
/// on the instance entry points, cf. <see cref="SgrMouseDecoder"/>).
/// The parser keeps thin forwarders so dispatch call sites are untouched;
/// caller migration and forwarder deletion are tracked in #1392.
/// The only shared dependencies are the CSI scalar scanners below;
/// kitty/SGR sub-decoders stay in <see cref="KittyKeyDecoder"/> /
/// <see cref="SgrMouseDecoder"/>.
/// </summary>
internal static class CsiDecoder
{
    /// <summary>Routes a complete CSI sequence to its decoder. Zone З.2 adds
    /// mouse M/m, zone З.3 adds paste ~ markers.</summary>
    internal static void DecodeCsiFinal(
        EscapeSequenceParser parser,
        byte finalByte,
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        ref MouseButton pressedButton,
        ref bool movedSincePress)
    {
        if (privatePrefix == (byte)'<')
        {
            // SGR mouse encoding (design §3.2): CSI < button ; col ; row M|m.
            if (finalByte is (byte)'M' or (byte)'m')
            {
                if (SgrMouseDecoder.TryDecode(
                    finalByte,
                    parameters,
                    pressedButton,
                    movedSincePress,
                    out var mouseEvent,
                    out var nextPressedButton,
                    out var nextMovedSincePress))
                {
                    pressedButton = nextPressedButton;
                    movedSincePress = nextMovedSincePress;
                    parser.Enqueue(InputEvent.FromMouse(mouseEvent));
                }
                else
                {
                    parser.IgnoredSequenceCount++;
                }
                return;
            }

            parser.IgnoredSequenceCount++;
            return;
        }

        if (privatePrefix != 0)
        {
            switch (finalByte)
            {
                case (byte)'u':
                    // Answer to our kitty probe CSI ? u → CSI ? flags u.
                    parser.EnqueueCapability(CapabilityEvent.KittyFlags((uint)Math.Max(0, FirstIntParam(parameters))));
                    return;
                case (byte)'y' when intermediates.Length == 1 && intermediates[0] == (byte)'$':
                    // DECRQM answer: CSI ? Ps ; Pv $ y.
                    parser.EnqueueCapability(CapabilityEvent.DecRqm(
                        Math.Max(0, FirstIntParam(parameters)),
                        Math.Max(0, IntParamAt(parameters, 1))));
                    return;
                case (byte)'c':
                    // Primary device attributes: CSI [ ? Ps ; … c.
                    parser.EnqueueCapability(CapabilityEvent.Da(Math.Max(0, FirstIntParam(parameters))));
                    return;
                default:
                    // Private-mode reports we did not request (h/l and friends).
                    parser.IgnoredSequenceCount++;
                    return;
            }
        }

        switch (finalByte)
        {
            case (byte)'A':
                EnqueueArrow(parser, KeyCode.Up, parameters);
                return;
            case (byte)'B':
                EnqueueArrow(parser, KeyCode.Down, parameters);
                return;
            case (byte)'C':
                EnqueueArrow(parser, KeyCode.Right, parameters);
                return;
            case (byte)'D':
                EnqueueArrow(parser, KeyCode.Left, parameters);
                return;
            case (byte)'H':
                EnqueueArrow(parser, KeyCode.Home, parameters);
                return;
            case (byte)'F':
                EnqueueArrow(parser, KeyCode.End, parameters);
                return;
            case (byte)'~':
                if (FirstIntParam(parameters) == 200)
                {
                    // Bracketed-paste open marker: CSI 200 ~ (§4.1).
                    parser.StartPaste();
                    return;
                }

                DecodeLegacyTilde(parser, parameters);
                return;
            case (byte)'u':
                if (KittyKeyDecoder.TryDecode(parameters, out var kittyKey))
                {
                    parser.Enqueue(InputEvent.FromKey(kittyKey));
                }
                else
                {
                    parser.IgnoredSequenceCount++;
                }
                return;
            case (byte)'R':
                // Cursor position report: CSI row ; col R (probe-routable).
                var row = IntParamAt(parameters, 0);
                var col = IntParamAt(parameters, 1);
                if (row > 0 && col > 0)
                {
                    parser.EnqueueCapability(CapabilityEvent.CursorPosition(row, col));
                    return;
                }
                parser.IgnoredSequenceCount++;
                return;
            default:
                parser.IgnoredSequenceCount++;
                return;
        }
    }

    internal static void EnqueueArrow(EscapeSequenceParser parser, KeyCode key, ReadOnlySpan<byte> parameters)
    {
        var mods = LegacyModifiers(parameters);
        parser.EnqueueSimple(key, mods);
    }

    internal static void DecodeLegacyTilde(EscapeSequenceParser parser, ReadOnlySpan<byte> parameters)
    {
        var code = FirstIntParam(parameters);
        var mods = LegacyModifiers(parameters);
        KeyCode key = code switch
        {
            1 => KeyCode.Home,
            2 => KeyCode.Insert,
            3 => KeyCode.Delete,
            4 => KeyCode.End,
            5 => KeyCode.PageUp,
            6 => KeyCode.PageDown,
            7 => KeyCode.Home,
            8 => KeyCode.End,
            11 => KeyCode.F1,
            12 => KeyCode.F2,
            13 => KeyCode.F3,
            14 => KeyCode.F4,
            15 => KeyCode.F5,
            17 => KeyCode.F6,
            18 => KeyCode.F7,
            19 => KeyCode.F8,
            20 => KeyCode.F9,
            21 => KeyCode.F10,
            23 => KeyCode.F11,
            24 => KeyCode.F12,
            _ => KeyCode.None,
        };

        if (key == KeyCode.None)
        {
            parser.IgnoredSequenceCount++;
            return;
        }

        parser.EnqueueSimple(key, mods);
    }

    /// <summary>
    /// Legacy CSI/SS3 modifier parameter (xterm encoding): SECOND parameter,
    /// value−1 with shift=bit0, alt=bit1, ctrl=bit2, meta=bit3. NOTE the
    /// different bit order versus kitty CSI-u (kitty: shift=1, ctrl=2, alt=4).
    /// </summary>
    internal static KeyModifiers LegacyModifiers(ReadOnlySpan<byte> parameters)
    {
        var bits = IntParamAt(parameters, 1) - 1;
        if (bits <= 0)
        {
            return KeyModifiers.None;
        }

        var mods = KeyModifiers.None;
        if ((bits & 0x01) != 0)
        {
            mods |= KeyModifiers.Shift;
        }
        if ((bits & 0x02) != 0)
        {
            mods |= KeyModifiers.Alt;
        }
        if ((bits & 0x04) != 0)
        {
            mods |= KeyModifiers.Ctrl;
        }
        if ((bits & 0x08) != 0)
        {
            mods |= KeyModifiers.Meta;
        }

        return mods;
    }

    // ── Param scanning helpers (scalar, zero-alloc) ───────────────────────

    internal static int FirstIntParam(ReadOnlySpan<byte> parameters) => IntParamAt(parameters, 0);

    /// <summary>Returns the <paramref name="index"/>-th ';'-separated parameter
    /// (sub-parameters ':' are ignored), or −1 when absent. Shared with
    /// <see cref="KittyKeyDecoder"/> (kitty modifier group).</summary>
    internal static int IntParamAt(ReadOnlySpan<byte> parameters, int index)
    {
        var group = 0;
        var value = 0;
        var digits = 0;
        foreach (var b in parameters)
        {
            if (b == (byte)';')
            {
                if (group == index && digits > 0)
                {
                    return value;
                }
                group++;
                value = 0;
                digits = 0;
                continue;
            }
            if (b == (byte)':')
            {
                if (group == index)
                {
                    // Target group's first sub-parameter ends here.
                    return digits > 0 ? value : -1;
                }

                // Sub-parameters of an EARLIER group are skipped transparently.
                value = 0;
                digits = 0;
                continue;
            }
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                checked
                {
                    value = value * 10 + (b - (byte)'0');
                }
                digits++;
            }
        }

        return group == index && digits > 0 ? value : -1;
    }
}
