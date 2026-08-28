using System.Text;

namespace Harbor.Tui.CellForge.Parsing;

/// <summary>Status of one incremental UTF-8 decode step.</summary>
public enum Utf8DecodeStatus : byte
{
    /// <summary>Byte consumed; more continuation bytes required.</summary>
    NeedMoreData = 0,

    /// <summary>Complete scalar value decoded into the output rune.</summary>
    Decoded,

    /// <summary>Invalid bytes consumed; caller should emit U+FFFD.</summary>
    ReplacementEmitted,

    /// <summary>Pending broken sequence flushed (emit U+FFFD) and the current
    /// byte was NOT consumed — feed it through the machine again.</summary>
    ReplacementPendingRetry,
}

/// <summary>
/// Incremental UTF-8 decoder holding up to 3 bytes of a split multibyte
/// sequence between chunks. Validates overlong forms, surrogates and
/// out-of-range scalars; every invalid sequence collapses to a single U+FFFD.
/// Zero-allocation: pure struct state, no spans retained.
/// </summary>
public struct Utf8IncrementalDecoder
{
    private byte _b0;
    private byte _b1;
    private byte _b2;
    private byte _consumed;
    private byte _expected;

    public readonly bool HasPending => _consumed != 0;

    public void Reset()
    {
        _consumed = 0;
        _expected = 0;
    }

    public Utf8DecodeStatus DecodeStep(byte b, out Rune rune)
    {
        rune = Rune.ReplacementChar;

        if (_consumed == 0)
        {
            if (b < 0x80)
            {
                rune = new Rune(b);
                return Utf8DecodeStatus.Decoded;
            }

            var expected = LeadSequenceLength(b);
            if (expected == 0)
            {
                // Stray continuation byte or invalid lead (C0/C1/F5..FF).
                return Utf8DecodeStatus.ReplacementEmitted;
            }

            _b0 = b;
            _consumed = 1;
            _expected = (byte)expected;
            return Utf8DecodeStatus.NeedMoreData;
        }

        if ((b & 0xC0) != 0x80)
        {
            // Current byte cannot continue the pending sequence: drop the
            // pending bytes as one replacement and make the caller reprocess b.
            Reset();
            return Utf8DecodeStatus.ReplacementPendingRetry;
        }

        switch (_consumed)
        {
            case 1: _b1 = b; break;
            case 2: _b2 = b; break;
        }
        _consumed++;

        if (_consumed < _expected)
        {
            return Utf8DecodeStatus.NeedMoreData;
        }

        // Structurally complete — assemble the scalar value; b is the final byte.
        uint codepoint = _expected switch
        {
            2 => (_b0 & 0x1Fu) << 6 | (b & 0x3Fu),
            3 => (_b0 & 0x0Fu) << 12 | (_b1 & 0x3Fu) << 6 | (b & 0x3Fu),
            _ => (_b0 & 0x07u) << 18 | (_b1 & 0x3Fu) << 12 | (_b2 & 0x3Fu) << 6 | (b & 0x3Fu),
        };
        var minLength = _expected switch { 2 => 0x80u, 3 => 0x800u, _ => 0x10000u };
        Reset();

        // Reject overlong encodings, surrogates and out-of-range scalars.
        if (codepoint < minLength || !IsValidScalar(codepoint))
        {
            return Utf8DecodeStatus.ReplacementEmitted;
        }

        rune = new Rune((int)codepoint);
        return Utf8DecodeStatus.Decoded;
    }

    private static bool IsValidScalar(uint codepoint) =>
        codepoint is >= 0 and <= 0xD7FF or >= 0xE000 and <= 0x10FFFF;

    private static int LeadSequenceLength(byte lead) => lead switch
    {
        >= 0xC2 and <= 0xDF => 2,
        >= 0xE0 and <= 0xEF => 3,
        >= 0xF0 and <= 0xF4 => 4,
        _ => 0,
    };
}
