// Issue #2375: adapters from a source string to the neutral document token
// stream. Markdown reuses the shipped StreamingMarkdownRenderer (CE-3 dialect)
// and CodeSyntaxTokenizer (fenced-code highlighting) rather than re-parsing;
// HTML is a deliberately small, self-contained subset for viewing generated
// docs. No registry, no plugin seam: the format is an enum chosen by the host.

using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Source → wrapped <see cref="DocumentLine"/> adapters for <see cref="DocumentView"/>.</summary>
public static class DocumentAdapters
{
    /// <summary>Adapts <paramref name="source"/> to wrapped lines for the chosen format.</summary>
    public static IReadOnlyList<DocumentLine> Adapt(string? source, DocumentFormat format, int width) => format switch
    {
        DocumentFormat.Markdown => AdaptMarkdown(source, width),
        DocumentFormat.Html => AdaptHtml(source, width),
        _ => AdaptPlain(source, width),
    };

    /// <summary>
    /// Markdown via the existing tokenizer: block/inline structure from
    /// <see cref="StreamingMarkdownRenderer.RenderRange"/>, fenced code bodies
    /// highlighted by <see cref="CodeSyntaxTokenizer"/>. Styles mirror the
    /// assistant markdown block so a document and a chat answer look the same.
    /// </summary>
    public static IReadOnlyList<DocumentLine> AdaptMarkdown(string? source, int width)
    {
        string text = source ?? string.Empty;
        int w = Math.Max(1, width);
        var mdLines = StreamingMarkdownRenderer.RenderRange(text, 0, text.Length, w);
        var code = CodeSyntaxTokenizer.Default.HighlightFenceBodies(mdLines);

        var lines = new List<DocumentLine>(mdLines.Count);
        for (int i = 0; i < mdLines.Count; i++)
        {
            if (code is not null && code.TryGetValue(i, out var spans))
            {
                var painted = new DocumentSpan[spans.Count];
                for (int s = 0; s < spans.Count; s++)
                {
                    painted[s] = new DocumentSpan(spans[s].Text, spans[s].Style);
                }

                lines.Add(new DocumentLine(painted));
                continue;
            }

            var md = mdLines[i];
            var runs = new DocumentSpan[md.Spans.Count];
            for (int s = 0; s < md.Spans.Count; s++)
            {
                var span = md.Spans[s];
                runs[s] = new DocumentSpan(span.Text, StyleFor(span.Style));
            }

            lines.Add(new DocumentLine(runs));
        }

        return lines.Count == 0 ? [DocumentLine.Empty] : lines;
    }

    /// <summary>Plain text: explicit newlines plus display-width word wrap.</summary>
    public static IReadOnlyList<DocumentLine> AdaptPlain(string? source, int width)
    {
        string text = source ?? string.Empty;
        int w = Math.Max(1, width);
        var lines = new List<DocumentLine>(4);
        int start = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', start);
            var segment = (nl < 0 ? text.AsSpan(start) : text.AsSpan(start, nl - start)).TrimEnd('\r');
            lines.AddRange(DocumentWrap.Wrap([new DocumentSpan(segment.ToString(), CellStyle.Plain)], w));
            if (nl < 0)
            {
                break;
            }

            start = nl + 1;
        }

