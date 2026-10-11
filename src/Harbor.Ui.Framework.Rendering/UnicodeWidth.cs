using System.Buffers;
using System.Text;

namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// Display-width lookup for terminal cells (perf-audit §3.2): two sorted
/// range tables — East-Asian Wide/Fullwidth → 2, combining/format marks → 0,
/// everything else → 1. Binary search ≈ 10 ns per rune; the tables are
/// compile-time constants (AOT-friendly, no runtime data files).
///
/// Data: UAX #11 EastAsianWidth.txt (W/F → 2) at Unicode 16.0, plus the
/// zero-width rules shared by glibc wcwidth and unicode-width (the crate
/// ratatui uses): Default_Ignorable/Prepend format controls → 0,
/// Grapheme_Extend (Mn/Me) → 0, Hangul V/T jamo → 0, C1 controls → 0.
/// Deliberate divergences from unicode-width 0.2 (terminal truth wins):
/// Emoji_Presentation symbols that are EAW-Narrow (©, ®, ™, arrows) stay 1 —
/// terminals render them in one cell; unassigned codepoints inside wide
/// ranges keep the wide superset (future-proof); U+17D8 Khmer Beyal stays 1
/// (reference width 3 needs a lead+2-tails cell model we don't have).
///
/// Known simplification: width is resolved per rune, not per grapheme
/// cluster. U+FE0F (VS16) and ZWJ sequences are zero-width no-ops here, so a
/// cluster like "❤️" measures 1 (not the terminal's 2) and ZWJ families
/// measure per-rune; regional-indicator pairs, keycaps and skin-tone
/// modifiers likewise sum per-rune. A cluster-aware pass can layer on top
/// (measure the grapheme, paint lead+tail) without changing this table —
/// but Cell stores one rune, so full cluster fidelity also needs the
/// emission path (DiffEngine/AnsiWriter) to carry the whole sequence.
/// Until then the per-rune no-op is the locally-correct choice: the grid
/// never reserves cells the flush cannot fill (see ENG9 audit, #281).
/// </summary>
public static class UnicodeWidth
{
    private static (int Lo, int Hi)[] _wide = Merge(
    [
        (0x1100, 0x115F), // Hangul Jamo initial
        (0x231A, 0x231B), // Watch .. +1
        (0x2329, 0x232A),
        (0x23E9, 0x23EC), // Black Right-Pointing Double Triangle .. +3
        (0x23F0, 0x23F0), // Alarm Clock
        (0x23F3, 0x23F3), // Hourglass With Flowing Sand
        (0x25FD, 0x25FE), // White Medium Small Square .. +1
        (0x2614, 0x2615), // Umbrella With Rain Drops .. +1
        (0x2630, 0x2637), // Trigram For Heaven .. +7
        (0x2648, 0x2653), // Aries .. +11
        (0x267F, 0x267F), // Wheelchair Symbol
        (0x268A, 0x268F), // Monogram For Yang .. +5
        (0x2693, 0x2693), // Anchor
        (0x26A1, 0x26A1), // High Voltage Sign
        (0x26AA, 0x26AB), // Medium White Circle .. +1
        (0x26BD, 0x26BE), // Soccer Ball .. +1
        (0x26C4, 0x26C5), // Snowman Without Snow .. +1
        (0x26CE, 0x26CE), // Ophiuchus
        (0x26D4, 0x26D4), // No Entry
        (0x26EA, 0x26EA), // Church
        (0x26F2, 0x26F3), // Fountain .. +1
        (0x26F5, 0x26F5), // Sailboat
        (0x26FA, 0x26FA), // Tent
        (0x26FD, 0x26FD), // Fuel Pump
        (0x2705, 0x2705), // White Heavy Check Mark
        (0x270A, 0x270B), // Raised Fist .. +1
        (0x2728, 0x2728), // Sparkles
        (0x274C, 0x274C), // Cross Mark
        (0x274E, 0x274E), // Negative Squared Cross Mark
        (0x2753, 0x2755), // Black Question Mark Ornament .. +2
        (0x2757, 0x2757), // Heavy Exclamation Mark Symbol
        (0x2795, 0x2797), // Heavy Plus Sign .. +2
        (0x27B0, 0x27B0), // Curly Loop
        (0x27BF, 0x27BF), // Double Curly Loop
        (0x2B1B, 0x2B1C), // Black Large Square .. +1
        (0x2B50, 0x2B50), // White Medium Star
        (0x2B55, 0x2B55), // Heavy Large Circle
        (0x2E80, 0x303E), // CJK radicals .. Kangxi supplements
        (0x3041, 0x3247), // Hiragana .. enclosed CJK (excl. ambiguous 3248..324F)
        (0x3250, 0x33FF), // Enclosed CJK .. CJK compatibility
        (0x3400, 0x4DBF), // CJK extension A
        (0x4DC0, 0x4DFF), // Hexagram For The Creative Heaven .. +63
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
        (0x16FF0, 0x16FF1), // Vietnamese Alternate Reading Mark Ca .. +1
        (0x17000, 0x187F7), // Tangut
        (0x18800, 0x18CD5), // Tangut components
        (0x18CFF, 0x18D08), // Khitan Small Script Character-18Cff .. +9
        (0x1AFF0, 0x1AFFF), // Kana extended-B
        (0x1B000, 0x1B152), // Kana supplement + small kana extension
        (0x1B155, 0x1B155), // Katakana Letter Small Ko
        (0x1B164, 0x1B167), // Nushu
        (0x1B170, 0x1B2FB), // Nushu Character-1B170 .. +395
        (0x1D300, 0x1D356), // Monogram For Earth .. +86
        (0x1D360, 0x1D376), // Counting Rod Unit Digit One .. +22
        (0x1F004, 0x1F004), // mahjong red dragon
        (0x1F0CF, 0x1F0CF), // playing-card joker
        (0x1F18E, 0x1F18E), // AB button (blood type)
        (0x1F191, 0x1F19A), // squared CL..VS
        (0x1F200, 0x1F2FF), // enclosed ideographic supplement
        (0x1F300, 0x1F64F), // misc symbols/pictographs, emoticons, transport-map part 1
        (0x1F680, 0x1F6FC), // transport/map
        (0x1F7E0, 0x1F7EB), // Large Orange Circle .. +11
        (0x1F7F0, 0x1F7F0), // Heavy Equals Sign
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
        (0x0600, 0x0605), // Arabic number signs (Prepend/ignorable)
        (0x0610, 0x061A),
        (0x064B, 0x065F),
        (0x0670, 0x0670),
        (0x06D6, 0x06DC),
        (0x06DF, 0x06E4),
        (0x06E7, 0x06E8),
        (0x06EA, 0x06ED),
        (0x070F, 0x070F), // Syriac abbreviation mark (Prepend)
        (0x0711, 0x0711),
        (0x0730, 0x074A),
        (0x07A6, 0x07B0),
        (0x07EB, 0x07F3),
        (0x07FD, 0x07FD), // Nko Dantayalan
        (0x0816, 0x0819),
        (0x081B, 0x0823),
        (0x0825, 0x0827),
        (0x0829, 0x082D),
        (0x0859, 0x085B),
        (0x0890, 0x0891), // Arabic Koranic marks (ignorable)
        (0x0897, 0x089F), // Arabic Pepet .. +8
        (0x08CA, 0x08FF), // Arabic Small High Farsi Yeh .. +53
        (0x0900, 0x0902), // Devanagari signs
        (0x093A, 0x093A),
        (0x093C, 0x093C),
        (0x0941, 0x0948),
        (0x094D, 0x094D),
        (0x0951, 0x0957),
        (0x0962, 0x0963),
        (0x0981, 0x0981), // Bengali Sign Candrabindu
        (0x09BC, 0x09BC), // Bengali Sign Nukta
        (0x09C1, 0x09C4), // Bengali Vowel Sign U .. +3
        (0x09CD, 0x09CD), // Bengali Sign Virama
        (0x09E2, 0x09E3), // Bengali Vowel Sign Vocalic L .. +1
        (0x09FE, 0x09FE), // Bengali Sandhi Mark
        (0x0A01, 0x0A02), // Gurmukhi Sign Adak Bindi .. +1
        (0x0A3C, 0x0A3C), // Gurmukhi Sign Nukta
        (0x0A41, 0x0A42), // Gurmukhi Vowel Sign U .. +1
        (0x0A47, 0x0A48), // Gurmukhi Vowel Sign Ee .. +1
        (0x0A4B, 0x0A4D), // Gurmukhi Vowel Sign Oo .. +2
        (0x0A51, 0x0A51), // Gurmukhi Sign Udaat
        (0x0A70, 0x0A71), // Gurmukhi Tippi .. +1
        (0x0A75, 0x0A75), // Gurmukhi Sign Yakash
        (0x0A81, 0x0A82), // Gujarati Sign Candrabindu .. +1
        (0x0ABC, 0x0ABC), // Gujarati Sign Nukta
        (0x0AC1, 0x0AC5), // Gujarati Vowel Sign U .. +4
        (0x0AC7, 0x0AC8), // Gujarati Vowel Sign E .. +1
        (0x0ACD, 0x0ACD), // Gujarati Sign Virama
        (0x0AE2, 0x0AE3), // Gujarati Vowel Sign Vocalic L .. +1
        (0x0AFA, 0x0AFF), // Gujarati Sign Sukun .. +5
        (0x0B01, 0x0B01), // Oriya Sign Candrabindu
        (0x0B3C, 0x0B3C), // Oriya Sign Nukta
        (0x0B3F, 0x0B3F), // Oriya Vowel Sign I
        (0x0B41, 0x0B44), // Oriya Vowel Sign U .. +3
        (0x0B4D, 0x0B4D), // Oriya Sign Virama
        (0x0B55, 0x0B56), // Oriya Sign Overline .. +1
        (0x0B62, 0x0B63), // Oriya Vowel Sign Vocalic L .. +1
        (0x0B82, 0x0B82), // Tamil Sign Anusvara
        (0x0BC0, 0x0BC0), // Tamil Vowel Sign Ii
        (0x0BCD, 0x0BCD), // Tamil Sign Virama
        (0x0C00, 0x0C00), // Telugu Sign Combining Candrabindu Above
        (0x0C04, 0x0C04), // Telugu Sign Combining Anusvara Above
        (0x0C3C, 0x0C3C), // Telugu Sign Nukta
        (0x0C3E, 0x0C40), // Telugu Vowel Sign Aa .. +2
        (0x0C46, 0x0C48), // Telugu Vowel Sign E .. +2
        (0x0C4A, 0x0C4D), // Telugu Vowel Sign O .. +3
        (0x0C55, 0x0C56), // Telugu Length Mark .. +1
        (0x0C62, 0x0C63), // Telugu Vowel Sign Vocalic L .. +1
        (0x0C81, 0x0C81), // Kannada Sign Candrabindu
        (0x0CBC, 0x0CBC), // Kannada Sign Nukta
        (0x0CBF, 0x0CBF), // Kannada Vowel Sign I
        (0x0CC6, 0x0CC6), // Kannada Vowel Sign E
        (0x0CCC, 0x0CCD), // Kannada Vowel Sign Au .. +1
        (0x0CE2, 0x0CE3), // Kannada Vowel Sign Vocalic L .. +1
        (0x0D00, 0x0D01), // Malayalam Sign Combining Anusvara Above .. +1
        (0x0D3B, 0x0D3C), // Malayalam Sign Vertical Bar Virama .. +1
        (0x0D41, 0x0D44), // Malayalam Vowel Sign U .. +3
        (0x0D4D, 0x0D4D), // Malayalam Sign Virama
        (0x0D62, 0x0D63), // Malayalam Vowel Sign Vocalic L .. +1
        (0x0D81, 0x0D81), // Sinhala Sign Candrabindu
        (0x0DCA, 0x0DCA), // Sinhala Sign Al-Lakuna
        (0x0DD2, 0x0DD4), // Sinhala Vowel Sign Ketti Is-Pilla .. +2
        (0x0DD6, 0x0DD6), // Sinhala Vowel Sign Diga Paa-Pilla
        (0x0E31, 0x0E31), // Thai
        (0x0E34, 0x0E3A),
        (0x0E47, 0x0E4E),
        (0x0EB1, 0x0EB1), // Lao Vowel Sign Mai Kan
        (0x0EB4, 0x0EBC), // Lao Vowel Sign I .. +8
        (0x0EC8, 0x0ECE), // Lao Tone Mai Ek .. +6
        (0x0F18, 0x0F19), // Tibetan Astrological Sign -Khyud Pa .. +1
        (0x0F35, 0x0F35), // Tibetan Mark Ngas Bzung Nyi Zla
        (0x0F37, 0x0F37), // Tibetan Mark Ngas Bzung Sgor Rtags
        (0x0F39, 0x0F39), // Tibetan Mark Tsa -Phru
        (0x0F71, 0x0F7E), // Tibetan Vowel Sign Aa .. +13
        (0x0F80, 0x0F84), // Tibetan Vowel Sign Reversed I .. +4
        (0x0F86, 0x0F87), // Tibetan Sign Lci Rtags .. +1
        (0x0F8D, 0x0F97), // Tibetan Subjoined Sign Lce Tsa Can .. +10
        (0x0F99, 0x0FBC), // Tibetan Subjoined Letter Nya .. +35
        (0x0FC6, 0x0FC6), // Tibetan Symbol Padma Gdan
        (0x102D, 0x1030), // Myanmar Vowel Sign I .. +3
        (0x1032, 0x1037), // Myanmar Vowel Sign Ai .. +5
        (0x1039, 0x103A), // Myanmar Sign Virama .. +1
        (0x103D, 0x103E), // Myanmar Consonant Sign Medial Wa .. +1
        (0x1058, 0x1059), // Myanmar Vowel Sign Vocalic L .. +1
        (0x105E, 0x1060), // Myanmar Consonant Sign Mon Medial Na .. +2
        (0x1071, 0x1074), // Myanmar Vowel Sign Geba Karen I .. +3
        (0x1082, 0x1082), // Myanmar Consonant Sign Shan Medial Wa
        (0x1085, 0x1086), // Myanmar Vowel Sign Shan E Above .. +1
        (0x108D, 0x108D), // Myanmar Sign Shan Council Emphatic Tone
        (0x109D, 0x109D), // Myanmar Vowel Sign Aiton Ai
        (0x1160, 0x11FF), // Hangul Jungseong/Jongseong (V/T jamo, wcwidth-zero)
        (0x135D, 0x135F), // Ethiopic Combining Gemination And Vowel Length Mark .. +2
        (0x1712, 0x1714), // Tagalog Vowel Sign I .. +2
        (0x1732, 0x1733), // Hanunoo Vowel Sign I .. +1
        (0x1752, 0x1753), // Buhid Vowel Sign I .. +1
        (0x1772, 0x1773), // Tagbanwa Vowel Sign I .. +1
        (0x17B4, 0x17B5), // Khmer Vowel Inherent Aq .. +1
        (0x17B7, 0x17BD), // Khmer Vowel Sign I .. +6
        (0x17C6, 0x17C6), // Khmer Sign Nikahit
        (0x17C9, 0x17D3), // Khmer Sign Muusikatoan .. +10
        (0x17DD, 0x17DD), // Khmer Sign Atthacan
        (0x180B, 0x180F), // Mongolian free variation selectors
        (0x1885, 0x1886), // Mongolian Letter Ali Gali Baluda .. +1
        (0x18A9, 0x18A9), // Mongolian Letter Ali Gali Dagalga
        (0x1920, 0x1922), // Limbu Vowel Sign A .. +2
        (0x1927, 0x1928), // Limbu Vowel Sign E .. +1
        (0x1932, 0x1932), // Limbu Small Letter Anusvara
        (0x1939, 0x193B), // Limbu Sign Mukphreng .. +2
        (0x1A17, 0x1A18), // Buginese Vowel Sign I .. +1
        (0x1A1B, 0x1A1B), // Buginese Vowel Sign Ae
        (0x1A56, 0x1A56), // Tai Tham Consonant Sign Medial La
        (0x1A58, 0x1A5E), // Tai Tham Sign Mai Kang Lai .. +6
        (0x1A60, 0x1A60), // Tai Tham Sign Sakot
        (0x1A62, 0x1A62), // Tai Tham Vowel Sign Mai Sat
        (0x1A65, 0x1A6C), // Tai Tham Vowel Sign I .. +7
        (0x1A73, 0x1A7C), // Tai Tham Vowel Sign Oa Above .. +9
        (0x1A7F, 0x1A7F), // Tai Tham Combining Cryptogrammic Dot
        (0x1AB0, 0x1AFF), // combining extended
        (0x1B00, 0x1B03), // Balinese Sign Ulu Ricem .. +3
        (0x1B34, 0x1B34), // Balinese Sign Rerekan
        (0x1B36, 0x1B3A), // Balinese Vowel Sign Ulu .. +4
        (0x1B3C, 0x1B3C), // Balinese Vowel Sign La Lenga
        (0x1B42, 0x1B42), // Balinese Vowel Sign Pepet
        (0x1B6B, 0x1B73), // Balinese Musical Symbol Combining Tegeh .. +8
        (0x1B80, 0x1B81), // Sundanese Sign Panyecek .. +1
        (0x1BA2, 0x1BA5), // Sundanese Consonant Sign Panyakra .. +3
        (0x1BA8, 0x1BA9), // Sundanese Vowel Sign Pamepet .. +1
        (0x1BAB, 0x1BAD), // Sundanese Sign Virama .. +2
        (0x1BE6, 0x1BE6), // Batak Sign Tompi
        (0x1BE8, 0x1BE9), // Batak Vowel Sign Pakpak E .. +1
        (0x1BED, 0x1BED), // Batak Vowel Sign Karo O
        (0x1BEF, 0x1BF1), // Batak Vowel Sign U For Simalungun Sa .. +2
        (0x1C2C, 0x1C33), // Lepcha Vowel Sign E .. +7
        (0x1C36, 0x1C37), // Lepcha Sign Ran .. +1
        (0x1CD0, 0x1CD2), // Vedic Tone Karshana .. +2
        (0x1CD4, 0x1CE0), // Vedic Sign Yajurvedic Midline Svarita .. +12
        (0x1CE2, 0x1CE8), // Vedic Sign Visarga Svarita .. +6
        (0x1CED, 0x1CED), // Vedic Sign Tiryak
        (0x1CF4, 0x1CF4), // Vedic Tone Candra Above
        (0x1CF8, 0x1CF9), // Vedic Tone Ring Above .. +1
        (0x1DC0, 0x1DFF), // combining extended supplemental
        (0x200B, 0x200F), // zero-width space..RLM
        (0x202A, 0x202E), // bidi overrides
        (0x2060, 0x2064), // word joiner etc.
        (0x2065, 0x206F), // Isolates (extends 2060..2064 above)
        (0x20D0, 0x20F0), // combining marks for symbols
        (0x2CEF, 0x2CF1), // Coptic Combining Ni Above .. +2
        (0x2D7F, 0x2D7F), // Tifinagh Consonant Joiner
        (0x2DE0, 0x2DFF), // Combining Cyrillic Letter Be .. +31
        (0x302A, 0x302D), // Ideographic Level Tone Mark .. +3
        (0x3099, 0x309A), // Combining Katakana-Hiragana Voiced Sound Mark .. +1
        (0xA66F, 0xA672), // Combining Cyrillic Vzmet .. +3
        (0xA674, 0xA67D), // Combining Cyrillic Letter Ukrainian Ie .. +9
        (0xA69E, 0xA69F), // Combining Cyrillic Letter Ef .. +1
        (0xA6F0, 0xA6F1), // Bamum Combining Mark Koqndon .. +1
        (0xA802, 0xA802), // Syloti Nagri Sign Dvisvara
        (0xA806, 0xA806), // Syloti Nagri Sign Hasanta
        (0xA80B, 0xA80B), // Syloti Nagri Sign Anusvara
        (0xA825, 0xA826), // Syloti Nagri Vowel Sign U .. +1
        (0xA82C, 0xA82C), // Syloti Nagri Sign Alternate Hasanta
        (0xA8C4, 0xA8C5), // Saurashtra Sign Virama .. +1
        (0xA8E0, 0xA8F1), // Combining Devanagari Digit Zero .. +17
        (0xA8FF, 0xA8FF), // Devanagari Vowel Sign Ay
        (0xA926, 0xA92D), // Kayah Li Vowel Ue .. +7
        (0xA947, 0xA951), // Rejang Vowel Sign I .. +10
        (0xA980, 0xA982), // Javanese Sign Panyangga .. +2
        (0xA9B3, 0xA9B3), // Javanese Sign Cecak Telu
        (0xA9B6, 0xA9B9), // Javanese Vowel Sign Wulu .. +3
        (0xA9BC, 0xA9BD), // Javanese Vowel Sign Pepet .. +1
        (0xA9E5, 0xA9E5), // Myanmar Sign Shan Saw
        (0xAA29, 0xAA2E), // Cham Vowel Sign Aa .. +5
        (0xAA31, 0xAA32), // Cham Vowel Sign Au .. +1
        (0xAA35, 0xAA36), // Cham Consonant Sign La .. +1
        (0xAA43, 0xAA43), // Cham Consonant Sign Final Ng
        (0xAA4C, 0xAA4C), // Cham Consonant Sign Final M
        (0xAA7C, 0xAA7C), // Myanmar Sign Tai Laing Tone-2
        (0xAAB0, 0xAAB0), // Tai Viet Mai Kang
        (0xAAB2, 0xAAB4), // Tai Viet Vowel I .. +2
        (0xAAB7, 0xAAB8), // Tai Viet Mai Khit .. +1
        (0xAABE, 0xAABF), // Tai Viet Vowel Am .. +1
        (0xAAC1, 0xAAC1), // Tai Viet Tone Mai Tho
        (0xAAEC, 0xAAED), // Meetei Mayek Vowel Sign Uu .. +1
        (0xAAF6, 0xAAF6), // Meetei Mayek Virama
        (0xABE5, 0xABE5), // Meetei Mayek Vowel Sign Anap
        (0xABE8, 0xABE8), // Meetei Mayek Vowel Sign Unap
        (0xABED, 0xABED), // Meetei Mayek Apun Iyek
        (0xD7B0, 0xD7FB), // Hangul Jamo Extended-B (V/T jamo)
        (0xFB1E, 0xFB1E), // Hebrew Point Judeo-Spanish Varika
        (0xFE00, 0xFE0F), // variation selectors (incl. VS16)
        (0xFE20, 0xFE2F), // combining half marks
        (0xFEFF, 0xFEFF), // BOM/ZWNBSP
        (0xFFF9, 0xFFFB), // Interlinear annotation anchors
        (0x101FD, 0x101FD), // Phaistos Disc Sign Combining Oblique Stroke
        (0x102E0, 0x102E0), // Coptic Epact Thousands Mark
        (0x10376, 0x1037A), // Combining Old Permic Letter An .. +4
        (0x10A01, 0x10A03), // Kharoshthi Vowel Sign I .. +2
        (0x10A05, 0x10A06), // Kharoshthi Vowel Sign E .. +1
        (0x10A0C, 0x10A0F), // Kharoshthi Vowel Length Mark .. +3
        (0x10A38, 0x10A3A), // Kharoshthi Sign Bar Above .. +2
        (0x10A3F, 0x10A3F), // Kharoshthi Virama
        (0x10AE5, 0x10AE6), // Manichaean Abbreviation Mark Above .. +1
        (0x10D24, 0x10D27), // Hanifi Rohingya Sign Harbahay .. +3
        (0x10D69, 0x10D6D), // Garay Vowel Sign E .. +4
        (0x10EAB, 0x10EAC), // Yezidi Combining Hamza Mark .. +1
        (0x10EFC, 0x10EFF), // Arabic Combining Alef Overlay .. +3
        (0x10F46, 0x10F50), // Sogdian Combining Dot Below .. +10
        (0x10F82, 0x10F85), // Old Uyghur Combining Dot Above .. +3
        (0x11001, 0x11001), // Brahmi Sign Anusvara
        (0x11038, 0x11046), // Brahmi Vowel Sign Aa .. +14
        (0x11070, 0x11070), // Brahmi Sign Old Tamil Virama
        (0x11073, 0x11074), // Brahmi Vowel Sign Old Tamil Short E .. +1
        (0x1107F, 0x11081), // Brahmi Number Joiner .. +2
        (0x110B3, 0x110B6), // Kaithi Vowel Sign U .. +3
        (0x110B9, 0x110BA), // Kaithi Sign Virama .. +1
        (0x110C2, 0x110C2), // Kaithi Vowel Sign Vocalic R
        (0x11100, 0x11102), // Chakma Sign Candrabindu .. +2
        (0x11127, 0x1112B), // Chakma Vowel Sign A .. +4
        (0x1112D, 0x11134), // Chakma Vowel Sign Ai .. +7
        (0x11173, 0x11173), // Mahajani Sign Nukta
        (0x11180, 0x11181), // Sharada Sign Candrabindu .. +1
        (0x111B6, 0x111BE), // Sharada Vowel Sign U .. +8
        (0x111C9, 0x111CC), // Sharada Sandhi Mark .. +3
        (0x111CF, 0x111CF), // Sharada Sign Inverted Candrabindu
        (0x1122F, 0x11231), // Khojki Vowel Sign U .. +2
        (0x11234, 0x11234), // Khojki Sign Anusvara
        (0x11236, 0x11237), // Khojki Sign Nukta .. +1
        (0x1123E, 0x1123E), // Khojki Sign Sukun
        (0x11241, 0x11241), // Khojki Vowel Sign Vocalic R
        (0x112DF, 0x112DF), // Khudawadi Sign Anusvara
        (0x112E3, 0x112EA), // Khudawadi Vowel Sign U .. +7
        (0x11300, 0x11301), // Grantha Sign Combining Anusvara Above .. +1
        (0x1133B, 0x1133C), // Combining Bindu Below .. +1
        (0x11340, 0x11340), // Grantha Vowel Sign Ii
        (0x11366, 0x1136C), // Combining Grantha Digit Zero .. +6
        (0x11370, 0x11374), // Combining Grantha Letter A .. +4
        (0x113BB, 0x113C0), // Tulu-Tigalari Vowel Sign U .. +5
        (0x113CE, 0x113CE), // Tulu-Tigalari Sign Virama
        (0x113D0, 0x113D0), // Tulu-Tigalari Conjoiner
        (0x113D2, 0x113D2), // Tulu-Tigalari Gemination Mark
        (0x113E1, 0x113E2), // Tulu-Tigalari Vedic Tone Svarita .. +1
        (0x11438, 0x1143F), // Newa Vowel Sign U .. +7
        (0x11442, 0x11444), // Newa Sign Virama .. +2
        (0x11446, 0x11446), // Newa Sign Nukta
        (0x1145E, 0x1145E), // Newa Sandhi Mark
        (0x114B3, 0x114B8), // Tirhuta Vowel Sign U .. +5
        (0x114BA, 0x114BA), // Tirhuta Vowel Sign Short E
        (0x114BF, 0x114C0), // Tirhuta Sign Candrabindu .. +1
        (0x114C2, 0x114C3), // Tirhuta Sign Virama .. +1
        (0x115B2, 0x115B5), // Siddham Vowel Sign U .. +3
        (0x115BC, 0x115BD), // Siddham Sign Candrabindu .. +1
        (0x115BF, 0x115C0), // Siddham Sign Virama .. +1
        (0x115DC, 0x115DD), // Siddham Vowel Sign Alternate U .. +1
        (0x11633, 0x1163A), // Modi Vowel Sign U .. +7
        (0x1163D, 0x1163D), // Modi Sign Anusvara
        (0x1163F, 0x11640), // Modi Sign Virama .. +1
        (0x116AB, 0x116AB), // Takri Sign Anusvara
        (0x116AD, 0x116AD), // Takri Vowel Sign Aa
        (0x116B0, 0x116B5), // Takri Vowel Sign U .. +5
        (0x116B7, 0x116B7), // Takri Sign Nukta
        (0x1171D, 0x1171D), // Ahom Consonant Sign Medial La
        (0x1171F, 0x1171F), // Ahom Consonant Sign Medial Ligating Ra
        (0x11722, 0x11725), // Ahom Vowel Sign I .. +3
        (0x11727, 0x1172B), // Ahom Vowel Sign Aw .. +4
        (0x1182F, 0x11837), // Dogra Vowel Sign U .. +8
        (0x11839, 0x1183A), // Dogra Sign Virama .. +1
        (0x1193B, 0x1193C), // Dives Akuru Sign Anusvara .. +1
        (0x1193E, 0x1193E), // Dives Akuru Virama
        (0x11943, 0x11943), // Dives Akuru Sign Nukta
        (0x119D4, 0x119D7), // Nandinagari Vowel Sign U .. +3
        (0x119DA, 0x119DB), // Nandinagari Vowel Sign E .. +1
        (0x119E0, 0x119E0), // Nandinagari Sign Virama
        (0x11A01, 0x11A0A), // Zanabazar Square Vowel Sign I .. +9
        (0x11A33, 0x11A38), // Zanabazar Square Final Consonant Mark .. +5
        (0x11A3B, 0x11A3E), // Zanabazar Square Cluster-Final Letter Ya .. +3
        (0x11A47, 0x11A47), // Zanabazar Square Subjoiner
        (0x11A51, 0x11A56), // Soyombo Vowel Sign I .. +5
        (0x11A59, 0x11A5B), // Soyombo Vowel Sign Vocalic R .. +2
        (0x11A8A, 0x11A96), // Soyombo Final Consonant Sign G .. +12
        (0x11A98, 0x11A99), // Soyombo Gemination Mark .. +1
        (0x11C30, 0x11C36), // Bhaiksuki Vowel Sign I .. +6
        (0x11C38, 0x11C3D), // Bhaiksuki Vowel Sign E .. +5
        (0x11C3F, 0x11C3F), // Bhaiksuki Sign Virama
        (0x11C92, 0x11CA7), // Marchen Subjoined Letter Ka .. +21
        (0x11CAA, 0x11CB0), // Marchen Subjoined Letter Ra .. +6
        (0x11CB2, 0x11CB3), // Marchen Vowel Sign U .. +1
        (0x11CB5, 0x11CB6), // Marchen Sign Anusvara .. +1
        (0x11D31, 0x11D36), // Masaram Gondi Vowel Sign Aa .. +5
        (0x11D3A, 0x11D3A), // Masaram Gondi Vowel Sign E
        (0x11D3C, 0x11D3D), // Masaram Gondi Vowel Sign Ai .. +1
        (0x11D3F, 0x11D45), // Masaram Gondi Vowel Sign Au .. +6
        (0x11D47, 0x11D47), // Masaram Gondi Ra-Kara
        (0x11D90, 0x11D91), // Gunjala Gondi Vowel Sign Ee .. +1
        (0x11D95, 0x11D95), // Gunjala Gondi Sign Anusvara
        (0x11D97, 0x11D97), // Gunjala Gondi Virama
        (0x11EF3, 0x11EF4), // Makasar Vowel Sign I .. +1
        (0x11F00, 0x11F01), // Kawi Sign Candrabindu .. +1
        (0x11F36, 0x11F3A), // Kawi Vowel Sign I .. +4
        (0x11F40, 0x11F40), // Kawi Vowel Sign Eu
        (0x11F42, 0x11F42), // Kawi Conjoiner
        (0x11F5A, 0x11F5A), // Kawi Sign Nukta
        (0x13440, 0x13440), // Egyptian Hieroglyph Mirror Horizontally
        (0x13447, 0x13455), // Egyptian Hieroglyph Modifier Damaged At Top Start .. +14
        (0x1611E, 0x16129), // Gurung Khema Vowel Sign Aa .. +11
        (0x1612D, 0x1612F), // Gurung Khema Sign Anusvara .. +2
        (0x16AF0, 0x16AF4), // Bassa Vah Combining High Tone .. +4
        (0x16B30, 0x16B36), // Pahawh Hmong Mark Cim Tub .. +6
        (0x16F4F, 0x16F4F), // Miao Sign Consonant Modifier Bar
        (0x16F8F, 0x16F92), // Miao Tone Right .. +3
        (0x16FE4, 0x16FE4), // Khitan Small Script Filler
        (0x1BC9D, 0x1BC9E), // Duployan Thick Letter Selector .. +1
        (0x1BCA0, 0x1BCA3), // Shorthand format controls
        (0x1CF00, 0x1CF2D), // Znamenny Combining Mark Gorazdo Nizko S Kryzhem On Left .. +45
        (0x1CF30, 0x1CF46), // Znamenny Combining Tonal Range Mark Mrachno .. +22
        (0x1D167, 0x1D169), // Musical Symbol Combining Tremolo-1 .. +2
        (0x1D173, 0x1D182), // Musical Symbol Begin Beam .. +15
        (0x1D185, 0x1D18B), // Musical Symbol Combining Doit .. +6
        (0x1D1AA, 0x1D1AD), // Musical Symbol Combining Down Bow .. +3
        (0x1D242, 0x1D244), // Combining Greek Musical Triseme .. +2
        (0x1DA00, 0x1DA36), // Signwriting Head Rim .. +54
        (0x1DA3B, 0x1DA6C), // Signwriting Mouth Closed Neutral .. +49
        (0x1DA75, 0x1DA75), // Signwriting Upper Body Tilting From Hip Joints
        (0x1DA84, 0x1DA84), // Signwriting Location Head Neck
        (0x1DA9B, 0x1DA9F), // Signwriting Fill Modifier-2 .. +4
        (0x1DAA1, 0x1DAAF), // Signwriting Rotation Modifier-2 .. +14
        (0x1E000, 0x1E006), // Combining Glagolitic Letter Azu .. +6
        (0x1E008, 0x1E018), // Combining Glagolitic Letter Zemlja .. +16
        (0x1E01B, 0x1E021), // Combining Glagolitic Letter Shta .. +6
        (0x1E023, 0x1E024), // Combining Glagolitic Letter Yu .. +1
        (0x1E026, 0x1E02A), // Combining Glagolitic Letter Yo .. +4
        (0x1E08F, 0x1E08F), // Combining Cyrillic Small Letter Byelorussian-Ukrainian I
        (0x1E130, 0x1E136), // Nyiakeng Puachue Hmong Tone-B .. +6
        (0x1E2AE, 0x1E2AE), // Toto Sign Rising Tone
        (0x1E2EC, 0x1E2EF), // Wancho Tone Tup .. +3
        (0x1E4EC, 0x1E4EF), // Nag Mundari Sign Muhor .. +3
        (0x1E5EE, 0x1E5EF), // Ol Onal Sign Mu .. +1
        (0x1E8D0, 0x1E8D6), // Mende Kikakui Combining Number Teens .. +6
        (0x1E944, 0x1E94A), // Adlam Alif Lengthener .. +6
        (0xE0000, 0xE007F), // Tag characters (Default_Ignorable)
        (0xE0100, 0xE01EF), // variation selectors supplement
    ]);

