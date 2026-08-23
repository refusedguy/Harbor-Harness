using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;
using MDInline = Markdig.Syntax.Inlines.Inline;

namespace Harbor.App.Wpf.Services;
/// <summary>
///     Renders Markdig-parsed markdown into a WPF <see cref="FlowDocument" />
///     suitable for display in a <c>RichTextBox</c> or
///     <c>FlowDocumentReader</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a custom renderer:</b> Markdig ships only HTML / plain-text
///         renderers. WPF's FlowDocument is the natural rich-text target for
///         desktop apps and supports paragraphs, lists, code blocks, inline
///         emphasis, and hyperlinks natively. This renderer walks the Markdig
///         AST and emits the corresponding FlowDocument elements.
///     </para>
///     <para>
///         Compatible with Markdig 1.x. Thread-safety: a new
///         <see cref="MarkdownFlowDocumentRenderer" /> should be created per
///         render (the builder pipeline is reused via a static field — Markdig
///         pipelines are thread-safe for parsing).
///     </para>
/// </remarks>
public sealed class MarkdownFlowDocumentRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    ///     Render a markdown string into a new <see cref="FlowDocument" />.
    /// </summary>
    /// <param name="markdown">Markdown source text.</param>
    /// <returns>A populated <see cref="FlowDocument" />.</returns>
    public FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Inter, Segoe UI, sans-serif"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            Background = Brushes.Transparent,
            PagePadding = new Thickness(0)
        };

        if (string.IsNullOrEmpty(markdown)) return doc;

        var parsed = Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
        {
            var element = RenderBlock(block);
            if (element is null) continue;
            if (element is Paragraph p) doc.Blocks.Add(p);
            else if (element is Section s) doc.Blocks.Add(s);
            else if (element is List l) doc.Blocks.Add(l);
        }

        return doc;
    }

    private static Block? RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return RenderHeading(heading);
            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph);
            case ListBlock list:
                return RenderList(list);
            case FencedCodeBlock fenced:
                return RenderCodeBlock(ExtractText(fenced));
            case CodeBlock code:
                return RenderCodeBlock(ExtractText(code));
            case QuoteBlock quote:
                return RenderQuote(quote);
            case ThematicBreakBlock:
                return new Paragraph
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(0, 4, 0, 0)
                };
            default:
                return null;
        }
    }

    private static Block RenderHeading(HeadingBlock heading)
    {
        double size = heading.Level switch
        {
            1 => 22,
            2 => 18,
            3 => 16,
            _ => 14
        };
        var p = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC2, 0xD3, 0xFF)),
            Margin = new Thickness(0, 8, 0, 4)
        };
        if (heading.Inline is not null) AddInlines(p, heading.Inline);
        return p;
    }

    private static Block RenderParagraph(ParagraphBlock paragraph)
    {
        var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
        if (paragraph.Inline is not null) AddInlines(p, paragraph.Inline);
        return p;
    }

    private static Block RenderList(ListBlock list)
    {
        var wpfList = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(20, 0, 0, 0)
        };
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem) continue;
            var listItemPara = new Paragraph();
            foreach (var sub in listItem)
            {
                if (sub is ParagraphBlock p && p.Inline is not null)
                {
                    AddInlines(listItemPara, p.Inline);
                }
            }
            wpfList.ListItems.Add(new ListItem(listItemPara));
        }
        return wpfList;
    }

    private static Block RenderCodeBlock(string code)
    {
        string text = code.Replace("\r\n", "\n").TrimEnd('\n');
        var para = new Paragraph
        {
            FontFamily = new FontFamily("JetBrains Mono, Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 4)
        };
        para.Inlines.Add(text);
        return para;
    }

    private static Block RenderQuote(QuoteBlock quote)
    {
        var section = new Section
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 4)
        };
        foreach (var block in quote)
        {
            var rendered = RenderBlock(block);
            if (rendered is not null) section.Blocks.Add(rendered);
        }
        return section;
    }

    private static void AddInlines(Paragraph paragraph, ContainerInline container)
    {
        foreach (var inline in container)
        {
            AddInline(paragraph, inline);
        }
    }

    private static void AddInline(Paragraph paragraph, MDInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                paragraph.Inlines.Add(new Run(literal.Content.ToString()));
                break;
            case EmphasisInline emphasis:
            {
                var span = new Span();
                if (emphasis.DelimiterCount == 2) span.FontWeight = FontWeights.Bold;
                else span.FontStyle = FontStyles.Italic;
                foreach (var child in emphasis) span.Inlines.Add(InlineFromContainer(child));
                paragraph.Inlines.Add(span);
                break;
            }
            case CodeInline code:
            {
                var run = new Run(code.Content)
                {
                    FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
                    Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF))
                };
                paragraph.Inlines.Add(run);
                break;
            }
            case LinkInline link:
            {
                // Markdig 1.x LinkInline has no .Text field — fall back to
                // the label (which holds the visible text for inline links)
                // or iterate children to recover the rendered text.
                string? text = link.Label;
                if (string.IsNullOrEmpty(text) && link.FirstChild is not null)
                {
                    var sb = new StringBuilder();
                    for (var child = link.FirstChild; child is not null; child = child.NextSibling)
                    {
                        if (child is LiteralInline lit) sb.Append(lit.Content.ToString());
                        else if (child is CodeInline code) sb.Append(code.Content);
                        else sb.Append(child);
                    }
                    text = sb.ToString();
                }
                text = string.IsNullOrEmpty(text) ? link.Url ?? "(link)" : text;
                var hyperlink = new Hyperlink(new Run(text))
                {
                    NavigateUri = string.IsNullOrEmpty(link.Url) ? null : new Uri(link.Url, UriKind.RelativeOrAbsolute),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                    TextDecorations = TextDecorations.Underline
                };
                paragraph.Inlines.Add(hyperlink);
                break;
            }
            case LineBreakInline:
                paragraph.Inlines.Add(new LineBreak());
                break;
            case ContainerInline childContainer:
                foreach (var child in childContainer) AddInline(paragraph, child);
                break;
            default:
                paragraph.Inlines.Add(new Run(inline.ToString()));
                break;
        }
    }

    private static Inline InlineFromContainer(MDInline inline)
    {
        if (inline is LiteralInline lit) return new Run(lit.Content.ToString());
        if (inline is CodeInline code) return new Run(code.Content);
        var span = new Span();
        if (inline is ContainerInline container)
        {
            foreach (var child in container) span.Inlines.Add(InlineFromContainer(child));
        }
        else
        {
            span.Inlines.Add(new Run(inline.ToString()));
        }
        return span;
    }

    private static string ExtractText(CodeBlock container)
    {
        // Markdig 1.x stores the code text in the `Lines` field (a
        // StringLineGroup) on the base Block class. StringLineGroup.ToString()
        // returns the concatenated raw text of all lines.
        return container.Lines.ToString();
    }
}
