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
        header.Lines.Add(TextLine.FromMarkup("[bold cyan]⚓ Harbor[/] [grey]- modular AI coding agent[/]"));

        var statusLine = new Paragraph()
            .Alignment(Justify.Left);
        statusLine.Lines.Add(TextLine.FromMarkup(
            $"[grey]{Escape(Provider)}/{Escape(Model)} | {Escape(Agent)} | {TokensIn}↑ {TokensOut}↓ | ${Cost:F4} | {Status}[/]"));

        var spinner = IsStreaming
            ? (IWidget)new Paragraph("[cyan]⏳ generating…[/]").LeftAligned()
            : new SpinnerWidget { Kind = SpinnerKind.Dots };

        var inputText = string.IsNullOrEmpty(_input.Text) && IsReadingInput
            ? "[dim]type a message, or /help[/]"
            : Escape(_input.Text);
        var inputBox = new BoxWidget()
            .Border(Border.Rounded)
            .Title(TextLine.FromMarkup("[green]>[/]"))
            .Inner(new Paragraph(inputText).Alignment(Justify.Left));

        var footer = new Paragraph("[grey]q[/] quit  [grey]Ctrl+L[/] clear  [grey]Ctrl+C[/] abort  [grey]Tab[/] complete  [grey]↑↓[/] history")
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
        var widgets = new List<IWidget>();
        foreach (var entry in _chat.Lines)
            widgets.Add(new Text(RoleLine(entry.Role, entry.Content)));

        if (IsStreaming)
        {
            if (!string.IsNullOrEmpty(ThinkBuffer))
                widgets.Add(new Text(RoleLine("thinking", ThinkBuffer.Trim())));
            if (!string.IsNullOrEmpty(StreamBuffer))
                widgets.Add(new Text(RoleLine("assistant", StreamBuffer.Trim())));
        }

        if (widgets.Count == 0)
            widgets.Add(new Text(TextLine.FromMarkup("[dim]no messages yet…[/]")));

        return new ScrollViewWidget().Inner(new CompositeWidget(widgets)).VerticalScroll(ScrollMode.Auto);
    }

    private static TextLine RoleLine(string role, string content)
    {
        var (prefix, hex) = role switch
        {
            "user" => ("> ", "00FF00"),
            "assistant" => (": ", "00FFFF"),
            "tool" => ("→ ", "0000FF"),
            "tool-result" => ("  ", "808080"),
            "thinking" => ("💭 ", "808080"),
            "system" => ("• ", "808080"),
            "error" => ("✗ ", "FF0000"),
            _ => ("  ", "FFFFFF")
        };
        return TextLine.FromMarkup($"[{hex}]{Escape(prefix)}[/]{Escape(content ?? string.Empty)}");
    }

    private static string Escape(string text)
        => (text ?? string.Empty).Replace("[", "\\[", StringComparison.Ordinal);
}
