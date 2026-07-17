using Harbor.Tui.SpectreTui.Components;
using Spectre.Console;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Helpers;
/// <summary>
///     Builds the chat screen widget tree from the current <see cref="ChatState" /> /
///     <see cref="InputState" /> each frame, using real Spectre.TUI widgets
///     (ScrollViewWidget, BoxWidget, SpinnerWidget, HelpWidget, Layout).
///     Returns one widget per named <see cref="Layout" /> region; the screen
///     resolves the region rectangle and renders each widget into it.
/// </summary>
internal sealed class LayoutBuilder
{
    private readonly ChatState _chat;
    private readonly InputState _input;

    public LayoutBuilder(ChatState chat, InputState input)
    {
        _chat = chat;
        _input = input;
    }

    public string Status { get; set; } = "idle";
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public bool IsReadingInput { get; set; }
    public bool IsStreaming { get; set; }
    public string StreamBuffer { get; set; } = string.Empty;
    public string ThinkBuffer { get; set; } = string.Empty;
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public decimal Cost { get; set; }

    public Layout Layout { get; } = new Layout("Root").SplitRows(
        new Layout("Header").Size(1),
        new Layout("History"),
        new Layout("Status").Size(1),
        new Layout("Spinner").Size(1),
        new Layout("Input").Size(3),
        new Layout("Footer").Size(1));

    public IReadOnlyDictionary<string, IWidget> BuildWidgets()
    {
        var header = new Paragraph()
            .Style(new Style(Color.Cyan, null, Decoration.Bold))
            .Alignment(Justify.Left);
        string title = string.IsNullOrEmpty(Provider)
            ? "⚓ Harbor"
            : $"⚓ Harbor — {Escape(Provider)}/{Escape(Model)} ({Escape(Agent)})";
        header.Lines.Add(TextLine.FromMarkup($"[bold cyan]{Escape(title)}[/]"));

        var statusLine = new Paragraph()
            .Alignment(Justify.Left);
        statusLine.Lines.Add(TextLine.FromMarkup(
            $"[grey]{Escape(Provider)}/{Escape(Model)} | {Escape(Agent)} | {TokensIn}↑ {TokensOut}↓ | ${Cost:F4} | {Status}[/]"));

        var spinner = IsStreaming
            ? (IWidget)new Paragraph(TextLine.FromMarkup("[cyan]⏳ generating…[/]")).LeftAligned()
            : new SpinnerWidget { Kind = SpinnerKind.Dots };

        string inputText = string.IsNullOrEmpty(_input.Text) && IsReadingInput
            ? "[dim]type a message, or /help[/]"
            : Escape(_input.Text);
        var inputBox = new BoxWidget()
            .Border(Border.Rounded)
            .MarkupTitle("[green]>[/]")
            .Inner(new Paragraph(TextLine.FromMarkup(inputText)).Alignment(Justify.Left));

        var footer = Paragraph.FromMarkup(
                "[grey]q[/] quit  [grey]Ctrl+L[/] clear  [grey]Ctrl+C[/] abort  [grey]Tab[/] complete  [grey]↑↓[/] history")
            .Centered();

        return new Dictionary<string, IWidget>
        {
            ["Header"] = header,
            ["History"] = BuildHistory(),
            ["Status"] = statusLine,
            ["Spinner"] = spinner,
            ["Input"] = inputBox,
            ["Footer"] = footer
        };
    }

    private IWidget BuildHistory()
    {
        var lines = new List<TextLine>();

        void AppendRole(string role, string content)
        {
            var blocks = RoleLines(role, content);
            lines.AddRange(blocks);
            lines.Add(Blank());
        }

        foreach (var entry in _chat.Lines)
            AppendRole(entry.Role, entry.Content);

        if (IsStreaming)
        {
            if (!string.IsNullOrEmpty(ThinkBuffer))
                AppendRole("thinking", ThinkBuffer.Trim());
            if (!string.IsNullOrEmpty(StreamBuffer))
                AppendRole("assistant", StreamBuffer.Trim());
        }

        if (lines.Count == 0)
            lines.Add(TextLine.FromMarkup("[dim]no messages yet…[/]"));

        var text = new Text();
        text.Lines.AddRange(lines);
        return new ScrollViewWidget().Inner(text).VerticalScroll(ScrollMode.Auto);
    }

    private static TextLine Blank() => TextLine.FromMarkup("");

    private static Color RoleColor(string role) => role switch
    {
        "user" => Color.Green,
        "assistant" => Color.Aqua,
        "tool" => Color.Blue,
        "tool-result" => Color.Grey,
        "thinking" => Color.Grey,
        "system" => Color.Grey,
        "error" => Color.Red,
        _ => Color.White
    };

    private static string RolePrefix(string role) => role switch
    {
        "user" => "> ",
        "assistant" => ": ",
        "tool" => "→ ",
        "tool-result" => "  ",
        "thinking" => "💭 ",
        "system" => "• ",
        "error" => "✗ ",
        _ => "  "
    };

    private static IEnumerable<TextLine> RoleLines(string role, string content)
    {
        var color = RoleColor(role);
        string prefix = RolePrefix(role);
        string body = (content ?? string.Empty).Replace("\\n", "\n");
        string[] segments = body.Split('\n');
        var result = new List<TextLine>();
        for (int i = 0; i < segments.Length; i++)
        {
            var line = new TextLine();
            line.Spans.Add(new TextSpan(i == 0 ? prefix : "  ", new Style(color)));
            var parsed = ParseMarkup(segments[i], color);
            line.Spans.AddRange(parsed.Spans);
            result.Add(line);
        }
        return result;
    }

    private static TextLine ParseMarkup(string text, Color fallback)
    {
        var parsed = TextLine.FromMarkup(text ?? string.Empty);
        var line = new TextLine();
        foreach (var span in parsed.Spans)
        {
            var fg = span.Style is null || span.Style.Value.Foreground == Color.Default
                ? fallback
                : span.Style.Value.Foreground;
            line.Spans.Add(new TextSpan(span.Text, new Style(fg)));
        }
        return line;
    }

    private static string Escape(string text)
        => (text ?? string.Empty).Replace("[", "\\[", StringComparison.Ordinal);
}
