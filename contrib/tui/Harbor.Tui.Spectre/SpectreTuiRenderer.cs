using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging;
using Spectre.Console;
namespace Harbor.Tui.Spectre;
/// <summary>
///     Spectre.Console-based TUI renderer — rich panels, tables, markup.
/// </summary>
public sealed class SpectreTuiRenderer : BaseTuiRenderer
{

    public SpectreTuiRenderer(ILogger<SpectreTuiRenderer> logger) : base(logger)
    {
        Context = new SpectreRenderContext();
        RegisterEventHandlers();
    }
    public override ITuiRenderContext Context { get; }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
    {
        // Chat history and the input prompt are handled live by the line REPL
        // (tokens stream directly, the prompt is written by ReadLineAsync). Only
        // the status bar and diff overlay are rendered through the builtin views.
        if (placement is TuiViewPlacement.ChatHistory or TuiViewPlacement.Input) return false;
        return base.ShouldRenderPlacement(placement, @event);
    }

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
        => InitializeGuardedAsync(
            static () => AnsiConsole.Write(new Rule("[bold cyan]Harbor[/] — AI coding agent")),
            ct: ct);

    public override async Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Live output is painted by the registered IAgentEventHandlers (issue
        // #185 visitor registry); everything else (status bar, finalized chat
        // history, diff overlay) renders through the builtin views in
        // BaseTuiRenderer.
        await DispatchToHandlersAsync(@event, ct).ConfigureAwait(false);
        await base.RenderAsync(@event, ct).ConfigureAwait(false);
    }

    private void RegisterEventHandlers()
    {
        RegisterHandler(new AssistantStreamHandler());
        RegisterHandler(new ToolStartHandler());
        RegisterHandler(new CompactionHandler());
        RegisterHandler(new AgentErrorHandler());
    }

    /// <summary>Live assistant stream: start banner, token deltas, end newline.</summary>
    private sealed class AssistantStreamHandler : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) =>
            @event is MessageStartEvent or MessageUpdateEvent or MessageEndEvent;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            switch (@event)
            {
                case MessageStartEvent:
                    AnsiConsole.Write(MarkupCache.GetOrParse("[cyan][[assistant]][/] "));
                    break;

                case MessageUpdateEvent mu:
                    RenderLiveToken(mu.LlmEvent);
                    break;

                case MessageEndEvent:
                    AnsiConsole.WriteLine();
                    break;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Tool-call start line. Emitted as a live line (not accumulated in the
    ///     chat history view) so it appears inline without repainting the whole log.
    /// </summary>
    private sealed class ToolStartHandler : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) => @event is ToolExecutionStartEvent;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            var tes = (ToolExecutionStartEvent)@event;
            AnsiConsole.WriteLine();
            var panel = new Panel(MarkupCache.GetOrParse($"[bold blue]→ {Markup.Escape(tes.ToolName)}[/] [dim]{Markup.Escape(tes.Args.GetRawText())}[/]"))
            {
                Padding = new Padding(1, 0)
            };
            AnsiConsole.Write(panel);
            return Task.CompletedTask;
        }
    }

    /// <summary>Compaction lifecycle lines.</summary>
    private sealed class CompactionHandler : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) =>
            @event is CompactionStartedEvent or CompactionCompletedEvent;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            switch (@event)
            {
                case CompactionStartedEvent:
                    AnsiConsole.Write(MarkupCache.GetOrParse("[dim][compacting context...][/]\n"));
                    break;

                case CompactionCompletedEvent cc:
                    AnsiConsole.Write(MarkupCache.GetOrParse($"[dim][compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens in {cc.Duration.TotalSeconds:F1}s][/]\n"));
                    break;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Agent error line.</summary>
    private sealed class AgentErrorHandler : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) => @event is AgentErrorEvent;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            var err = (AgentErrorEvent)@event;
            AnsiConsole.Write(MarkupCache.GetOrParse($"[red][[error]] {Markup.Escape(err.Message)}[/]\n"));
            return Task.CompletedTask;
        }
    }

    private static void RenderLiveToken(LlmEvent evt)
    {
        switch (evt)
        {
            case TextDeltaEvent td:
                AnsiConsole.Write(Markup.Escape(td.Delta));
                break;

            case ThinkingDeltaEvent thd:
                AnsiConsole.Write(MarkupCache.GetOrParse($"[dim italic]{Markup.Escape(thd.Delta)}[/]"));
                break;

            case ToolCallDeltaEvent tcd:
                AnsiConsole.Write(MarkupCache.GetOrParse($"[dim]{Markup.Escape(tcd.ArgsDelta)}[/]"));
                break;
        }
    }

    public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        AnsiConsole.Write(MarkupCache.GetOrParse($"[green]{Markup.Escape(prompt)}[/]"));
        string? line = Console.ReadLine();
        // EOF is absence, not failure: Maybe.None, not Success("") (#589).
        return Task.FromResult(Maybe.From(line));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        AnsiConsole.Write(Markup.Escape(text));
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        if (text is null)
            AnsiConsole.WriteLine();
        else
            AnsiConsole.Write(MarkupCache.GetOrParse(Markup.Escape(text) + "\n"));
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        AnsiConsole.Clear();
        return Task.FromResult(Result.Success());
    }
}

internal sealed class SpectreRenderContext : ITuiRenderContext
{
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public bool SupportsColor => true;

    public void Write(string text) => AnsiConsole.Write(Markup.Escape(text));
    public void WriteLine(string? text = null)
    {
        if (text is null) AnsiConsole.WriteLine();
        else AnsiConsole.Write(MarkupCache.GetOrParse(Markup.Escape(text) + "\n"));
    }

    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
    {
        string fgHex = foreground.ToString();
        string markup = $"[#{fgHex[1..]}]{Markup.Escape(text)}[/]";
        AnsiConsole.Write(MarkupCache.GetOrParse(markup));
    }

    public void WriteStyled(string text, TuiStyle style)
    {
        var parts = new List<string>();
        if (style.HasFlag(TuiStyle.Bold)) parts.Add("bold");
        if (style.HasFlag(TuiStyle.Italic)) parts.Add("italic");
        if (style.HasFlag(TuiStyle.Underline)) parts.Add("underline");
        if (style.HasFlag(TuiStyle.Dim)) parts.Add("dim");
        if (style.HasFlag(TuiStyle.Strike)) parts.Add("strikethrough");

        string tags = string.Join(" ", parts);
        string markup = parts.Count > 0
            ? $"[{tags}]{Markup.Escape(text)}[/]"
            : Markup.Escape(text);
        AnsiConsole.Write(MarkupCache.GetOrParse(markup));
    }

    public void SetCursorPosition(int row, int col) { }
    public void ClearLine() => AnsiConsole.Write("\x1b[2K\r");
    public void Clear() => AnsiConsole.Clear();
    public void HideCursor() => Console.Write("\x1b[?25l");
    public void ShowCursor() => Console.Write("\x1b[?25h");
    public void EnterAlternateScreen() => Console.Write("\x1b[?1049h");
    public void ExitAlternateScreen() => Console.Write("\x1b[?1049l");
    public void Flush() { }
}