    /// <summary>Display width in terminal cells: 0 (combining/format/control), 1 or 2.</summary>
    public static int Width(Rune r)
    {
        int v = r.Value;
        if (v < 0x20 || v == 0x7F || (v >= 0x80 && v <= 0x9F))
        {
            return 0; // C0 + DEL + C1 controls never occupy a cell
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

    /// <summary>
    /// True when terminals disagree on how many columns <paramref name="symbol"/>
    /// occupies (R1 steal, epic #1155: ratatui <c>has_uncertain_width</c> port).
    /// The known case is emoji presentation sequences carrying VS16 (U+FE0F):
    /// width tables report 2, but some terminals draw 1. The diff cannot assume
    /// either, so uncertain output must clear reserved columns before painting
    /// and must never let cursor elision inherit the advance (see
    /// <c>AnsiWriter.WriteText</c>). VS16 itself is BMP, so a char scan is exact.
    /// </summary>
    public static bool HasUncertainWidth(ReadOnlySpan<char> symbol)
    {
        foreach (char c in symbol)
        {
            if (c == '\uFE0F')
            {
                return true;
            }
        }

        return false;
    }

    // ── Per-run width cache (ENG5, issue #276; per-thread since #487) ───
    //
    // Status rows and markdown lines re-measure the same run texts every
    // frame, and runs are immutable strings, so widths memoize exactly: a tiny
    // direct-mapped table (256 slots, hash-indexed, collision = evict, never
    // grows) keyed by reference-then-value equality. Hits allocate nothing,
    // misses measure once and store. Bounded by construction — at most 256
    // retained strings per measuring thread, no eviction bookkeeping.
    //
    // The table is [ThreadStatic] on purpose. It used to be process-global
    // behind a `lock`, with the O(L) rune decode INSIDE the critical section:
    // every width measurement serialised the in-process renderer, the IPC
    // client and any plugin renderer against each other, and
    // StatusBarLayout.Fit — which re-summed the whole row once per dropped
    // victim — re-took that lock O(n²) times per painted status frame. The
    // lock was strictly wider than the work it protected, and it was on a
    // per-frame path taken while tokens stream. A thread owns its slots
    // outright, so a lookup is a plain array read: no monitor, no contention,
    // no shared write. What is traded for it is one decode per distinct run
    // per thread instead of per process — a handful at startup, and 4 KB of
    // table per thread that ever measures text.

    private const int WidthCacheSize = 256;

    [ThreadStatic]
    private static WidthCacheEntry[]? t_widthCache;

    [ThreadStatic]
    private static bool t_trackLookups;

    [ThreadStatic]
    private static long t_widthLookups;

    private struct WidthCacheEntry
    {
        public string? Text;
        public int CellWidth;
    }

    /// <summary>
    /// Display width of a text run with per-run memoization. Same result as
    /// <c>Width(ReadOnlySpan)</c>; use for immutable run texts
    /// that are measured repeatedly (status segments, markdown spans).
    /// Lock-free since #487: the table belongs to the calling thread.
    /// </summary>
    public static int WidthCached(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (t_trackLookups)
        {
            t_widthLookups++;
        }

        WidthCacheEntry[] cache = t_widthCache ??= new WidthCacheEntry[WidthCacheSize];
        int index = (text.GetHashCode() & int.MaxValue) & (WidthCacheSize - 1);
        ref WidthCacheEntry slot = ref cache[index];
        if (slot.Text is not null
            && (ReferenceEquals(slot.Text, text) || slot.Text.Equals(text, StringComparison.Ordinal)))
        {
            return slot.CellWidth;
        }

        int cellWidth = Width(text.AsSpan());
        cache[index] = new WidthCacheEntry { Text = text, CellWidth = cellWidth };
        return cellWidth;
    }

    /// <summary>
    /// Starts counting <see cref="WidthCached"/> calls on the calling thread,
    /// discarding anything it had counted so far. Pair with
    /// <see cref="EndWidthLookupTracking"/>, which returns the number of calls
    /// made in between — that number needs no baseline arithmetic, so there is
    /// nothing here to subtract from it later.
    /// </summary>
    /// <remarks>
    /// Counting is opt-in so the render path does not pay for it: the only cost
    /// when tracking is off is one thread-static bool read, against a base
    /// pointer the method has already loaded for the cache table itself.
    /// Thread-local for a second reason — a counter shared between the TUnit
    /// workers would make the number depend on whatever else happened to be
    /// running, which is the same "wall clock in a parallel test host" trap as a
    /// stopwatch. What is tracked here is a property of the algorithm (#487: one
    /// width lookup per segment, whatever the outcome), so a test can assert it
    /// exactly on any machine.
    /// </remarks>
    public static void BeginWidthLookupTracking()
    {
        t_widthLookups = 0;
        t_trackLookups = true;
    }

    /// <summary>
    /// Stops counting on the calling thread and returns the number of
    /// <see cref="WidthCached"/> calls it made since
    /// <see cref="BeginWidthLookupTracking"/>.
    /// </summary>
    public static long EndWidthLookupTracking()
    {
        t_trackLookups = false;
        return t_widthLookups;
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
