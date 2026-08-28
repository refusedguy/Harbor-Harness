using System.Runtime.InteropServices;
using System.Text;

namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// One terminal cell — readonly struct of 16 bytes (celldiff §1.1):
/// codepoint + packed fg/bg + attribute flags + display width.
/// Equality is five integer compares (~1–2 ns), which is what makes the
/// fused full-scan diff affordable.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Cell : IEquatable<Cell>
{
    public const byte WSkip = 0;   // tail half of a wide rune — invisible cell
    public const byte Narrow = 1;
    public const byte Wide = 2;

    public readonly int Rune;
    public readonly uint Fg;
    public readonly uint Bg;
    public readonly ushort Flags;
    public readonly byte Width;

    private Cell(int rune, uint fg, uint bg, ushort flags, byte width)
    {
        Rune = rune;
        Fg = fg;
        Bg = bg;
        Flags = flags;
        Width = width;
    }

    /// <summary>Default blank: space, plain style, narrow.</summary>
    public static Cell Blank { get; } = new(' ', 0, 0, 0, Narrow);

    /// <summary>Invisible tail half that follows a wide lead cell.</summary>
    public static Cell WideTail { get; } = new(0, 0, 0, 0, WSkip);

    public static Cell From(Rune rune, in CellStyle style) => new(
        rune.Value,
        style.Fg.Value,
        style.Bg.Value,
        (ushort)style.Attrs,
        (byte)UnicodeWidth.Width(rune));

    /// <summary>Style view of this cell (colors + attributes).</summary>
    public CellStyle Style => new(
        PackedColor.FromRaw(Fg),
        PackedColor.FromRaw(Bg),
        (StyleAttr)Flags);

    /// <summary>True when this cell is a printable space with default styling.</summary>
    public bool IsBlankSpace => Width == Narrow && Rune == ' ' && Fg == 0 && Bg == 0 && Flags == 0;

    /// <summary>Wide lead whose tail got clobbered — diff must repaint both halves (§1.3).</summary>
    public static Cell BlankAt(byte width) => width switch
    {
        WSkip => WideTail,
        _ => new Cell(' ', 0, 0, 0, width),
    };

    public bool Equals(Cell other) =>
        Rune == other.Rune && Fg == other.Fg && Bg == other.Bg && Flags == other.Flags && Width == other.Width;

    public override bool Equals(object? obj) => obj is Cell other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Rune, Fg, Bg, Flags, Width);

    public static bool operator ==(Cell left, Cell right) => left.Equals(right);
    public static bool operator !=(Cell left, Cell right) => !left.Equals(right);

    public override string ToString() => Width == WSkip
        ? "<tail>"
        : $"'{(char)Rune}' {Width}w {Fg}/{Bg} [{Flags}]";

    /// <summary>Managed size guard for the 16-byte budget (celldiff §1.1).</summary>
    public static int SizeBytes => Marshal.SizeOf<Cell>();
}
