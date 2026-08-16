using Avalonia.Controls.Documents;
using Avalonia.Media;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdContainerInline = Markdig.Syntax.Inlines.ContainerInline;
using MdLiteralInline = Markdig.Syntax.Inlines.LiteralInline;
using MdEmphasisInline = Markdig.Syntax.Inlines.EmphasisInline;
using MdCodeInline = Markdig.Syntax.Inlines.CodeInline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using MdLineBreakInline = Markdig.Syntax.Inlines.LineBreakInline;
using AvalonInline = Avalonia.Controls.Documents.Inline;

namespace Harbor.App.Avalonia.Views.Controls.Markdown;
/// <summary>
///     Renders Markdig inlines into Avalonia <see cref="Inline" /> objects
///     (Run / LineBreak) that can be added to a TextBlock's Inlines
///     collection. Extracted from <c>MarkdownRenderer.axaml.cs</c>
///     (Task R31 god-object decomposition) so the block renderer doesn't
///     carry inline-emission logic.
/// </summary>
/// <remarks>
///     <para>
///         Supported inline types: literal text, emphasis (bold ** and
///         italic *), inline <c>code</c>, links, line breaks. Unhandled
///         types (DelimiterInline, HtmlInline) are silently skipped.
///     </para>
///     <para>
///         All brush / font lookups go through
///         <see cref="MarkdownResourceResolver" /> so theme-variant changes
///         are picked up automatically.
///     </para>
/// </remarks>
internal static class MarkdownInlineRenderer
{
    /// <summary>
    ///     Build a list of <see cref="Inline" /> objects from a Markdig
    ///     container. Returns an empty list for null containers.
    /// </summary>
    public static List<AvalonInline> BuildInlines(MdContainerInline? container)
    {
        var list = new List<AvalonInline>();
        if (container is null)
        {
            return list;
        }
        foreach (var inline in container)
        {
            EmitInline(inline, list);
        }
        return list;
    }

    /// <summary>
    ///     Emit a single Markdig inline (and its children, recursively)
    ///     into <paramref name="sink" />.
    /// </summary>
    public static void EmitInline(MdInline inline, List<AvalonInline> sink)
    {
        switch (inline)
        {
            case MdLiteralInline lit:
                sink.Add(new Run(lit.Content.ToString()));
                break;
            case MdEmphasisInline em:
            {
                var childRuns = new List<AvalonInline>();
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
                    FontFamily = MarkdownResourceResolver.TryFindFont("FontMono", FontFamily.Default),
                    FontSize = 12,
                    Background = MarkdownResourceResolver.TryFindBrush("BgSubtleBrush", Brushes.DarkGray),
                    Foreground = MarkdownResourceResolver.TryFindBrush("StateWarningBrush", Brushes.Orange)
                });
                break;
            case MdLinkInline link:
            {
                var labelRuns = new List<AvalonInline>();
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
                        run.Foreground = MarkdownResourceResolver.TryFindBrush("AccentHoverBrush", Brushes.SkyBlue);
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
                foreach (var child in ci)
                {
                    EmitInline(child, sink);
                }
                break;
        }
    }

    /// <summary>
    ///     Collect all <see cref="Inline" /> children of a container
    ///     (recursively) without applying emphasis styling. Used by
    ///     <see cref="EmitInline" /> to gather child runs before applying
    ///     bold/italic at the parent level.
    /// </summary>
    public static void CollectRuns(MdContainerInline container, List<AvalonInline> sink)
    {
        foreach (var inline in container)
        {
            CollectRunsFromInline(inline, sink);
        }
    }

    /// <summary>
    ///     Collect a single inline's runs (recursively for containers)
    ///     without applying emphasis styling.
    /// </summary>
    public static void CollectRunsFromInline(MdInline inline, List<AvalonInline> sink)
    {
        switch (inline)
        {
            case MdLiteralInline lit:
                sink.Add(new Run(lit.Content.ToString()));
                break;
            case MdCodeInline code:
                sink.Add(new Run(code.Content)
                {
                    FontFamily = MarkdownResourceResolver.TryFindFont("FontMono", FontFamily.Default),
                    FontSize = 12,
                    Background = MarkdownResourceResolver.TryFindStaticBrush("BgSubtleBrush"),
                    Foreground = MarkdownResourceResolver.TryFindStaticBrush("StateWarningBrush")
                });
                break;
            case MdContainerInline ci:
                CollectRuns(ci, sink);
                break;
        }
    }
}
