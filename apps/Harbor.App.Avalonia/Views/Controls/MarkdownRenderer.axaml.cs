using Avalonia;
using Avalonia.Controls;
using Harbor.App.Avalonia.Views.Controls.Markdown;
using Markdig;
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
///         <see cref="TextBlock" /> / <see cref="Border" /> / <see cref="StackPanel" />
///         directly.
///     </para>
///     <para>
///         <b>Streaming:</b> bind <see cref="Markdown" /> to the streaming
///         buffer. Every property change rebuilds the children. Markdig is
///         fast enough (&lt;1 ms for typical chat chunks) that we don't
///         need diff/incremental rendering.
///     </para>
///     <para>
///         <b>Decomposition (Task R31):</b> this control used to be a
///         487-line god-object mixing (1) Markdig pipeline setup, (2) block
///         rendering, (3) inline rendering, (4) text extraction, (5) brush
///         / font resource lookup. Those concerns now live in:
///         <list type="bullet">
///             <item><see cref="MarkdownBlockRenderer" /> — block-level rendering</item>
///             <item><see cref="MarkdownInlineRenderer" /> — inline emission</item>
///             <item><see cref="MarkdownTextExtractor" /> — text extraction</item>
///             <item><see cref="MarkdownResourceResolver" /> — brush / font lookup</item>
///         </list>
///         The control itself just owns the <see cref="Markdown" /> property,
///         the Markdig pipeline, and the Render() entry point that walks
///         the parsed document and adds the block renderer's output to
///         <see cref="RootPanel" />.
///     </para>
/// </remarks>
public sealed partial class MarkdownRenderer : UserControl
{
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownRenderer, string>(nameof(Markdown), string.Empty);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    static MarkdownRenderer()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownRenderer>((r, _) => r.Render());
    }

    public MarkdownRenderer()
    {
        InitializeComponent();
    }

    public string Markdown
    {
        get => this.GetValue(MarkdownProperty);
        set => this.SetValue(MarkdownProperty, value);
    }

    public void Render()
    {
        if (RootPanel is null)
        {
            return;
        }

        string src = Markdown ?? string.Empty;
        RootPanel.Children.Clear();

        if (src.Length == 0)
        {
            return;
        }

        var doc = Markdig.Markdown.Parse(src, Pipeline);
        foreach (var block in doc)
        {
            var ctrl = MarkdownBlockRenderer.RenderBlock(block);
            if (ctrl is not null)
            {
                RootPanel.Children.Add(ctrl);
            }
        }
    }
}
