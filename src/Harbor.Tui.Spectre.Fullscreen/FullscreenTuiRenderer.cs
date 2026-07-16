using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;

namespace Harbor.Tui.Spectre.Fullscreen;

/// <summary>
///     Full-screen Spectre.Console TUI renderer.
///     <para>
///         Owns the interactive lifecycle: renders a live <see cref="Layout" /> (header/status +
///         scrollable chat + footer input) that is repainted on every <see cref="AgentEvent" />,
///         and drives its own REPL loop with a multi-line, history-aware input prompt.
///     </para>
/// </summary>
public sealed class FullscreenTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly List<ChatLine> _lines = new();
    private readonly List<string> _inputHistory = new();
    private readonly StringBuilder _inputBuffer = new();
    private readonly object _renderLock = new();

    private decimal _cost;
    private string _footer = "Type a message, or /help.";
    private bool _isStreaming;
    private string _model = string.Empty;
    private string _provider = string.Empty;
    private string _agent = "code";
    private string _status = "idle";
    private int _tokensIn;
    private int _tokensOut;
    private string _streamBuffer = string.Empty;
    private string _thinkBuffer = string.Empty;
    private bool _stop;
    private bool _isReadingInput;
    private LiveDisplayContext? _liveCtx;
    private Func<string, Task>? _slashHandler;

    public FullscreenTuiRenderer(ILogger<FullscreenTuiRenderer> logger) : base(logger)
    {
        Context = new FullscreenRenderContext();
    }

    public override ITuiRenderContext Context { get; }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            AnsiConsole.Write(new Rule("[bold cyan]Harbor[/] — [silver]modular AI coding agent[/]")
            {
                Style = Style.Parse("grey")
            });
            AnsiConsole.WriteLine();
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        ApplyEvent(@event);
        base.RenderAsync(@event, ct).GetAwaiter().GetResult();
        Redraw();
        return Task.CompletedTask;
    }

    private void ApplyEvent(AgentEvent @event)
    {
        switch (@event)
        {
            case AgentStartEvent ase:
                _status = "running";
                // Наполняем историю из стартового ивента только ОДИН раз при запуске.
                // В интерактивном режиме новые сообщения юзера добавляются вручную в цикле ввода!
                if (_lines.Count == 0)
                {
                    foreach (var m in ase.Messages)
                        if (m is UserMessage u)
                            _lines.Add(new ChatLine("user", u.Content));
                }
                break;

            case MessageStartEvent:
                _status = "running";
                _isStreaming = true;
                _streamBuffer = string.Empty;
                break;

            case MessageUpdateEvent mu:
                switch (mu.LlmEvent)
                {
                    case TextDeltaEvent td:
                        _streamBuffer += td.Delta;
                        break;
                    case ThinkingDeltaEvent thd:
                        _thinkBuffer += thd.Delta;
                        break;
                    case ToolCallStartEvent tcs:
                        _lines.Add(new ChatLine("tool", $"→ {tcs.ToolName}"));
                        break;
                    case StepFinishEvent sf when sf.Usage is not null:
                        _tokensIn += sf.Usage.InputTokens;
                        _tokensOut += sf.Usage.OutputTokens;
                        _cost += EstimateCost(sf.Usage.InputTokens, sf.Usage.OutputTokens);
                        break;
                }
                break;

            case MessageEndEvent:
                if (!string.IsNullOrEmpty(_thinkBuffer))
                {
                    _lines.Add(new ChatLine("thinking", _thinkBuffer.Trim()));
                    _thinkBuffer = string.Empty;
                }
                if (!string.IsNullOrEmpty(_streamBuffer))
                {
                    _lines.Add(new ChatLine("assistant", _streamBuffer.Trim()));
                    _streamBuffer = string.Empty;
                }
                _isStreaming = false;
                _status = "idle";
                break;

            case ToolExecutionStartEvent tes:
                string args = tes.Args.GetRawText();
                _lines.Add(new ChatLine("tool",
                    string.IsNullOrEmpty(args) || args == "{}"
                        ? $"→ {tes.ToolName}"
                        : $"→ {tes.ToolName}  [dim]{Markup.Escape(args)}[/]"));
                break;

            case ToolExecutionEndEvent tee:
                string label = tee.IsError ? "[red]✗[/]" : "[green]✓[/]";
                string preview = tee.Result.Output.Length > 600
                    ? tee.Result.Output[..600] + "..."
                    : tee.Result.Output;
                _lines.Add(new ChatLine("tool-result", $"{label} {Markup.Escape(preview.Trim())}"));
                break;

            case CompactionStartedEvent:
                _status = "compacting";
                break;

            case CompactionCompletedEvent cc:
                _status = "running";
                _lines.Add(new ChatLine("system",
                    $"[dim]compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens in {cc.Duration.TotalSeconds:F1}s[/]"));
                break;

            case AgentErrorEvent err:
                _status = "error";
                _lines.Add(new ChatLine("error", err.Message));
                break;

            case AgentEndEvent:
                _status = "idle";
                break;
        }
    }

    private static decimal EstimateCost(int inTok, int outTok)
        => (decimal)inTok / 1_000_000m * 3m + (decimal)outTok / 1_000_000m * 15m;

    private void Redraw()
    {
        lock (_renderLock)
        {
            _liveCtx?.UpdateTarget(BuildLayout());
        }
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        var result = AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]{Markup.Escape(prompt)}[/]").AllowEmpty());
        return Task.FromResult(Result.Success(result));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        AnsiConsole.Write(Markup.Escape(text));
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        AnsiConsole.WriteLine(text ?? string.Empty);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        AnsiConsole.Clear();
        return Task.FromResult(Result.Success());
    }

    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _model = agent.State.Agent.Model;
        _provider = agent.State.Agent.ProviderId;
        _agent = agent.State.Agent.Name.Value;

        var inputTask = Task.Run(() => RunInputLoopAsync(agent, ct), ct);
        var live = AnsiConsole.Live(BuildLayout());

        AnsiConsole.Cursor.Hide();

        live.StartAsync(async ctx =>
        {
            _liveCtx = ctx;
            while (!_stop)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }).GetAwaiter().GetResult();

        inputTask.GetAwaiter().GetResult();

        AnsiConsole.Cursor.Show();
        AnsiConsole.WriteLine();
        return Task.FromResult(0);
    }

    private void RunInputLoopAsync(IAgent agent, CancellationToken ct)
    {
        while (!_stop)
        {
            if (agent.State.IsRunning)
            {
                var key = WaitForKey(ct);
                if (key == ConsoleKey.Escape)
                {
                    agent.AbortSource.Cancel();
                    _lines.Add(new ChatLine("system", "[yellow]aborting…[/]"));
                    agent.WaitForIdleAsync(ct).GetAwaiter().GetResult();
                }
                continue;
            }

            _footer = "Type a message, or [grey]/help[/].  Alt+Enter = newline, Enter = submit.";
            string? input = ReadInput(ct);
            if (input is null) { _stop = true; return; }

            string trimmed = input.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed is "exit" or "quit" or ":q") { _stop = true; return; }

            if (trimmed.StartsWith('/'))
            {
                if (_slashHandler is not null) _slashHandler(trimmed).GetAwaiter().GetResult();
                continue;
            }

            _inputHistory.Add(trimmed);
            _lines.Add(new ChatLine("user", trimmed));
            _footer = "[yellow]working…[/]  (Esc to abort)";
            agent.PromptAsync(trimmed, ct).GetAwaiter().GetResult();
        }
    }

    private string? ReadInput(CancellationToken ct)
    {
        _inputBuffer.Clear();
        _isReadingInput = true;
        Redraw();

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                _isReadingInput = false;
                return null;
            }
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(20);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter && key.Modifiers == ConsoleModifiers.Alt)
            {
                _inputBuffer.Append('\n');
                Redraw();
                continue;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                var result = _inputBuffer.ToString();
                _inputBuffer.Clear();
                _isReadingInput = false;
                Redraw();
                return result;
            }
            if (key.Key == ConsoleKey.Escape)
            {
                var result = _inputBuffer.ToString();
                _inputBuffer.Clear();
                _isReadingInput = false;
                Redraw();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                    Redraw();
                }
                continue;
            }
            if (key.KeyChar != '\0')
            {
                _inputBuffer.Append(key.KeyChar);
                Redraw();
            }
        }
    }

    private static ConsoleKey WaitForKey(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Console.KeyAvailable) return Console.ReadKey(intercept: true).Key;
            Thread.Sleep(50);
        }
        return ConsoleKey.Escape;
    }

    private Layout BuildLayout()
    {
        var layout = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body"),
                new Layout("footer").Size(3));

        layout["header"].Update(Header());
        layout["body"].Update(Body());
        layout["footer"].Update(Footer());
        return layout;
    }

    private Panel Header()
    {
        var statusColor = _status switch
        {
            "running" => "cyan",
            "error" => "red",
            "compacting" => "yellow",
            _ => "grey"
        };

        var grid = new Grid().Expand();
        grid.AddColumn(new GridColumn().LeftAligned());
        grid.AddColumn(new GridColumn().Centered());
        grid.AddColumn(new GridColumn().RightAligned());

        grid.AddRow(
            new Markup($"[bold cyan]⚓ HARBOR[/] [grey]»[/] [bold white]{Markup.Escape(_provider)}/{Markup.Escape(_model)}[/]"),
            new Markup($"[grey]agent:[/] [bold silver]{Markup.Escape(_agent)}[/]  [grey]•[/]  [grey]status:[/] [{statusColor}]{Markup.Escape(_status)}[/]"),
            new Markup($"[bold green]Cost: {_cost:C4}[/] [grey]({_tokensIn}↑ / {_tokensOut}↓)[/]")
        );

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("cyan"),
            Padding = new Padding(1, 0),
            Expand = true
        };
    }

    private Panel Body()
    {
        var visible = _lines.ToList();

        if (_isStreaming)
        {
            if (!string.IsNullOrEmpty(_thinkBuffer))
                visible.Add(new ChatLine("thinking", _thinkBuffer.Trim()));
            if (!string.IsNullOrEmpty(_streamBuffer))
                visible.Add(new ChatLine("assistant", _streamBuffer.Trim()));
        }

        int width = 80;
        int height = 24;
        try
        {
            width = Console.WindowWidth;
            height = Console.WindowHeight;
        }
        catch { /* Fallback */ }

        int maxWidth = Math.Max(20, width - 6);
        int availableHeight = Math.Max(3, height - 8);

        var allMarkupLines = new List<string>();
        foreach (var chatLine in visible)
        {
            allMarkupLines.AddRange(RenderLineToMarkupLines(chatLine, maxWidth));
            allMarkupLines.Add(string.Empty);
        }

        var slicedLines = allMarkupLines;
        bool truncated = false;
        if (allMarkupLines.Count > availableHeight)
        {
            slicedLines = allMarkupLines.Skip(allMarkupLines.Count - availableHeight).ToList();
            truncated = true;
        }

        var bodyBuilder = new StringBuilder();
        if (truncated)
        {
            bodyBuilder.AppendLine("[dim grey]▲ ... (older history truncated, increase window size to view) ...[/]");
        }

        foreach (var line in slicedLines)
        {
            bodyBuilder.AppendLine(line);
        }

        return new Panel(new Markup(bodyBuilder.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0),
            Expand = true
        };
    }

    private IEnumerable<string> RenderLineToMarkupLines(ChatLine line, int maxWidth)
    {
        var result = new List<string>();
        switch (line.Role)
        {
            case "user":
                result.Add("[bold green]👤 User[/]");
                foreach (var l in WrapText(line.Content, maxWidth - 4))
                    result.Add($"[green]│[/] [white]{Markup.Escape(l)}[/]");
                break;

            case "assistant":
                result.Add("[bold cyan]🤖 Assistant[/]");
                var formatted = FormatAssistantContentToList(line.Content, maxWidth - 6);
                foreach (var l in formatted)
                    result.Add($"[cyan]│[/] {l}");
                break;

            case "thinking":
                result.Add("[italic dim grey]🧠 Thinking[/]");
                foreach (var l in WrapText(line.Content, maxWidth - 4))
                    result.Add($"[dim grey]│[/] [italic dim grey]{Markup.Escape(l)}[/]");
                break;

            case "tool":
                result.Add("[bold yellow]🔧 Tool Call[/]");
                foreach (var l in WrapText(line.Content, maxWidth - 4))
                    result.Add($"[yellow]│[/] {l}");
                break;

            case "tool-result":
                result.Add("[bold blue]📦 Tool Result[/]");
                foreach (var l in WrapText(line.Content, maxWidth - 4))
                    result.Add($"[blue]│[/] {l}");
                break;

            case "error":
                result.Add("[bold red]❌ Error[/]");
                foreach (var l in WrapText(line.Content, maxWidth - 4))
                    result.Add($"[red]│[/] [bold red]{Markup.Escape(l)}[/]");
                break;

            default:
                result.Add("[bold dim grey]⚙️ System[/]");
                foreach (var l in WrapText(line.Content, maxWidth - 4))
                    result.Add($"[dim grey]│[/] [dim]{l}[/]");
                break;
        }
        return result;
    }

    private static List<string> FormatAssistantContentToList(string content, int maxWidth)
    {
        var lines = content.Replace("\r", "").Split('\n');
        var result = new List<string>();
        bool inCodeBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                if (inCodeBlock)
                {
                    var lang = trimmed[3..].Trim();
                    result.Add($"[bold yellow]┌─── Code: {lang} ──────────────────────────────────[/]");
                }
                else
                {
                    result.Add("[bold yellow]└─── Code End ──────────────────────────────────────[/]");
                }
                continue;
            }

            if (inCodeBlock)
            {
                var wrapped = WrapText(line, maxWidth - 4);
                foreach (var wl in wrapped)
                {
                    result.Add($"[bold yellow]│[/] [silver]{Markup.Escape(wl)}[/]");
                }
            }
            else
            {
                var wrapped = WrapText(line, maxWidth);
                foreach (var wl in wrapped)
                {
                    result.Add($"[white]{Markup.Escape(wl)}[/]");
                }
            }
        }

        return result;
    }

    private static List<string> WrapText(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return new List<string> { string.Empty };
        var lines = new List<string>();
        var rawLines = text.Replace("\r", "").Split('\n');
        foreach (var rawLine in rawLines)
        {
            if (rawLine.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }
            int index = 0;
            while (index < rawLine.Length)
            {
                int length = Math.Min(maxWidth, rawLine.Length - index);
                lines.Add(rawLine.Substring(index, length));
                index += length;
            }
        }
        return lines;
    }

    private Panel Footer()
    {
        if (_isReadingInput)
        {
            var typedText = Markup.Escape(_inputBuffer.ToString());
            var display = string.IsNullOrEmpty(typedText)
                ? "[green]›[/] [blink dim white]Type your message here...[/]"
                : $"[green]›[/] [white]{typedText}[/]";

            return new Panel(new Markup(display))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("green"),
                Padding = new Padding(1, 0),
                Expand = true
            };
        }

        return new Panel(new Markup(_footer))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("grey"),
            Padding = new Padding(1, 0),
            Expand = true
        };
    }

    private sealed record ChatLine(string Role, string Content);
}

/// <summary>
///     Render context shim — full-screen renderer draws through <see cref="AnsiConsole" />
///     directly, so the <see cref="ITuiRenderContext" /> is only used by the base view dispatch.
/// </summary>
internal sealed class FullscreenRenderContext : ITuiRenderContext
{
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public bool SupportsColor => true;
    public void Write(string text) => AnsiConsole.Write(Markup.Escape(text));
    public void WriteLine(string? text = null) => AnsiConsole.WriteLine(text ?? string.Empty);
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
        => AnsiConsole.Write(new Markup($"[{foreground.ToString()[1..]}]{Markup.Escape(text)}[/]"));
    public void WriteStyled(string text, TuiStyle style) => AnsiConsole.Write(Markup.Escape(text));
    public void SetCursorPosition(int row, int col) { }
    public void ClearLine() { }
    public void Clear() => AnsiConsole.Clear();
    public void HideCursor() { }
    public void ShowCursor() { }
    public void EnterAlternateScreen() => AnsiConsole.Write("\x1b[?1049h");
    public void ExitAlternateScreen() => AnsiConsole.Write("\x1b[?1049l");
    public void Flush() { }
}
