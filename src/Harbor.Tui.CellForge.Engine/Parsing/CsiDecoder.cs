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
