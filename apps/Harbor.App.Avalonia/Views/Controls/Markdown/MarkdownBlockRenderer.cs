using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MdBlock = Markdig.Syntax.Block;
using MdCodeBlock = Markdig.Syntax.CodeBlock;
using MdFencedCodeBlock = Markdig.Syntax.FencedCodeBlock;
using MdHeadingBlock = Markdig.Syntax.HeadingBlock;
using MdParagraphBlock = Markdig.Syntax.ParagraphBlock;
using MdListBlock = Markdig.Syntax.ListBlock;
using MdListItemBlock = Markdig.Syntax.ListItemBlock;
using MdQuoteBlock = Markdig.Syntax.QuoteBlock;
using MdThematicBreakBlock = Markdig.Syntax.ThematicBreakBlock;

namespace Harbor.App.Avalonia.Views.Controls.Markdown;
/// <summary>
///     Renders Markdig block-level elements into Avalonia <see cref="Control" />s.
///     Extracted from <c>MarkdownRenderer.axaml.cs</c> (Task R31 god-object
///     decomposition) — the renderer UserControl now just calls
///     <see cref="RenderBlock" /> per top-level block and adds the result
///     to its children. All block-type-specific logic lives here.
/// </summary>
/// <remarks>
///     <para>
///         Supported block types: ATX headings (H1–H6), paragraphs,
///         fenced code blocks (delegated to <see cref="CodeBlock" />),
///         plain code blocks, bullet &amp; numbered lists, blockquotes,
///         thematic breaks. Unhandled block types return null and are
///         silently skipped by the caller.
///     </para>
///     <para>
///         All brush / font lookups go through
///         <see cref="MarkdownResourceResolver" /> so theme-variant changes
///         are picked up automatically.
///     </para>
/// </remarks>
internal static class MarkdownBlockRenderer
{
    /// <summary>
    ///     Render a single Markdig block to an Avalonia control. Returns
    ///     null for unsupported block types so the caller can skip them.
    /// </summary>
    public static Control? RenderBlock(MdBlock block) => block switch
    {
        MdHeadingBlock h => RenderHeading(h),
        MdParagraphBlock p => RenderParagraph(p),
        MdFencedCodeBlock fc => RenderFencedCode(fc),
        MdCodeBlock c => RenderPlainCode(c),
        MdListBlock l => RenderList(l),
        MdQuoteBlock q => RenderQuote(q),
        MdThematicBreakBlock => RenderThematicBreak(),
        _ => null
    };

    private static Control RenderHeading(MdHeadingBlock h)
    {
        string text = MarkdownTextExtractor.ExtractInlineText(h.Inline);
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MarkdownResourceResolver.TryFindBrush("TextBrush", Brushes.White)
        };
        switch (h.Level)
        {
            case 1:
                tb.FontSize = 22;
                tb.FontWeight = FontWeight.Bold;
                tb.Margin = new Thickness(0, 8, 0, 4);
                break;
            case 2:
                tb.FontSize = 18;
                tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 6, 0, 3);
                break;
            case 3:
                tb.FontSize = 16;
                tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 4, 0, 2);
                break;
            case 4:
                tb.FontSize = 14;
                tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 3, 0, 2);
                break;
            default:
                tb.FontSize = 13;
                tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 2, 0, 1);
                break;
        }
        return tb;
    }

    private static Control RenderParagraph(MdParagraphBlock p)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 14,
            Foreground = MarkdownResourceResolver.TryFindBrush("TextBrush", Brushes.White),
            Margin = new Thickness(0, 1)
        };
        var inlines = MarkdownInlineRenderer.BuildInlines(p.Inline);
        foreach (var run in inlines)
        {
            tb.Inlines?.Add(run);
        }
        return tb;
    }

    private static Control RenderFencedCode(MdFencedCodeBlock fc)
    {
        string code = MarkdownTextExtractor.ExtractCodeText(fc);
        string lang = fc.Info ?? string.Empty;
        return new CodeBlock { Code = code, Language = lang };
    }

    private static Control RenderPlainCode(MdCodeBlock c) => new CodeBlock { Code = MarkdownTextExtractor.ExtractCodeText(c), Language = string.Empty };

    private static Control RenderList(MdListBlock l)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        int orderedIndex = 1;
        foreach (var item in l)
        {
            if (item is not MdListItemBlock li)
            {
                continue;
            }

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(0, 1)
            };

            var bullet = new TextBlock
            {
                FontFamily = MarkdownResourceResolver.TryFindFont("FontMono", FontFamily.Default),
                FontSize = 13,
                Foreground = MarkdownResourceResolver.TryFindBrush("StateWarningBrush", Brushes.Orange),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 8, 0),
                Text = l.IsOrdered ? $"{orderedIndex}." : "\u2022"
            };
            row.Children.Add(bullet);
            Grid.SetColumn(bullet, 0);

            var content = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var sub in li)
            {
                var ctrl = RenderBlock(sub);
                if (ctrl is not null)
                {
                    content.Children.Add(ctrl);
                }
            }
            row.Children.Add(content);
            Grid.SetColumn(content, 1);
            panel.Children.Add(row);

            if (l.IsOrdered)
            {
                orderedIndex++;
            }
        }
        return panel;
    }

    private static Control RenderQuote(MdQuoteBlock q)
    {
        var inner = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        foreach (var sub in q)
        {
            var ctrl = RenderBlock(sub);
            if (ctrl is not null)
            {
                inner.Children.Add(ctrl);
            }
        }
        return new Border
        {
            BorderBrush = MarkdownResourceResolver.TryFindBrush("BgPanelBrush", Brushes.Gray),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 6, 0, 6),
            Margin = new Thickness(0, 2),
            Child = inner
        };
    }

    private static Control RenderThematicBreak()
    {
        return new Border
        {
            Height = 1,
            Background = MarkdownResourceResolver.TryFindStaticBrush("BgPanelElevatedBrush"),
            Margin = new Thickness(0, 6)
        };
    }
}
