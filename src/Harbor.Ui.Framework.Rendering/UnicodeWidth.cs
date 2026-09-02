using System.Buffers;
using System.Text;

namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// Display-width lookup for terminal cells (perf-audit §3.2): two sorted
/// range tables — East-Asian Wide/Fullwidth → 2, combining/format marks → 0,
/// everything else → 1. Binary search ≈ 10 ns per rune; the tables are
/// compile-time constants (AOT-friendly, no runtime data files).
///
/// Known simplification: width is resolved per rune, not per grapheme
/// cluster. U+FE0F (VS16) and ZWJ sequences are zero-width no-ops here; a
/// cluster-aware pass can layer on top without changing this table.
/// </summary>
public static class UnicodeWidth
{
    private static (int Lo, int Hi)[] _wide = Merge(
    [
        (0x1100, 0x115F), // Hangul Jamo initial
        (0x2329, 0x232A),
        (0x2E80, 0x303E), // CJK radicals .. Kangxi supplements
        (0x3041, 0x33FF), // Hiragana .. CJK compatibility
        (0x3400, 0x4DBF), // CJK extension A
        (0x4E00, 0x9FFF), // CJK unified
        (0xA000, 0xA4CF), // Yi syllables/roots
        (0xA960, 0xA97F), // Hangul Jamo extended-A
        (0xAC00, 0xD7A3), // Hangul syllables + extended-B
        (0xF900, 0xFAFF), // CJK compatibility ideographs
        (0xFE10, 0xFE19), // vertical forms
        (0xFE30, 0xFE6B), // CJK compatibility forms
        (0xFF00, 0xFF60), // fullwidth forms
        (0xFFE0, 0xFFE6), // fullwidth signs
        (0x16FE0, 0x16FE4), // Tangut marks
        (0x17000, 0x187F7), // Tangut
        (0x18800, 0x18CD5), // Tangut components
        (0x1AFF0, 0x1AFFF), // Kana extended-B
        (0x1B000, 0x1B152), // Kana supplement + small kana extension
        (0x1B164, 0x1B167), // Nushu
        (0x1F004, 0x1F004), // mahjong red dragon
        (0x1F0CF, 0x1F0CF), // playing-card joker
        (0x1F18E, 0x1F18E), // AB button (blood type)
        (0x1F191, 0x1F19A), // squared CL..VS
        (0x1F200, 0x1F2FF), // enclosed ideographic supplement
        (0x1F300, 0x1F64F), // misc symbols/pictographs, emoticons, transport-map part 1
        (0x1F680, 0x1F6FC), // transport/map
        (0x1F900, 0x1FAFF), // supplemental symbols & pictographs + extended-A
        (0x20000, 0x2FFFD), // CJK extensions B..F
        (0x30000, 0x3FFFD), // extensions G..
    ]);

    private static (int Lo, int Hi)[] _zeroWidth = Merge(
    [
        (0x0300, 0x036F), // combining diacritical marks
        (0x0483, 0x0489),
        (0x0591, 0x05BD),
        (0x05BF, 0x05BF),
        (0x05C1, 0x05C2),
        (0x05C4, 0x05C5),
        (0x05C7, 0x05C7),
        (0x0610, 0x061A),
        (0x064B, 0x065F),
        (0x0670, 0x0670),
        (0x06D6, 0x06DC),
        (0x06DF, 0x06E4),
        (0x06E7, 0x06E8),
        (0x06EA, 0x06ED),
        (0x0711, 0x0711),
        (0x0730, 0x074A),
        (0x07A6, 0x07B0),
        (0x07EB, 0x07F3),
        (0x0816, 0x0819),
        (0x081B, 0x0823),
        (0x0825, 0x0827),
        (0x0829, 0x082D),
        (0x0859, 0x085B),
        (0x0900, 0x0902), // Devanagari signs
        (0x093A, 0x093A),
        (0x093C, 0x093C),
        (0x0941, 0x0948),
        (0x094D, 0x094D),
        (0x0951, 0x0957),
        (0x0962, 0x0963),
        (0x0E31, 0x0E31), // Thai
        (0x0E34, 0x0E3A),
        (0x0E47, 0x0E4E),
        (0x200B, 0x200F), // zero-width space..RLM
        (0x202A, 0x202E), // bidi overrides
        (0x2060, 0x2064), // word joiner etc.
        (0x20D0, 0x20F0), // combining marks for symbols
        (0xFE00, 0xFE0F), // variation selectors (incl. VS16)
        (0xFE20, 0xFE2F), // combining half marks
        (0xFEFF, 0xFEFF), // BOM/ZWNBSP
        (0x1AB0, 0x1AFF), // combining extended
        (0x1DC0, 0x1DFF), // combining extended supplemental
        (0xE0100, 0xE01EF), // variation selectors supplement
    ]);

    /// <summary>Display width in terminal cells: 0 (combining/format/control), 1 or 2.</summary>
    public static int Width(Rune r)
    {
        int v = r.Value;
        if (v < 0x20 || v == 0x7F)
        {
            return 0;
        }

        if (v < 0x300)
        {
            return 1;
        }

        return In(_zeroWidth, v) ? 0 : In(_wide, v) ? 2 : 1;
    }

    /// <summary>Sum of display widths of every rune in the span.</summary>
    public static int Width(ReadOnlySpan<char> text)
    {
        int total = 0;
        var slice = text;
        while (!slice.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(slice, out var rune, out int consumed) == OperationStatus.Done)
            {
                total += Width(rune);
                slice = slice[consumed..];
            }
            else
            {
                slice = slice[1..];
            }
        }

        return total;
    }

    private static (int Lo, int Hi)[] Merge((int Lo, int Hi)[] ranges)
    {
        Array.Sort(ranges);
        var merged = new List<(int Lo, int Hi)>(ranges.Length);
        foreach (var (lo, hi) in ranges)
        {
            if (merged.Count > 0 && lo <= merged[^1].Hi + 1)
            {
                var last = merged[^1];
                merged[^1] = (last.Lo, Math.Max(last.Hi, hi));
            }
            else
            {
                merged.Add((lo, hi));
            }
        }

        return [.. merged];
    }

    private static bool In((int Lo, int Hi)[] ranges, int v)
    {
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (v < ranges[mid].Lo)
            {
                hi = mid - 1;
            }
            else if (v > ranges[mid].Hi)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
