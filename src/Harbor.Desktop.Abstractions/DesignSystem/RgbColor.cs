namespace Harbor.Desktop.Abstractions.DesignSystem;

/// <summary>
///     Cross-platform RGB color value. Each platform app converts this to its
///     own native color type (Avalonia <c>Avalonia.Media.Color</c>,
///     WPF <c>System.Windows.Media.Color</c>, MAUI <c>Microsoft.Maui.Graphics.Color</c>,
///     Blazor CSS string). Keeping the value framework-agnostic means
///     <c>Harbor.Desktop.DesignSystem</c> can stay pure C# with no UI deps.
/// </summary>
/// <param name="R">Red channel (0-255).</param>
/// <param name="G">Green channel (0-255).</param>
/// <param name="B">Blue channel (0-255).</param>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary>Convert to a CSS hex string (<c>#RRGGBB</c>).</summary>
    public string ToHex() => string.Create(7, this, static (span, color) =>
    {
        span[0] = '#';
        span[1] = ToHexDigit(color.R >> 4);
        span[2] = ToHexDigit(color.R & 0xF);
        span[3] = ToHexDigit(color.G >> 4);
        span[4] = ToHexDigit(color.G & 0xF);
        span[5] = ToHexDigit(color.B >> 4);
        span[6] = ToHexDigit(color.B & 0xF);
    });

    private static char ToHexDigit(int v) => (char)(v < 10 ? '0' + v : 'A' + v - 10);

    /// <summary>Parse a <c>#RRGGBB</c> or <c>#RGB</c> hex string.</summary>
    public static RgbColor Parse(string hex)
    {
        if (hex is null || hex.Length == 0)
            throw new ArgumentException("Hex string is null or empty.", nameof(hex));

        var span = hex.AsSpan();
        if (span[0] == '#') span = span[1..];

        return span.Length switch
        {
            3 => new RgbColor(
                (byte)(FromHex(span[0]) * 17),
                (byte)(FromHex(span[1]) * 17),
                (byte)(FromHex(span[2]) * 17)),
            6 => new RgbColor(
                (byte)((FromHex(span[0]) << 4) | FromHex(span[1])),
                (byte)((FromHex(span[2]) << 4) | FromHex(span[3])),
                (byte)((FromHex(span[4]) << 4) | FromHex(span[5]))),
            _ => throw new FormatException($"Hex color must be #RGB or #RRGGBB; got '{hex}'."),
        };
    }

    private static int FromHex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => throw new FormatException($"Invalid hex digit '{c}'."),
    };

    /// <summary>Implicitly convert a hex string to an <see cref="RgbColor"/>.</summary>
    public static implicit operator RgbColor(string hex) => Parse(hex);

    /// <summary>Implicitly convert an (int, int, int) tuple (e.g. <c>(0x1E, 0x1E, 0x2E)</c>) to an <see cref="RgbColor"/>.</summary>
    public static implicit operator RgbColor((int R, int G, int B) rgb)
        => new((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
}
