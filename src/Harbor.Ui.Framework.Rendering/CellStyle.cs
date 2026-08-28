namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// Attribute bits of a cell style (celldiff §1.1). Low byte holds the eight
/// SGR attributes; bits 8..10 are reserved for underline styles.
/// </summary>
[Flags]
public enum StyleAttr : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Reverse = 1 << 5,
    Hidden = 1 << 6,
    Strike = 1 << 7,
}

/// <summary>
/// Packed terminal color (celldiff §1.1, adapted): <c>default</c> value (all
/// zero bits) = terminal default (emit 39/49); bit 31 = RGB24 in bits 23..0;
/// otherwise a 1-biased palette index 0..255 in bits 8..0 (the bias makes the
/// all-zero struct the default color — plain field initializers never need an
/// explicit factory call). One branch per emit — no class hierarchy of color types.
/// </summary>
public readonly struct PackedColor : IEquatable<PackedColor>
{
    private const uint RgbFlag = 1u << 31;
    private const uint RgbMask = 0x00FF_FFFF;

    public readonly uint Value;

    private PackedColor(uint value) => Value = value;

    /// <summary>Terminal-default foreground/background.</summary>
    public static PackedColor Default { get; } = default;

    /// <summary>Rebuilds a color from its packed representation.</summary>
    internal static PackedColor FromRaw(uint value) => new(value);

    /// <summary>256-color palette entry.</summary>
    public static PackedColor Indexed(byte index) => new((uint)index + 1);

    /// <summary>Direct truecolor.</summary>
    public static PackedColor Rgb(byte r, byte g, byte b) =>
        new(RgbFlag | ((uint)r << 16) | ((uint)g << 8) | b);

    /// <summary>All-zero struct means terminal-default color.</summary>
    public bool IsDefault => Value == 0;

    public bool IsRgb => (Value & RgbFlag) != 0;

    public byte Index => (byte)(((Value & 0x1FF) - 1) & 0xFF);

    /// <summary>Decomposes an RGB24 color into channel bytes.</summary>
    public (byte R, byte G, byte B) RgbChannels => (
        (byte)((Value >> 16) & 0xFF),
        (byte)((Value >> 8) & 0xFF),
        (byte)(Value & 0xFF));

    public bool Equals(PackedColor other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PackedColor other && Equals(other);
    public override int GetHashCode() => (int)Value;
    public static bool operator ==(PackedColor left, PackedColor right) => left.Equals(right);
    public static bool operator !=(PackedColor left, PackedColor right) => !left.Equals(right);
    public override string ToString() => IsDefault ? "default"
        : IsRgb ? $"rgb({RgbChannels.R},{RgbChannels.G},{RgbChannels.B})"
        : $"idx({Index})";
}

/// <summary>
/// Foreground/background/attribute triple shared by the ANSI writer
/// (phase CE-1) and the cell grid (phase CE-2). Equality is field-wise so the
/// SGR automaton can diff styles with plain integer compares.
/// </summary>
public readonly struct CellStyle : IEquatable<CellStyle>
{
    public readonly PackedColor Fg;
    public readonly PackedColor Bg;
    public readonly StyleAttr Attrs;

    public CellStyle(PackedColor fg = default, PackedColor bg = default, StyleAttr attrs = StyleAttr.None)
    {
        Fg = fg;
        Bg = bg;
        Attrs = attrs;
    }

    /// <summary>No colors, no attributes — the reset state.</summary>
    public static CellStyle Plain { get; } = default;

    public bool IsPlain => Fg.IsDefault && Bg.IsDefault && Attrs == StyleAttr.None;

    public bool Equals(CellStyle other) => Fg == other.Fg && Bg == other.Bg && Attrs == other.Attrs;
    public override bool Equals(object? obj) => obj is CellStyle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Fg, Bg, Attrs);
    public static bool operator ==(CellStyle left, CellStyle right) => left.Equals(right);
    public static bool operator !=(CellStyle left, CellStyle right) => !left.Equals(right);
}
