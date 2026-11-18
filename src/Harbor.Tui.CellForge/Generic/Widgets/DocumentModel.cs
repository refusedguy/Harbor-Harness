// Issue #2375: neutral document token model. A document is a flat list of
// already-wrapped display lines; each line is a list of styled spans. The
// model carries no chat state, no plugin seam and no event subscription. The
// host feeds lines in and drives paging through DocumentView's methods.

using System.Buffers;
using System.Text;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Source format a <see cref="DocumentView"/> can adapt.</summary>
public enum DocumentFormat : byte
{
    /// <summary>Plain text with word wrap and explicit newlines.</summary>
    Plain = 0,

    /// <summary>Simplified CE-3 markdown, rendered by the existing markdown tokenizer.</summary>
    Markdown = 1,

    /// <summary>Small HTML subset (headings, paragraphs, emphasis, lists, code, breaks).</summary>
    Html = 2,
}

/// <summary>One styled run inside a document line. Style is already resolved to the cell vocabulary.</summary>
public readonly record struct DocumentSpan(string Text, CellStyle Style)
{
    /// <summary>An empty plain run. Keeps blank lines representable.</summary>
    public static DocumentSpan Empty => new(string.Empty, CellStyle.Plain);
}

/// <summary>One display line of a document: styled spans laid out left to right.</summary>
public sealed class DocumentLine
{
    /// <summary>A blank line.</summary>
    public static readonly DocumentLine Empty = new([DocumentSpan.Empty]);

    /// <summary>Creates a line from its spans.</summary>
    public DocumentLine(IReadOnlyList<DocumentSpan> spans) => Spans = spans;

    /// <summary>The styled runs on this line.</summary>
    public IReadOnlyList<DocumentSpan> Spans { get; }

    /// <summary>Display width of the line in cells (wide runes count two, combining zero).</summary>
    public int CellWidth
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Spans.Count; i++)
            {
                total += UnicodeWidth.Width(Spans[i].Text);
            }

            return total;
        }
    }
}

/// <summary>
/// Display-width aware greedy wrapper for styled document spans. Splits each
/// span into trailing-whitespace tokens, packs tokens up to <paramref name="width"/>
/// cells with a word-boundary preference, and hard-cuts a single over-long token
/// by rune width. Wide runes never split; zero-width runes attach to the current
/// piece. AOT-clean: no regex, no reflection, BCL only.
/// </summary>
public static class DocumentWrap
{
    /// <summary>Wraps <paramref name="spans"/> into display lines of at most <paramref name="width"/> cells.</summary>
    public static List<DocumentLine> Wrap(IReadOnlyList<DocumentSpan> spans, int width)
    {
        width = Math.Max(1, width);
        var output = new List<DocumentLine>(4);
        var current = new List<DocumentSpan>(4);
        int currentWidth = 0;

        for (int s = 0; s < spans.Count; s++)
        {
            var span = spans[s];
            string text = span.Text;
            int tokenStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!IsBreak(text[i]) || (i + 1 < text.Length && IsBreak(text[i + 1])))
                {
                    continue;
                }

                Emit(text[tokenStart..(i + 1)], span.Style);
                tokenStart = i + 1;
            }

            if (tokenStart < text.Length)
            {
                Emit(text[tokenStart..], span.Style);
            }
        }

        if (current.Count > 0 || output.Count == 0)
        {
            output.Add(MakeLine(current));
        }

        return output;

        void Emit(string token, CellStyle style)
        {
            if (token.Length == 0)
            {
                return;
            }

            int tw = UnicodeWidth.Width(token);
            if (currentWidth > 0 && currentWidth + tw > width)
            {
                output.Add(MakeLine(current));
                current = [];
                currentWidth = 0;
            }

            if (tw > width)
            {
                foreach (string piece in HardSplit(token, width))
                {
                    current.Add(new DocumentSpan(piece, style));
                    output.Add(MakeLine(current));
                    current = [];
                    currentWidth = 0;
                }

                return;
            }

            if (currentWidth == 0 && IsAllBreak(token))
            {
                return; // leading whitespace on a continuation line
            }

            current.Add(new DocumentSpan(token, style));
            currentWidth += tw;
        }
    }

    private static DocumentLine MakeLine(List<DocumentSpan> spans) =>
        spans.Count == 0 ? DocumentLine.Empty : new DocumentLine(spans.ToArray());

    private static bool IsBreak(char c) => char.IsWhiteSpace(c) && c != '\u00A0';

    private static bool IsAllBreak(string token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (!IsBreak(token[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> HardSplit(string token, int width)
    {
        var pieces = new List<string>(2);
        var piece = new StringBuilder(width + 2);
        int pieceWidth = 0;
        var rest = token.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int consumed) != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }

            int w = UnicodeWidth.Width(rune);
            if (w == 0)
            {
                piece.Append(rest[..consumed]);
                rest = rest[consumed..];
                continue;
            }

            if (pieceWidth > 0 && pieceWidth + w > width)
            {
                pieces.Add(piece.ToString());
                piece.Clear();
                pieceWidth = 0;
            }

            piece.Append(rest[..consumed]);
            pieceWidth += w;
            rest = rest[consumed..];
        }

        if (piece.Length > 0)
        {
            pieces.Add(piece.ToString());
        }

        return pieces;
    }
}
