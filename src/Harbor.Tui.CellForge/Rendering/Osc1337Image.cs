using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// OSC 1337 inline-image encoder (iTerm2 protocol family: iTerm2, WezTerm,
/// Konsole, mintty). Wire form: «ESC ] 1337 ; File=name=N;size=N;inline=1;
/// preserveAspectRatio=1:&lt;base64&gt; BEL» — the terminal decodes the payload
/// by content sniffing, so PNG and JPEG ride the same envelope.
///
/// kitty deliberately does NOT speak 1337 (see
/// <see cref="Graphics.KittyPngInline" /> for its APC path); the runtime
/// picks the encoder per <see cref="Capabilities.InlineImageProbe" /> and
/// callers fall back to the text card when no protocol matches.
///
/// Static formatter ONLY — nothing here touches Console (lesson from
/// AnsiTui's Ansi class); callers write the returned bytes themselves.
/// </summary>
public static class Osc1337Image
{
    /// <summary>Payload cap (raw bytes before base64): keeps one attachment
    /// from flooding the tty stream; oversized images fall back to the card.</summary>
    public const int MaxDataBytes = 8 * 1024 * 1024;

    private const char EscChar = '\u001B';
    private const char BelChar = '\u0007';

    /// <summary>
    /// Encodes an inline image for OSC 1337-capable terminals. Returns null
    /// when the payload is empty, oversize, or the name collapses to nothing
    /// — callers then keep the text description card.
    /// </summary>
    /// <param name="name">File name shown by the terminal (sanitized: ';',
    /// ESC, BEL and control chars are replaced so the key=value envelope
    /// cannot be broken out of).</param>
    /// <param name="data">Raw PNG/JPEG bytes — the terminal sniffs the format.</param>
    public static byte[]? Encode(string name, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > MaxDataBytes)
        {
            return null;
        }

        string safeName = SanitizeName(name);
        if (safeName.Length == 0)
        {
            return null;
        }

        string payload = Convert.ToBase64String(data);
        string header = $"{EscChar}]1337;File=name={safeName};size={data.Length};inline=1;preserveAspectRatio=1:";
        int total = Encoding.ASCII.GetByteCount(header) + Encoding.UTF8.GetMaxByteCount(payload.Length) + 1;
        var bytes = new byte[total];
        int len = Encoding.ASCII.GetBytes(header, bytes);
        len += Encoding.UTF8.GetBytes(payload, bytes.AsSpan(len));
        bytes[len++] = (byte)BelChar;
        return bytes[..len];
    }

    /// <summary>Replaces envelope-breaking bytes so a hostile file name can
    /// neither terminate the OSC string nor inject additional File keys.</summary>
    public static string SanitizeName(ReadOnlySpan<char> name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(IsUnsafe(c) ? '_' : c);
        }

        return sb.ToString();
    }

    private static bool IsUnsafe(char c) =>
        c is ';' or EscChar or BelChar or '"' or '\\'
        || char.IsControl(c);
}
