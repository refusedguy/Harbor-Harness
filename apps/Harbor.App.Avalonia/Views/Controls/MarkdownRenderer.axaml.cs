using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using MdBlock = Markdig.Syntax.Block;
using MdCodeBlock = Markdig.Syntax.CodeBlock;
using MdFencedCodeBlock = Markdig.Syntax.FencedCodeBlock;
using MdHeadingBlock = Markdig.Syntax.HeadingBlock;
using MdParagraphBlock = Markdig.Syntax.ParagraphBlock;
using MdListBlock = Markdig.Syntax.ListBlock;
using MdListItemBlock = Markdig.Syntax.ListItemBlock;
using MdQuoteBlock = Markdig.Syntax.QuoteBlock;
using MdThematicBreakBlock = Markdig.Syntax.ThematicBreakBlock;
using MdLeafBlock = Markdig.Syntax.LeafBlock;
using MdContainerInline = Markdig.Syntax.Inlines.ContainerInline;
using MdLiteralInline = Markdig.Syntax.Inlines.LiteralInline;
using MdEmphasisInline = Markdig.Syntax.Inlines.EmphasisInline;
using MdCodeInline = Markdig.Syntax.Inlines.CodeInline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using MdLineBreakInline = Markdig.Syntax.Inlines.LineBreakInline;
using MdHtmlInline = Markdig.Syntax.Inlines.HtmlInline;
using MdDelimiterInline = Markdig.Syntax.Inlines.DelimiterInline;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Harbor.App.Avalonia.Views.Controls;

/// <summary>
///     Renders a markdown string into native Avalonia controls — ORCA
///     feature steal #1 (streaming markdown rendering).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why not HtmlLabel / WebView?</b> Harbor's Avalonia 12 build
///         doesn't bundle a markdown-aware rich-text control and we don't
///         want a WebView dependency (process overhead, IPC). Markdig is
///         already a package reference (used by Blazor UI's markdown
///         renderer) — we walk its syntax tree and emit Avalonia
///         <see cref="TextBlock"/> / <see cref="Border"/> / <see cref="StackPanel"/>
///         directly.
///     </para>
///     <para>
///         <b>Streaming:</b> bind <see cref="Markdown"/> to the streaming
///         buffer. Every property change rebuilds the children. Markdig is
///         fast enough (&lt;1 ms for typical chat chunks) that we don't
///         need diff/incremental rendering.
///     </para>
///     <para>
///         <b>Supported elements:</b> ATX headings (H1–H6), paragraphs,
///         bold/italic/strike inline, inline <c>code</c>, fenced code blocks
///         (delegated to <see cref="CodeBlock"/>), bullet &amp; numbered
///         lists, blockquotes, links, thematic breaks.
///     </para>
///     <para>
///         <b>Type aliases:</b> Markdig and Avalonia both define
///         <c>Inline</c>, <c>CodeBlock</c>, <c>Span</c>, etc. We use
///         <c>Md*</c> aliases for Markdig types at the top of this file
///         to disambiguate from the Avalonia controls without sprinkling
///         full-qualified names through the body.
///     </para>
/// </remarks>
public sealed partial class MarkdownRenderer : UserControl
{
    /// <summary>Styled property for the markdown source string.</summary>
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownRenderer, string>(nameof(Markdown), defaultValue: string.Empty);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>Construct the markdown renderer.</summary>
    public MarkdownRenderer()
    {
        InitializeComponent();
        // Re-render on every Markdown change. PropertyChanged fires
        // regardless of visual-tree attachment — ideal for streaming
        // buffers that update before the control is fully laid out.
        PropertyChanged += OnPropertyChangedHandler;
    }

