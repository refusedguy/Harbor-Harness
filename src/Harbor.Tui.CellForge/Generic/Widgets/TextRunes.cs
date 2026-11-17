using System.Buffers;
using System.Text;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Rune and display-cell helpers shared by the text-input primitive (#2362).
/// Every index in this family is a UTF-16 offset into the original string;
/// the helpers keep the cursor off the inside of a surrogate pair and count
/// wide runes as more than one cell.
/// </summary>
internal static class TextRunes
{
    /// <summary>Counts runes, treating a surrogate pair as one.</summary>
    internal static int Count(string s)
    {
        int n = 0;
        var rest = s.AsSpan();
        while (!rest.IsEmpty)
        {
            Rune.DecodeFromUtf16(rest, out _, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            n++;
            rest = rest[consumed..];
        }

        return n;
    }

    /// <summary>Returns the longest prefix holding at most <paramref name="count"/> runes.</summary>
    internal static string Take(string s, int count)
    {
        var rest = s.AsSpan();
        int taken = 0;
        int n = 0;
        while (!rest.IsEmpty && n < count)
        {
            Rune.DecodeFromUtf16(rest, out _, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            taken += consumed;
            n++;
            rest = rest[consumed..];
        }

        return s[..taken];
    }

    /// <summary>UTF-16 length of the rune starting at <paramref name="index"/>.</summary>
    internal static int LengthAt(string s, int index)
    {
        if ((uint)index >= (uint)s.Length)
        {
            return 0;
        }

        Rune.DecodeFromUtf16(s.AsSpan(index), out _, out int consumed);
        return consumed > 0 ? consumed : 1;
    }

    /// <summary>UTF-16 length of the rune ending at <paramref name="index"/>.</summary>
    internal static int LengthBefore(string s, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        if (index >= 2 && char.IsLowSurrogate(s[index - 1]) && char.IsHighSurrogate(s[index - 2]))
        {
            return 2;
        }

        return 1;
    }

    /// <summary>Display cells used by the runes before a UTF-16 offset.</summary>
    internal static int CellsBefore(ReadOnlySpan<char> s)
    {
        int cells = 0;
        var rest = s;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int consumed) != OperationStatus.Done)
            {
                consumed = 1;
                rune = new Rune('\uFFFD');
            }

            cells += Math.Max(1, UnicodeWidth.Width(rune));
            rest = rest[consumed..];
        }

        return cells;
    }

    /// <summary>UTF-16 offset where the accumulated display cells reach <paramref name="targetCells"/>.</summary>
    internal static int CharIndexForCells(string s, int targetCells)
    {
        int cells = 0;
        int i = 0;
        while (i < s.Length)
        {
            if (Rune.DecodeFromUtf16(s.AsSpan(i), out var rune, out int consumed) != OperationStatus.Done)
            {
                consumed = 1;
                rune = new Rune('\uFFFD');
            }

            int w = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells + w > targetCells)
            {
                break;
            }

            cells += w;
            i += consumed;
        }

        return i;
    }

    /// <summary>Paints the caret by reversing the cell it covers.</summary>
    internal static void PaintCursor(ScreenBuffer buffer, int x, int y, CellStyle baseStyle)
    {
        if ((uint)x >= (uint)buffer.Cols || (uint)y >= (uint)buffer.Rows)
        {
            return;
        }

        var style = new CellStyle(baseStyle.Fg, baseStyle.Bg, baseStyle.Attrs | StyleAttr.Reverse);
        int rune = buffer.Get(x, y).Rune;
        Rune r = rune == 0 || rune == ' ' ? new Rune('_') : new Rune(rune);
        buffer.SetRune(x, y, r, in style);
    }

    /// <summary>Number of whole runes before a UTF-16 offset.</summary>
    internal static int RuneIndexAt(string s, int charIndex)
    {
        int runes = 0;
        int i = 0;
        int target = Math.Clamp(charIndex, 0, s.Length);
        while (i < target)
        {
            Rune.DecodeFromUtf16(s.AsSpan(i), out _, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            i += consumed;
            runes++;
        }

        return runes;
    }
}
