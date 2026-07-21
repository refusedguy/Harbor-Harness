using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
namespace Harbor.Desktop.Shared.Services;
/// <summary>
///     Converts Markdown to plain text using Markdig. Used by the command
///     palette to fuzzy-search chat messages and by the toast notifications
///     to render a one-line summary of a multi-line Markdown payload.
/// </summary>
public sealed class MarkdownToPlainTextService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>Convert <paramref name="markdown" /> to plain text (no HTML).</summary>
    /// <param name="markdown">Markdown source. If null or empty, returns empty string.</param>
    /// <returns>Plain-text rendering — headings, lists, code blocks all flattened to text.</returns>
    public string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        var doc = Markdown.Parse(markdown, Pipeline);
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteBlock(doc, writer);
        return writer.ToString().Trim();
    }

    /// <summary>Truncate <paramref name="markdown" /> to <paramref name="maxChars" /> of plain text.</summary>
    public string ToSummary(string markdown, int maxChars = 100)
    {
        string text = ToPlainText(markdown);
        if (text.Length <= maxChars) return text;
        return text[..(maxChars - 1)] + "…";
    }

    private static void WriteBlock(ContainerBlock block, TextWriter writer)
    {
        foreach (var child in block)
        {
            switch (child)
            {
                case LeafBlock leaf when leaf.Inline is not null:
                    WriteInlines(leaf.Inline, writer);
                    writer.Write('\n');
                    break;
                case LeafBlock leaf when leaf is CodeBlock code:
                    // Code blocks: write the raw lines as-is.
                    var slice = code.Lines;
                    for (int i = 0; i < slice.Count; i++)
                        writer.WriteLine(slice.Lines[i].ToString());
                    break;
                case ContainerBlock nested:
                    WriteBlock(nested, writer);
                    break;
            }
        }
    }

    private static void WriteInlines(ContainerInline inlines, TextWriter writer)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    writer.Write(literal.Content.ToString());
                    break;
                case LineBreakInline:
                    writer.Write('\n');
                    break;
                case LinkInline link:
                    if (link.FirstChild is not null)
                        WriteInlines((ContainerInline)link.FirstChild, writer);
                    break;
                case EmphasisInline emphasis:
                    if (emphasis.FirstChild is not null)
                        WriteInlines((ContainerInline)emphasis.FirstChild, writer);
                    break;
                case ContainerInline nested:
                    WriteInlines(nested, writer);
                    break;
                case CodeInline code:
                    writer.Write(code.Content);
                    break;
            }
        }
    }
}
