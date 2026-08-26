using System.Globalization;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>Kind of one unified-diff line.</summary>
public enum DiffLineKind : byte
{
    Context,
    Add,
    Delete,
    HunkHeader,
    FileHeader,
}

/// <summary>Parsed diff row: kind, resolved line numbers (0 when n/a) and raw text.</summary>
public readonly record struct DiffLine(DiffLineKind Kind, int OldNo, int NewNo, string Text);

/// <summary>
/// Strict unified-diff reader (CE-3 scope): file headers, hunk headers,
/// ±/context rows. Anything that does not look like a unified diff yields an
/// empty list — callers skip instead of guessing (widgets §3.10, no
/// «Contains("Wrote ")» heuristics).
/// </summary>
public static class UnifiedDiffParser
{
    /// <summary>Cheap gate before parsing.</summary>
    public static bool LooksLikeDiff(ReadOnlySpan<char> text)
    {
        var t = text.TrimStart();
        return t.StartsWith("diff --git", StringComparison.Ordinal)
            || t.StartsWith("--- ", StringComparison.Ordinal)
            || t.StartsWith("@@ -", StringComparison.Ordinal);
    }

    public static IReadOnlyList<DiffLine> Parse(string diffText)
    {
        if (!LooksLikeDiff(diffText))
        {
            return [];
        }

        var lines = new List<DiffLine>(16);
        int oldNo = 0;
        int newNo = 0;

        var rest = diffText.AsSpan();
        while (!rest.IsEmpty)
        {
            int nl = rest.IndexOf('\n');
            var line = nl >= 0 ? rest[..nl] : rest;
            rest = nl >= 0 ? rest[(nl + 1)..] : default;
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (line.StartsWith("@@ -", StringComparison.Ordinal))
            {
                var (o, n, ok) = ParseHunk(line);
                if (!ok)
                {
                    continue;
                }

                oldNo = o;
                newNo = n;
                lines.Add(new DiffLine(DiffLineKind.HunkHeader, 0, 0, line.ToString()));
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("diff --git", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.FileHeader, 0, 0, line.ToString()));
                continue;
            }

            if (line.StartsWith('\\') || line.IsEmpty)
            {
                continue; // "\ No newline at end of file"
            }

            char sign = line[0];
            switch (sign)
            {
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Add, 0, ++newNo, line[1..].ToString()));
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Delete, ++oldNo, 0, line[1..].ToString()));
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, ++oldNo, ++newNo, line[1..].ToString()));
                    break;
            }
        }

        return lines;
    }

    private static (int OldStart, int NewStart, bool Ok) ParseHunk(ReadOnlySpan<char> header)
    {
        // Format: @@ -a[,b] +c[,d] @@ …
        int plus = header.IndexOf('+');
        if (plus < 0)
        {
            return (0, 0, false);
        }

        int secondAt = header.Slice(plus).IndexOf("@@");
        int tailEnd = secondAt >= 0 ? plus + secondAt : header.Length;

        var oldPart = header.Slice(4, Math.Max(0, plus - 5));
        var newPart = header.Slice(plus + 1, Math.Max(0, tailEnd - plus - 1));

        return (ParseLeadingInt(oldPart), ParseLeadingInt(newPart),
            int.TryParse(SpanSliceUntil(oldPart, ','), NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    private static ReadOnlySpan<char> SpanSliceUntil(ReadOnlySpan<char> span, char stop)
    {
        int i = span.IndexOf(stop);
        return i >= 0 ? span[..i] : span;
    }

    private static int ParseLeadingInt(ReadOnlySpan<char> span)
    {
        span = SpanSliceUntil(span.TrimStart(), ',');
        return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>
/// Diff chat block (widgets §3.10, simplified CE-3 build): right-aligned
/// gutter numbers + sign + per-kind color, hard-truncated at rect width. No
/// syntax overlay, no stitcher yet.
/// </summary>
public sealed class DiffBlock : IChatBlock
{
    private readonly string _diffText;
    private IReadOnlyList<DiffLine> _lines = [];
    private bool _parsed;

    public DiffBlock(string diffText, string? path = null)
    {
        _diffText = diffText ?? string.Empty;
        Path = path;
    }

    public string Kind => "diff";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 96 + (_diffText.Length * 2);

    public string? Path { get; }

    public IReadOnlyList<DiffLine> Lines
    {
        get
        {
            EnsureParsed();
            return _lines;
        }
    }

    public BlockMeasure Measure(int width)
    {
        EnsureParsed();
        return BlockMeasure.Exact(_lines.Count);
    }

    public int CheapEstimate(int width) => BlockMath.EstimateLines(_diffText, Math.Max(8, width));

    public void Paint(in BlockPaintContext ctx)
    {
        EnsureParsed();
        var buffer = ctx.Buffer;
        int rows = Math.Min(ctx.Rect.Height, _lines.Count);

        for (int i = 0; i < rows; i++)
        {
            var dl = _lines[i];
            int y = ctx.Rect.Y + i;

            buffer.SetText(ctx.Rect.X, y, Gutter(dl), ChatPalette.Dim);

            var bodyStyle = dl.Kind switch
            {
                DiffLineKind.Add => ChatPalette.ToolOk,
                DiffLineKind.Delete => ChatPalette.ToolError,
                DiffLineKind.HunkHeader => new CellStyle(PackedColor.Indexed(6)),
                DiffLineKind.FileHeader => new CellStyle(attrs: StyleAttr.Bold),
                _ => CellStyle.Plain,
            };

            int x = ctx.Rect.X + GutterWidth;
            int avail = Math.Max(0, ctx.Rect.Right - x);
            if (avail == 0)
            {
                continue;
            }

            if (dl.Kind is DiffLineKind.HunkHeader or DiffLineKind.FileHeader)
            {
                // Headers carry their own markers — no sign column.
                buffer.SetText(x, y, dl.Text.AsSpan(0, Math.Min(avail, dl.Text.Length)), bodyStyle);
                continue;
            }

            char sign = dl.Kind switch
            {
                DiffLineKind.Add => '+',
                DiffLineKind.Delete => '-',
                _ => ' ',
            };

            buffer.SetText(x, y, [sign], bodyStyle);
            if (avail > 1)
            {
                var body = dl.Text.AsSpan(0, Math.Min(avail - 1, dl.Text.Length));
                buffer.SetText(x + 1, y, body, bodyStyle);
            }
        }
    }

    public const int GutterWidth = 11; // "1234 5678  "

    internal static string Gutter(DiffLine dl) => dl.Kind switch
    {
        DiffLineKind.Add => $"{' ',4} {dl.NewNumberString()}  ",
        DiffLineKind.Delete => $"{dl.OldNumberString()} {' ',4}  ",
        DiffLineKind.Context => $"{dl.OldNumberString()} {dl.NewNumberString()}  ",
        _ => new string(' ', GutterWidth),
    };
    public string RawText() => _diffText;

    private void EnsureParsed()
    {
        if (!_parsed)
        {
            _lines = UnifiedDiffParser.Parse(_diffText);
            _parsed = true;
        }
    }
}

internal static class DiffNumberExtensions
{
    public static string OldNumberString(this DiffLine dl) =>
        dl.OldNo > 0 ? dl.OldNo.ToString(CultureInfo.InvariantCulture).PadLeft(4) : new string(' ', 4);

    public static string NewNumberString(this DiffLine dl) =>
        dl.NewNo > 0 ? dl.NewNo.ToString(CultureInfo.InvariantCulture).PadLeft(4) : new string(' ', 4);
}
