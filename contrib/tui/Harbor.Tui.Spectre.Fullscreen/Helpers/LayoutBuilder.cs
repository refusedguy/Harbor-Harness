using System.Text;
using Harbor.Tui.Spectre.Fullscreen.Components;
using Spectre.Console;
namespace Harbor.Tui.Spectre.Fullscreen.Helpers;
/// <summary>
///     Builds Spectre.Console layout panels for the fullscreen renderer.
///     Single responsibility: convert state → visual panels.
/// </summary>
internal sealed class LayoutBuilder
{
    private static readonly string[] BuiltinCommands =
    {
        "/help", "/exit", "/setup", "/auth", "/model", "/agent", "/config",
        "/providers", "/sessions", "/tui", "/storage", "/clear"
    };

    private readonly ChatState _chat;
    private readonly InputState _input;
    private readonly ScrollManager _scroll;

    public LayoutBuilder(ChatState chat, ScrollManager scroll, InputState input)
    {
        _chat = chat;
        _scroll = scroll;
        _input = input;
    }

    // Metrics displayed in header
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Agent { get; set; } = "code";
    public string Status { get; set; } = "idle";
    public decimal Cost { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }

    // Streaming state
    public bool IsStreaming { get; set; }
    public string StreamBuffer { get; set; } = string.Empty;
    public string ThinkBuffer { get; set; } = string.Empty;

    // Footer
    public string Footer { get; set; } = "Type a message, or /help.";
    public bool IsReadingInput { get; set; }

    public Layout Build()
    {
        var layout = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body"),
                new Layout("footer").Size(3));