        return lines.Count == 0 ? [DocumentLine.Empty] : lines;
    }

    /// <summary>
    /// Small HTML subset: h1-h6, p, div, blockquote, ul/ol/li, pre, br, hr and
    /// inline b/strong, i/em, u, code, a. Unknown tags and comments are dropped,
    /// the five XML entities plus nbsp are decoded. Script/style blocks are
    /// skipped with their content. No DOM, no regex, no external dependency.
    /// </summary>
    public static IReadOnlyList<DocumentLine> AdaptHtml(string? source, int width) =>
        new HtmlCollector(Math.Max(1, width)).Collect(source ?? string.Empty);

    /// <summary>Markdown style → cell style, byte-identical to the chat block's palette mapping.</summary>
    private static CellStyle StyleFor(MdStyle style) => style switch
    {
        MdStyle.Bold or MdStyle.Heading => new CellStyle(
            style == MdStyle.Heading ? PackedColor.Indexed(4) : default,
            attrs: StyleAttr.Bold),
        MdStyle.Italic => new CellStyle(attrs: StyleAttr.Italic),
        MdStyle.BoldItalic => new CellStyle(attrs: StyleAttr.Bold | StyleAttr.Italic),
        MdStyle.Code => new CellStyle(PackedColor.Indexed(3)),
        MdStyle.Fence => ChatPalette.Dim,
        MdStyle.Bullet => new CellStyle(PackedColor.Indexed(4)),
        _ => CellStyle.Plain,
    };

    private readonly struct Inline(bool bold, bool italic, bool underline, bool code)
    {
        public bool Bold { get; init; } = bold;
        public bool Italic { get; init; } = italic;
        public bool Underline { get; init; } = underline;
        public bool Code { get; init; } = code;
    }

    private sealed class HtmlCollector
    {
        private static readonly CellStyle HeadingStyle = new(PackedColor.Indexed(4), attrs: StyleAttr.Bold);
        private static readonly CellStyle CodeStyle = new(PackedColor.Indexed(3));
        private static readonly CellStyle QuoteStyle = new(ChatPalette.Dim.Fg, attrs: StyleAttr.Italic);

        private readonly int _width;
        private readonly List<DocumentLine> _lines = [];
        private readonly Stack<Inline> _inline = new();
        private List<DocumentSpan> _current = [];
        private CellStyle _blockStyle = CellStyle.Plain;
        private int _itemIndex;
        private bool _ordered;
        private bool _pre;
        private string? _skipTag;

        public HtmlCollector(int width) => _width = width;

        public IReadOnlyList<DocumentLine> Collect(string source)
        {
            int i = 0;
            while (i < source.Length)
            {
                if (source[i] == '<')
                {
                    int close = source.IndexOf('>', i + 1);
                    if (close >= 0)
                    {
                        HandleTag(source.AsSpan(i + 1, close - i - 1));
                        i = close + 1;
                        continue;
                    }
                }

                int next = source.IndexOf('<', i);
                int end = next < 0 ? source.Length : next;
                HandleText(source.AsSpan(i, end - i));
                i = end;
            }

            FlushLine();
            return _lines.Count == 0 ? [DocumentLine.Empty] : _lines;
        }

        private CellStyle CurrentStyle()
        {
            bool bold = false, italic = false, underline = false, code = false;
            foreach (var f in _inline)
            {
                bold |= f.Bold;
                italic |= f.Italic;
                underline |= f.Underline;
                code |= f.Code;
            }

            var baseStyle = _blockStyle;
            var attrs = baseStyle.Attrs;
            if (bold)
            {
                attrs |= StyleAttr.Bold;
            }

            if (italic)
            {
                attrs |= StyleAttr.Italic;
            }

            if (underline)
            {
                attrs |= StyleAttr.Underline;
            }

            var plainAttrs = attrs;
            if (code)
            {
                return new CellStyle(CodeStyle.Fg, baseStyle.Bg, plainAttrs);
            }

            return new CellStyle(baseStyle.Fg, baseStyle.Bg, plainAttrs);
        }

        private void HandleText(ReadOnlySpan<char> text)
        {
            if (_skipTag is not null || text.IsEmpty)
            {
                return;
            }

            if (_pre)
            {
                int start = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\n')
                    {
                        continue;
                    }

                    AppendPre(text.Slice(start, i - start));
                    FlushPreLine();
                    start = i + 1;
                }

                AppendPre(text[start..]);
                return;
            }

            int seg = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }

                AppendDecoded(text[seg..i]);
                FlushLine();
                seg = i + 1;
            }

            AppendDecoded(text[seg..]);
        }

        private void AppendDecoded(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty)
            {
                return;
            }

            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '&')
                {
                    int semi = text[(i + 1)..].IndexOf(';');
                    if (semi >= 0)
                    {
                        var entity = text.Slice(i + 1, semi);
                        string? decoded = DecodeEntity(entity);
                        if (decoded is not null)
                        {
                            sb.Append(decoded);
                            i += semi + 2;
                            continue;
                        }
                    }
                }

                sb.Append(text[i]);
                i++;
            }

            if (sb.Length > 0)
            {
                _current.Add(new DocumentSpan(sb.ToString(), CurrentStyle()));
            }
        }

        private static string? DecodeEntity(ReadOnlySpan<char> entity)
        {
            if (entity.SequenceEqual("amp"))
            {
                return "&";
            }

            if (entity.SequenceEqual("lt"))
            {
                return "<";
            }

            if (entity.SequenceEqual("gt"))
            {
                return ">";
            }

            if (entity.SequenceEqual("quot"))
            {
                return "\"";
            }

            if (entity.SequenceEqual("apos") || entity.SequenceEqual("#39"))
            {
                return "'";
            }

            if (entity.SequenceEqual("nbsp"))
            {
                return "\u00A0";
            }

            return null;
        }

        private void AppendPre(ReadOnlySpan<char> text)
        {
            if (!text.IsEmpty)
            {
                _current.Add(new DocumentSpan(text.ToString(), CurrentStyle()));
            }
        }

        private void HandleTag(ReadOnlySpan<char> raw)
        {
            var tag = raw.Trim();
            if (tag.IsEmpty || tag[0] == '!')
            {
                return; // comment / doctype / declaration
            }

            bool closing = tag[0] == '/';
            if (closing)
            {
                tag = tag[1..].TrimStart();
            }

            int end = 0;
            while (end < tag.Length && !char.IsWhiteSpace(tag[end]) && tag[end] != '/')
            {
                end++;
            }

            string name = tag[..end].ToString().ToLowerInvariant();

            if (_skipTag is not null)
            {
                if (closing && name == _skipTag)
                {
                    _skipTag = null;
                }

                return;
            }

            if (name is "script" or "style")
            {
                if (!closing)
                {
                    FlushLine();
                    _skipTag = name;
                }

                return;
            }

            if (!closing && name == "br")
            {
                FlushLine();
                return;
            }

            if (!closing && name == "hr")
            {
                FlushLine();
                _lines.Add(new DocumentLine([new DocumentSpan(new string('─', _width), ChatPalette.Dim)]));
                return;
            }

            switch (name)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    Block(closing, HeadingStyle);
                    break;
                case "p": case "div":
                    Block(closing, CellStyle.Plain);
                    break;
                case "blockquote":
                    Block(closing, QuoteStyle);
                    break;
                case "ul": case "ol":
                    FlushLine();
                    if (!closing)
                    {
                        _itemIndex = 0;
                        _ordered = name == "ol";
                    }

                    break;
                case "li":
                    if (!closing)
                    {
                        FlushLine();
                        _itemIndex++;
                        string bullet = _ordered ? _itemIndex + ". " : "- ";
                        _current.Add(new DocumentSpan(bullet, new CellStyle(PackedColor.Indexed(4))));
                    }
                    else
                    {
                        FlushLine();
                    }

                    break;
                case "pre":
                    if (!closing)
                    {
                        FlushLine();
                        _pre = true;
                        _blockStyle = CodeStyle;
                    }
                    else
                    {
                        FlushPreLine();
                        _pre = false;
                        _blockStyle = CellStyle.Plain;
                    }

                    break;
                case "b": case "strong":
                    ToggleInline(closing, f => f with { Bold = true });
                    break;
                case "i": case "em":
                    ToggleInline(closing, f => f with { Italic = true });
                    break;
                case "u": case "a":
                    ToggleInline(closing, f => f with { Underline = true });
                    break;
                case "code": case "kbd": case "tt":
                    ToggleInline(closing, f => f with { Code = true });
                    break;
                // table/tr/td/span/font and anything else are transparent:
                // span and font carry inline children, table rows break lines.
                case "tr":
                    FlushLine();
                    break;
                case "td": case "th":
                    if (closing)
                    {
                        _current.Add(new DocumentSpan("  ", CellStyle.Plain));
                    }

                    break;
                default:
                    break;
            }
        }

        private void Block(bool closing, CellStyle style)
        {
            FlushLine();
            _blockStyle = closing ? CellStyle.Plain : style;
        }

        private void ToggleInline(bool closing, Func<Inline, Inline> open)
        {
            if (closing)
            {
                if (_inline.Count > 0)
                {
                    _inline.Pop();
                }

                return;
            }

            var top = _inline.Count == 0 ? default : _inline.Peek();
            _inline.Push(open(top));
        }

        private void FlushLine()
        {
            if (_current.Count == 0)
            {
                return;
            }

            _lines.AddRange(DocumentWrap.Wrap(_current, _width));
            _current = [];
        }

        private void FlushPreLine()
        {
            if (_current.Count == 0)
            {
                return;
            }

            _lines.Add(new DocumentLine(_current.ToArray()));
            _current = [];
        }
    }
}
