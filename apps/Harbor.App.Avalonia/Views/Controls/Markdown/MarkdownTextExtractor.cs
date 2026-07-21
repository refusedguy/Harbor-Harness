using System.Text;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdContainerInline = Markdig.Syntax.Inlines.ContainerInline;
using MdLiteralInline = Markdig.Syntax.Inlines.LiteralInline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using MdCodeInline = Markdig.Syntax.Inlines.CodeInline;
using MdLeafBlock = Markdig.Syntax.LeafBlock;

namespace Harbor.App.Avalonia.Views.Controls.Markdown;
/// <summary>
///     Stateless text-extraction helpers for the Markdown renderer.
///     Extracted from <c>MarkdownRenderer.axaml.cs</c> (Task R31
///     god-object decomposition) — these pure functions walk a Markdig
///     inline tree or a code block's source lines and produce plain
///     text. Used by <c>MarkdownBlockRenderer.RenderHeading</c> (which
///     needs the heading text as a plain string for the TextBlock) and
///     by <c>MarkdownInlineRenderer</c> (which needs it as a fallback
///     for link labels).
/// </summary>
internal static class MarkdownTextExtractor
{
    /// <summary>
    ///     Concatenate the literal text of every inline in a container.
    ///     Returns <see cref="string.Empty" /> for null containers.
    /// </summary>
    public static string ExtractInlineText(MdContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            AppendInlineText(inline, sb);
        }
        return sb.ToString();
    }

    /// <summary>
    ///     Recursively append the literal text of an inline (and its
    ///     children, for containers) to <paramref name="sb" />.
    /// </summary>
    public static void AppendInlineText(MdInline inline, StringBuilder sb)
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
                foreach (var child in ci)
                {
                    AppendInlineText(child, sb);
                }
                break;
        }
    }

    /// <summary>
    ///     Extract the source-text of a code block (fenced or indented).
    ///     Markdig exposes the raw lines via <c>block.Lines.Lines</c> —
    ///     we concatenate each non-null slice and trim trailing newlines
    ///     so the <c>CodeBlock</c> control receives clean text.
    /// </summary>
    public static string ExtractCodeText(MdLeafBlock block)
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
}
