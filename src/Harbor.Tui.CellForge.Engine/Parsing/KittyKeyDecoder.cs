using System.Text;
using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Parsing;

/// <summary>
/// Kitty keyboard protocol key decoder (design §2.3), extracted from
/// <see cref="EscapeSequenceParser"/> (#95, one extract per PR):
/// CSI unicode-key-code : shifted : base-layout ; modifiers : event-type u.
/// Modifier bits follow the CellForge design contract: shift=1, ctrl=2,
/// alt=4 (super/hyper/meta collapse to the Meta bit); modifier value is 1-based.
/// Pure and allocation-free: the only shared dependency is the CSI scalar
/// scanner (<see cref="EscapeSequenceParser.IntParamAt"/>); the ':' matrix
/// scanner below is kitty-exclusive and lives here.
/// </summary>
public static class KittyKeyDecoder
{
    /// <summary>Decodes kitty parameters into a key event. Returns false when
    /// the sequence carries no primary codepoint (caller counts it ignored).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> parameters, out KeyEvent keyEvent)
    {
        var primary = IntSubParamAt(parameters, 0, 0);
        if (primary < 0)
        {
            keyEvent = default;
            return false;
        }

        var shifted = IntSubParamAt(parameters, 0, 1);
        var text = IntSubParamAt(parameters, 2, 0);
        var modValue = Math.Max(1, EscapeSequenceParser.IntParamAt(parameters, 1));
        var eventTypeValue = IntSubParamAt(parameters, 1, 1);

        var bits = modValue - 1;
        var mods = KeyModifiers.None;
        if ((bits & 0x01) != 0)
        {
            mods |= KeyModifiers.Shift;
        }
        if ((bits & 0x02) != 0)
        {
            mods |= KeyModifiers.Ctrl;
        }
        if ((bits & 0x04) != 0)
        {
            mods |= KeyModifiers.Alt;
        }
        if ((bits & 0x08) != 0 || (bits & 0x10) != 0 || (bits & 0x20) != 0)
        {
            mods |= KeyModifiers.Meta; // super/hyper/meta collapse (design §2.3, sprint bits 1–4)
        }

        var eventType = eventTypeValue switch
        {
            2 => KeyEventType.Repeat,
            3 => KeyEventType.Release,
            _ => KeyEventType.Press,
        };

        KeyCode key;
        var character = default(Rune);
        uint codepoint = 0;
        switch (primary)
        {
            case 13:
                key = KeyCode.Enter;
                break;
            case 9:
                key = KeyCode.Tab;
                break;
            case 27:
                key = KeyCode.Escape;
                break;
            case 127:
                key = KeyCode.Backspace;
                break;
            case >= 32 and not 127 when primary is < 57344 or > 63743:
                // Printable (excluding the Unicode private-use area where
                // kitty parks its functional keys).
                var scalar = text > 0 ? text : shifted > 0 ? shifted : primary;
                if (IsValidScalar(scalar))
                {
                    key = KeyCode.Char;
                    character = new Rune(scalar);
                }
                else
                {
                    key = KeyCode.Unknown;
                    codepoint = (uint)primary;
                }
                break;
            default:
                // Functional/unmapped codepoints stay lossless.
                key = KeyCode.Unknown;
                codepoint = (uint)primary;
                break;
        }

        keyEvent = new KeyEvent(key, character, mods, eventType, isKittyEncoded: true, codepoint);
        return true;
    }

    private static bool IsValidScalar(int scalar) =>
        scalar <= 0xD7FF || scalar is >= 0xE000 and <= 0x10FFFF;

    /// <summary>Returns the sub-parameter at (group, sub) of the ';'/':'
    /// parameter matrix — e.g. kitty CSI unicode:shifted ; mods:event u —
    /// or −1 when absent.</summary>
    private static int IntSubParamAt(ReadOnlySpan<byte> parameters, int groupIndex, int subIndex)
    {
        var group = 0;
        var sub = 0;
        var value = 0;
        var digits = 0;
        foreach (var b in parameters)
        {
            if (b == (byte)';')
            {
                if (group == groupIndex && sub == subIndex)
                {
                    return digits > 0 ? value : -1;
                }
                group++;
                sub = 0;
                value = 0;
                digits = 0;
                continue;
            }
            if (b == (byte)':')
            {
                if (group == groupIndex && sub == subIndex)
                {
                    return digits > 0 ? value : -1;
                }
                if (group > groupIndex)
                {
                    break;
                }
                sub++;
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

        return group == groupIndex && sub == subIndex && digits > 0 ? value : -1;
    }
}