        layout["header"].Update(BuildHeader());
        layout["body"].Update(BuildBody());
        layout["footer"].Update(BuildFooter());
        return layout;
    }

    private Panel BuildHeader()
    {
        string statusColor = Status switch
        {
            "running" => "cyan",
            "error" => "red",
            "compacting" => "yellow",
            _ => "grey"
        };

        string statusIcon = Status switch
        {
            "running" => "⟳",
            "error" => "✗",
            "compacting" => "⏳",
            _ => "●"
        };

        string scrollIndicator = _scroll.IsScrolling
            ? " [grey](scrolling: ↑↓ wheel, PageUp/Down, End=bottom)[/]"
            : "";

        var grid = new Grid().Expand();
        grid.AddColumn(new GridColumn().LeftAligned());
        grid.AddColumn(new GridColumn().Centered());
        grid.AddColumn(new GridColumn().RightAligned());

        grid.AddRow(
            new Markup($"[bold cyan]⚓ Harbor[/] [grey]»[/] [bold white]{Markup.Escape(Provider)}/{Markup.Escape(Model)}[/]"),
            new Markup($"[grey]agent:[/] [bold silver]{Markup.Escape(Agent)}[/]  [grey]•[/]  [{statusColor}]{statusIcon} {Markup.Escape(Status)}[/]{scrollIndicator}"),
            new Markup($"[bold green]${Cost:F4}[/] [grey]({TokensIn}↑ / {TokensOut}↓)[/]")
        );

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("cyan"),
            Padding = new Padding(1, 0),
            Expand = true
        };
    }

    private Panel BuildBody()
    {
        var visible = _chat.Lines.ToList();

        if (IsStreaming)
        {
            if (!string.IsNullOrEmpty(ThinkBuffer))
                visible.Add(new ChatState.ChatLine("thinking", ThinkBuffer.Trim()));
            if (!string.IsNullOrEmpty(StreamBuffer))
                visible.Add(new ChatState.ChatLine("assistant", StreamBuffer.Trim()));
        }

        int width = 80;
        try { width = Console.WindowWidth; }
        catch
        { /* Non-TTY */
        }

        int maxWidth = Math.Max(20, width - 6);
        int availableHeight = GetBodyHeight();

        var allMarkupLines = new List<string>();
        foreach (var chatLine in visible)
        {
            allMarkupLines.AddRange(RenderChatLine(chatLine, maxWidth));
            allMarkupLines.Add(string.Empty);
        }

        int totalLines = allMarkupLines.Count;
        int skip = 0;
        bool truncatedTop = false;
        bool truncatedBottom = false;

        if (totalLines > availableHeight)
        {
            int bottomSkip = totalLines - availableHeight;
            skip = Math.Max(0, bottomSkip - _scroll.Offset);
            if (skip > 0) truncatedTop = true;
            if (_scroll.Offset > 0) truncatedBottom = true;
        }

        var slicedLines = allMarkupLines.Skip(skip).Take(availableHeight).ToList();
        var bodyBuilder = new StringBuilder();

        if (truncatedTop)
            bodyBuilder.AppendLine($"[dim grey]▲ {skip} lines above (PageUp/wheel to scroll) ▲[/]");

        foreach (string line in slicedLines)
            bodyBuilder.AppendLine(line);

        if (truncatedBottom)
            bodyBuilder.AppendLine($"[dim grey]▼ {_scroll.Offset} lines below (PageDown/wheel to scroll) ▼[/]");

        return new Panel(new Markup(bodyBuilder.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0),
            Expand = true
        };
    }

    private static List<string> RenderChatLine(ChatState.ChatLine line, int maxWidth)
    {
        var result = new List<string>();

        switch (line.Role)
        {
            case "user":
                result.Add("[bold green]┌─ 👤 You[/]");
                foreach (string l in MarkdownRenderer.WordWrap(line.Content, maxWidth - 4))
                    result.Add($"[green]│[/] [white]{Markup.Escape(l)}[/]");
                result.Add("[dim green]└[/]");
                break;

            case "assistant":
                result.Add("[bold cyan]┌─ 🤖 Assistant[/]");
                foreach (string l in MarkdownRenderer.FormatToList(line.Content, maxWidth - 6))
                    result.Add($"[cyan]│[/] {l}");
                result.Add("[dim cyan]└[/]");
                break;

            case "thinking":
                result.Add("[italic dim grey]┌─ 🧠 Thinking[/]");
                foreach (string l in MarkdownRenderer.WordWrap(line.Content, maxWidth - 4))
                    result.Add($"[dim grey]│[/] [italic dim grey]{Markup.Escape(l)}[/]");
                result.Add("[dim grey]└[/]");
                break;

            case "tool":
                result.Add("[bold yellow]┌─ 🔧 Tool[/]");
                foreach (string l in MarkdownRenderer.WordWrap(line.Content, maxWidth - 4))
                    result.Add($"[yellow]│[/] {l}");
                result.Add("[dim yellow]└[/]");
                break;

            case "tool-result":
                result.Add("[bold blue]┌─ 📦 Result[/]");
                foreach (string l in MarkdownRenderer.WordWrap(line.Content, maxWidth - 4))
                    result.Add($"[blue]│[/] {Markup.Escape(l)}");
                result.Add("[dim blue]└[/]");
                break;

            case "error":
                result.Add("[bold red]┌─ ❌ Error[/]");
                foreach (string l in MarkdownRenderer.WordWrap(line.Content, maxWidth - 4))
                    result.Add($"[red]│[/] [bold red]{Markup.Escape(l)}[/]");
                result.Add("[dim red]└[/]");
                break;

            default:
                result.Add("[dim grey]┌─ ⚙️ System[/]");
                foreach (string l in MarkdownRenderer.WordWrap(line.Content, maxWidth - 4))
                    result.Add($"[dim grey]│[/] [dim]{l}[/]");
                result.Add("[dim grey]└[/]");
                break;
        }
        return result;
    }

    private Panel BuildFooter()
    {
        if (!IsReadingInput)
        {
            return new Panel(new Markup(Footer))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("grey"),
                Padding = new Padding(1, 0),
                Expand = true
            };
        }

        string typedText = Markup.Escape(_input.Text);
        string display;

        if (string.IsNullOrEmpty(typedText))
        {
            string hintLine = string.Join("  ", BuiltinCommands.Select(c => $"[dim blue]{c}[/]"));
            display = $"[green]›[/] [blink dim white]Type your message…[/]\n[dim]{hintLine}[/]";
        }
        else if (_input[0] == '/')
        {
            string current = _input.Text;
            var matches = BuiltinCommands
                .Where(c => c.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count > 0 && matches[0] != current)
            {
                string hint = Markup.Escape(matches[0][current.Length..]);
                display = $"[green]›[/] [white]{typedText}[/][dim blue]{hint}[/]\n[dim grey]  Tab to complete  ↑↓ = history  Enter = send  Esc = quit[/]";
            }
            else if (matches.Count > 0)
            {
                display = $"[green]›[/] [white]{typedText}[/]\n[dim green]  ✓ Press Enter to run[/]";
            }
            else
            {
                display = $"[green]›[/] [white]{typedText}[/]\n[dim red]  ✗ Unknown command[/]";
            }
        }
        else
        {
            display = $"[green]›[/] [white]{typedText}[/]\n[dim grey]  ↑↓ = history  Enter = send  Alt+Enter = newline  Esc = quit[/]";
        }

        return new Panel(new Markup(display))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("green"),
            Padding = new Padding(1, 0),
            Expand = true
        };
    }

    private static int GetBodyHeight()
    {
        try { return Math.Max(3, Console.WindowHeight - 8); }
        catch { return 16; }
    }

    internal static int GetTotalVisibleLines(ChatState.ChatLine[] visible, int maxWidth)
    {
        int count = 0;
        foreach (var line in visible)
        {
            count += RenderChatLine(line, maxWidth).Count;
            count++;
        }
        return count;
    }
}
