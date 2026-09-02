namespace Harbor.Ui.Framework.Rendering.Markdown;

/// <summary>Simplified markdown style tags (CE-3 scope: paragraphs/fences/ATX/lists + inline B/I/code).</summary>
public enum MdStyle : byte
{
    Normal = 0,
    Bold,
    Italic,
    BoldItalic,
    Code,

    /// <summary>Whole-line style for ATX headings.</summary>
    Heading,

    /// <summary>Fence marker lines («```» and «```lang»).</summary>
    Fence,

    /// <summary>List bullet prefix («- » / «1. »); item text stays Normal.</summary>
    Bullet,
}

/// <summary>A styled run of text inside one display line.</summary>
public readonly record struct MdSpan(string Text, MdStyle Style);

/// <summary>One wrapped display line: styled spans laid out left-to-right.</summary>
public sealed class MdLine
{
    public static readonly MdLine Empty = new([new MdSpan(string.Empty, MdStyle.Normal)]);

    public MdLine(IReadOnlyList<MdSpan> spans) => Spans = spans;

    public IReadOnlyList<MdSpan> Spans { get; }

    public int CellWidth
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Spans.Count; i++)
            {
                total += Rendering.UnicodeWidth.Width(Spans[i].Text);
            }

            return total;
        }
    }
}