    /// <summary>The markdown source string.</summary>
    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void OnPropertyChangedHandler(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MarkdownProperty)
        {
            Render();
        }
    }

    /// <summary>Force a re-render of the current markdown.</summary>
    public void Render()
    {
        if (RootPanel is null)
        {
            return;
        }

        var src = Markdown ?? string.Empty;
        RootPanel.Children.Clear();

        if (src.Length == 0)
        {
            return;
        }

        var doc = Markdig.Markdown.Parse(src, Pipeline);
        foreach (MdBlock block in doc)
        {
            var ctrl = RenderBlock(block);
            if (ctrl is not null)
            {
                RootPanel.Children.Add(ctrl);
            }
        }
    }

    private Control? RenderBlock(MdBlock block) => block switch
    {
        MdHeadingBlock h => RenderHeading(h),
        MdParagraphBlock p => RenderParagraph(p),
        MdFencedCodeBlock fc => RenderFencedCode(fc),
        MdCodeBlock c => RenderPlainCode(c),
        MdListBlock l => RenderList(l),
        MdQuoteBlock q => RenderQuote(q),
        MdThematicBreakBlock => RenderThematicBreak(),
        _ => null,
    };

    private Control RenderHeading(MdHeadingBlock h)
    {
        var text = ExtractInlineText(h.Inline);
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindBrush("TextBrush", Brushes.White),
        };
        switch (h.Level)
        {
            case 1:
                tb.FontSize = 22; tb.FontWeight = FontWeight.Bold;
                tb.Margin = new Thickness(0, 8, 0, 4);
                break;
            case 2:
                tb.FontSize = 18; tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 6, 0, 3);
                break;
            case 3:
                tb.FontSize = 16; tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 4, 0, 2);
                break;
            case 4:
                tb.FontSize = 14; tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 3, 0, 2);
                break;
            default:
                tb.FontSize = 13; tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 2, 0, 1);
                break;
        }
        return tb;
    }

    private Control RenderParagraph(MdParagraphBlock p)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 14,
            Foreground = TryFindBrush("TextBrush", Brushes.White),
            Margin = new Thickness(0, 1),
        };
        var inlines = BuildInlines(p.Inline);
        foreach (var run in inlines)
        {
            tb.Inlines?.Add(run);
        }
        return tb;
    }

    private Control RenderFencedCode(MdFencedCodeBlock fc)
    {
        var code = ExtractCodeText(fc);
        var lang = fc.Info ?? string.Empty;
        return new CodeBlock { Code = code, Language = lang };
    }

    private Control RenderPlainCode(MdCodeBlock c)
    {
        return new CodeBlock { Code = ExtractCodeText(c), Language = string.Empty };
    }

    private Control RenderList(MdListBlock l)
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
                Margin = new Thickness(0, 1),
            };

            var bullet = new TextBlock
            {
                FontFamily = TryFindFont("CodeFont", FontFamily.Default),
                FontSize = 13,
                Foreground = TryFindBrush("MochaPeach", Brushes.Orange),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 8, 0),
                Text = l.IsOrdered ? $"{orderedIndex}." : "•",
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

    private Control RenderQuote(MdQuoteBlock q)
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
            BorderBrush = TryFindBrush("MochaSurface2", Brushes.Gray),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 6, 0, 6),
            Margin = new Thickness(0, 2),
            Child = inner,
        };
    }

    private static Control RenderThematicBreak()
    {
        return new Border
        {
            Height = 1,
            Background = TryFindStaticBrush("MochaSurface1"),
            Margin = new Thickness(0, 6),
        };
    }

    private List<Inline> BuildInlines(MdContainerInline? container)
    {
        var list = new List<Inline>();
        if (container is null)
        {
            return list;
        }
        foreach (MdInline inline in container)
        {
            EmitInline(inline, list);
        }
        return list;
    }

    private void EmitInline(MdInline inline, List<Inline> sink)
    {
        switch (inline)
        {
            case MdLiteralInline lit:
                sink.Add(new Run(lit.Content.ToString()));
                break;
            case MdEmphasisInline em:
                {
                    // Collect child runs (literal text + nested code spans),
                    // apply bold (**) or italic (*) formatting, then emit.
                    var childRuns = new List<Inline>();
                    CollectRuns(em, childRuns);
                    bool isBold = em.DelimiterCount == 2;
                    foreach (var r in childRuns)
                    {
                        if (r is Run run)
                        {
                            if (isBold)
                            {
                                run.FontWeight = FontWeight.Bold;
                            }
                            else
                            {
                                run.FontStyle = FontStyle.Italic;
                            }
                        }
                    }
                    sink.AddRange(childRuns);
                    break;
                }
            case MdCodeInline code:
                sink.Add(new Run(code.Content)
                {
                    FontFamily = TryFindFont("CodeFont", FontFamily.Default),
                    FontSize = 12,
                    Background = TryFindBrush("MochaSurface0", Brushes.DarkGray),
                    Foreground = TryFindBrush("MochaPeach", Brushes.Orange),
                });
                break;
            case MdLinkInline link:
                {
                    var labelRuns = new List<Inline>();
                    if (link.FirstChild is { } first)
                    {
                        CollectRunsFromInline(first, labelRuns);
                    }
                    if (labelRuns.Count == 0)
                    {
                        labelRuns.Add(new Run(link.Url ?? "link"));
                    }
                    foreach (var r in labelRuns)
                    {
                        if (r is Run run)
                        {
                            run.Foreground = TryFindBrush("MochaSapphire", Brushes.SkyBlue);
                            run.TextDecorations = TextDecorations.Underline;
                        }
                    }
                    sink.AddRange(labelRuns);
                    break;
                }
            case MdLineBreakInline:
                sink.Add(new LineBreak());
                break;
            case MdContainerInline ci:
                foreach (MdInline child in ci)
                {
                    EmitInline(child, sink);
                }
                break;
            default:
                // Unhandled inline type (DelimiterInline, HtmlInline, …) —
                // silently skip rather than throw.
                break;
        }
    }

    private static void CollectRuns(MdContainerInline container, List<Inline> sink)
    {
        foreach (MdInline inline in container)
        {
            CollectRunsFromInline(inline, sink);
        }
    }

    private static void CollectRunsFromInline(MdInline inline, List<Inline> sink)
    {
        switch (inline)
        {
            case MdLiteralInline lit:
                sink.Add(new Run(lit.Content.ToString()));
                break;
            case MdCodeInline code:
                sink.Add(new Run(code.Content)
                {
                    FontFamily = TryFindFont("CodeFont", FontFamily.Default),
                    FontSize = 12,
                    Background = TryFindStaticBrush("MochaSurface0"),
                    Foreground = TryFindStaticBrush("MochaPeach"),
                });
                break;
            case MdContainerInline ci:
                CollectRuns(ci, sink);
                break;
        }
    }

    private static string ExtractInlineText(MdContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (MdInline inline in container)
        {
            AppendInlineText(inline, sb);
        }
        return sb.ToString();
    }

    private static void AppendInlineText(MdInline inline, StringBuilder sb)
    {
        switch (inline)
        {
            case MdLiteralInline lit:
                sb.Append(lit.Content);
                break;
            case MdLinkInline link:
                if (link.FirstChild is { } first)
                {
                    AppendInlineText(first, sb);
                }
                break;
            case MdCodeInline code:
                sb.Append(code.Content);
                break;
            case MdContainerInline ci:
                foreach (MdInline child in ci)
                {
                    AppendInlineText(child, sb);
                }
                break;
        }
    }

    private static string ExtractCodeText(MdLeafBlock block)
    {
        if (block.Lines.Lines is null)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        for (int i = 0; i < block.Lines.Lines.Length; i++)
        {
            var line = block.Lines.Lines[i];
            if (line.Slice.Text is null)
            {
                continue;
            }
            sb.AppendLine(line.Slice.ToString());
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static IBrush TryFindBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b)
        {
            return b;
        }
        return fallback;
    }

    private static IBrush TryFindStaticBrush(string key) => TryFindBrush(key, Brushes.Gray);

    private static FontFamily TryFindFont(string key, FontFamily fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is FontFamily f)
        {
            return f;
        }
        return fallback;
    }
}
