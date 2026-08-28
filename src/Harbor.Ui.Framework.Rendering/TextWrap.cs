using System.Buffers;
using System.Text;

namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// Display-width aware greedy wrapper (inline mode): fills each output line up
/// to the given cell width, preferring the last word boundary inside
/// the window; wide runes never split across lines; zero-width runes attach to
/// the preceding rune's line.
/// </summary>
public static class TextWrap
{
    /// <summary>Wraps <paramref name="text"/> appending produced lines to <paramref name="output"/>.</summary>
    public static void WrapTo(ReadOnlySpan<char> text, int width, List<string> output)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        var slice = text.TrimStart("\r\n");
        while (!slice.IsEmpty)
        {
            int take = MeasureFit(slice, width, out _);

            // Back off to the last word boundary only when the fit ends
            // mid-word (a clean boundary or an over-long hard segment stays).
            if (take < slice.Length && !char.IsWhiteSpace(slice[take]))
            {
                int lastSpace = slice[..take].LastIndexOf(' ');
                if (lastSpace > 0)
                {
                    take = lastSpace;
                }
            }

            var line = slice[..take].TrimEnd(' ');
            output.Add(line.ToString());
            slice = slice[take..].TrimStart(' ');
        }
    }

    /// <summary>Splits explicit newlines first, then wraps each logical line.</summary>
    public static void WrapDocument(ReadOnlySpan<char> text, int width, List<string> output)
    {
        var remainder = text;
        while (true)
        {
            int idx = remainder.IndexOf('\n');
            var linePart = idx < 0 ? remainder : remainder[..idx];
            if (linePart.IsEmpty)
            {
                output.Add(string.Empty);
            }
            else
            {
                WrapTo(linePart, width, output);
            }

            if (idx < 0)
            {
                break;
            }

            remainder = remainder[(idx + 1)..];
        }
    }

    /// <summary>
    /// Number of UTF-16 units that fit into <paramref name="width"/> cells
    /// without splitting a rune cluster; wide runes need both cells free.
    /// </summary>
    private static int MeasureFit(ReadOnlySpan<char> slice, int width, out int cellsUsed)
    {
        int consumed = 0;
        int cells = 0;
        var rest = slice;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int size) != OperationStatus.Done)
            {
                size = 1;
                cells += 1;
                consumed += 1;
                rest = rest[size..];
                continue;
            }

            int w = UnicodeWidth.Width(rune);
            if (cells + w > width)
            {
                break;
            }

            cells += w;
            consumed += size;
            rest = rest[size..];
        }

        // Guarantee progress for pathological inputs (zero-width-only prefixes).
        if (consumed == 0 && !slice.IsEmpty)
        {
            consumed = 1;
            cells = 1;
        }

        cellsUsed = cells;
        return consumed;
    }
}
