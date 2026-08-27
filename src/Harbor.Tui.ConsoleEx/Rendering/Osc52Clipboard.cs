using System.Text;

namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
/// Copy-on-select transport (killer features §P6.4): OSC 52 escape sequences
/// write to the system clipboard through the terminal itself — no native
/// interop, works over SSH. Static formatters only; callers write the
/// returned string to their backend. Sequences are capped so a runaway
/// selection can't flood the stream.
/// </summary>
public static class Osc52Clipboard
{
    /// <summary>Maximum payload size (base64 chars) sent in one sequence.</summary>
    public const int MaxPayloadChars = 100_000;

    /// <summary>System clipboard ("c"); primary selection would be "p".</summary>
    public const string SystemClipboardSelector = "c";

    /// <summary>
    /// Encodes <paramref name="text" /> as an OSC 52 copy sequence
    /// («ESC ] 52 ; c ; &lt;base64&gt; BEL»). Text longer than
    /// <see cref="MaxPayloadChars" /> base64 chars is truncated on a char
    /// boundary before encoding; empty text yields the clear sequence.
    /// </summary>
    public static string Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EncodeSelection(SystemClipboardSelector, text);
    }

    /// <summary>Same as <see cref="Encode" /> with an explicit selection selector ("c"/"p").</summary>
    public static string EncodeSelection(string selector, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(text);
        if (selector.Contains(';') || selector.Contains('\u001B'))
        {
            throw new ArgumentException("Selector must not contain ';' or ESC.", nameof(selector));
        }

        if (text.Length == 0)
        {
            return Clear();
        }

        string payload = Base64Of(text);
        if (payload.Length > MaxPayloadChars)
        {
            int charBudget = (MaxPayloadChars / 4) * 3; // whole base64 quads → whole UTF-16 units
            if (char.IsLowSurrogate(text[charBudget]))
            {
                charBudget--; // never split a surrogate pair
            }

            payload = Base64Of(text[..charBudget]);
        }

        return $"\u001B]52;{selector};{payload}\u0007";
    }

    /// <summary>Clear-clipboard sequence — empty base64 payload.</summary>
    public const string ClearSequence = "\u001B]52;c;\u0007";

    public static string Clear() => ClearSequence;

    private static string Base64Of(string text)
    {
        int maxLen = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[] utf8 = new byte[maxLen];
        int written = Encoding.UTF8.GetBytes(text, utf8);
        return Convert.ToBase64String(utf8, 0, written);
    }
}
