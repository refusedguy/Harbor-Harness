using System.Collections.Generic;
using Harbor.Tui.SpectreTui.Components;
using Spectre.Console;
using Spectre.Tui;
using Spectre.Tui.App;

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

    public LayoutBuilder(ChatState chat, InputState input)
    {
        _chat = chat;
        _input = input;
    }

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
        var title = string.IsNullOrEmpty(Provider)
            ? "⚓ Harbor"
            : $"⚓ Harbor — {Escape(Provider)}/{Escape(Model)} ({Escape(Agent)})";
        header.Lines.Add(LineExtensions.FromMarkup($"[bold cyan]{Escape(title)}[/]", null));

        var statusLine = new Paragraph()
            .Alignment(Justify.Left);
        statusLine.Lines.Add(LineExtensions.FromMarkup(
            $"[grey]{Escape(Provider)}/{Escape(Model)} | {Escape(Agent)} | {TokensIn}↑ {TokensOut}↓ | ${Cost:F4} | {Status}[/]", null));

        var spinner = IsStreaming
            ? (IWidget)new Paragraph(LineExtensions.FromMarkup("[cyan]⏳ generating…[/]", null)).LeftAligned()
            : new SpinnerWidget { Kind = SpinnerKind.Dots };

        var inputText = string.IsNullOrEmpty(_input.Text) && IsReadingInput
            ? "[dim]type a message, or /help[/]"
            : Escape(_input.Text);
        var inputBox = new BoxWidget()
            .Border(Border.Rounded)
            .MarkupTitle("[green]>[/]")
            .Inner(new Paragraph(LineExtensions.FromMarkup(inputText, null)).Alignment(Justify.Left));

        var footer = Paragraph.FromMarkup(
            "[grey]q[/] quit  [grey]Ctrl+L[/] clear  [grey]Ctrl+C[/] abort  [grey]Tab[/] complete  [grey]↑↓[/] history", null)
            .Centered();

        return new Dictionary<string, IWidget>
        {
            ["Header"] = header,
            ["History"] = BuildHistory(),
            ["Status"] = statusLine,
            ["Spinner"] = spinner,
            ["Input"] = inputBox,
            ["Footer"] = footer,
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
            lines.Add(LineExtensions.FromMarkup("[dim]no messages yet…[/]", null));

        var text = new Text();
        text.Lines.AddRange(lines);
        return new ScrollViewWidget().Inner(text).VerticalScroll(ScrollMode.Auto);
    }

    private static TextLine Blank() => LineExtensions.FromMarkup("", null);

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
        var prefix = RolePrefix(role);
        var body = (content ?? string.Empty).Replace("\\n", "\n");
        var segments = body.Split('\n');
        var result = new List<TextLine>();
        for (var i = 0; i < segments.Length; i++)
        {
            var line = new TextLine();
            line.Spans.Add(new TextSpan(i == 0 ? prefix : "  ", new Style(color, null)));
            var parsed = ParseMarkup(segments[i], color);
            line.Spans.AddRange(parsed.Spans);
            result.Add(line);
        }
        return result;
    }

    private static TextLine ParseMarkup(string text, Color fallback)
    {
        var parsed = LineExtensions.FromMarkup(text ?? string.Empty, null);
        var line = new TextLine();
        foreach (var span in parsed.Spans)
        {
            var fg = span.Style is null || span.Style.Value.Foreground == Color.Default
                ? fallback
                : span.Style.Value.Foreground;
            line.Spans.Add(new TextSpan(span.Text, new Style(fg, null)));
        }
        return line;
    }

    private static string Escape(string text)
        => (text ?? string.Empty).Replace("[", "\\[", StringComparison.Ordinal);
}
