using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Tui.Spectre.Fullscreen.Components;
using Harbor.Tui.Spectre.Fullscreen.Helpers;
using Microsoft.Extensions.Logging;
using Spectre.Console;
namespace Harbor.Tui.Spectre.Fullscreen;
/// <summary>
///     Full-screen interactive TUI renderer — thin orchestrator.
///     Delegates to: ChatState (history), ScrollManager (scroll), InputState (input),
///     LayoutBuilder (rendering), MouseHandler (mouse), MarkdownRenderer (formatting).
/// </summary>
public sealed class FullscreenTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{

    private static readonly string[] BuiltinCommands =
    {
        "/help", "/exit", "/setup", "/auth", "/model", "/agent", "/config",
        "/providers", "/sessions", "/tui", "/storage", "/clear"
    };
    private readonly ChatState _chat = new();
    private readonly InputState _input = new();
    private readonly LayoutBuilder _layout;
    private readonly object _renderLock = new();
    private readonly ScrollManager _scroll = new();

    private decimal _cost;
    private string _footer = "Type a message, or /help.";
    private bool _isStreaming;
    private LiveDisplayContext? _liveCtx;
    private Func<string, Task>? _slashHandler;
    private bool _stop;
    private string _streamBuffer = string.Empty;
    private string _thinkBuffer = string.Empty;

    public FullscreenTuiRenderer(ILogger<FullscreenTuiRenderer> logger) : base(logger)
    {
        Context = new FullscreenRenderContext();
        _layout = new LayoutBuilder(_chat, _scroll, _input);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test hooks
    // ═══════════════════════════════════════════════════════════════

    internal int TestScrollOffset => _scroll.Offset;
    internal bool TestIsScrolling => _scroll.IsScrolling;
    internal int TestInputHistoryCount => _input.HistoryCount;
    internal int TestHistoryIndex => _input.HistoryIndex;
    internal string TestInputBuffer => _input.Text;

    public override ITuiRenderContext Context { get; }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    // ═══════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            AnsiConsole.Write(new Rule("[bold cyan]⚓ Harbor[/] [grey]— modular AI coding agent[/]")
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

    public override async Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        ApplyEvent(@event);
        await base.RenderAsync(@event, ct).ConfigureAwait(false);
        Redraw();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Interactive REPL — delegates input to InputState, scroll to ScrollManager
    // ═══════════════════════════════════════════════════════════════

    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _layout.Model = agent.State.Agent.Model;
        _layout.Provider = agent.State.Agent.ProviderId;
        _layout.Agent = agent.State.Agent.Name.Value;

        Console.Write("\x1b[?1000h\x1b[?1006h");

        var inputTask = Task.Run(() => RunInputLoopAsync(agent, ct), ct);
        var live = AnsiConsole.Live(_layout.Build());

        AnsiConsole.Cursor.Hide();
        await live.StartAsync(async ctx =>
        {
            _liveCtx = ctx;
            while (!_stop)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        await inputTask.ConfigureAwait(false);

        Console.Write("\x1b[?1006l\x1b[?1000l");
        AnsiConsole.Cursor.Show();
        AnsiConsole.WriteLine();
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Base overrides
    // ═══════════════════════════════════════════════════════════════

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        string result = AnsiConsole.Prompt(new TextPrompt<string>($"[green]{Markup.Escape(prompt)}[/]").AllowEmpty());
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

    // ═══════════════════════════════════════════════════════════════
    //  Event handling — delegates to ChatState
    // ═══════════════════════════════════════════════════════════════

    private void ApplyEvent(AgentEvent @event)
    {
        _layout.Status = "idle";

        switch (@event)
        {
            case AgentStartEvent ase:
                _layout.Status = "running";
                if (_chat.Count == 0)
                    foreach (var m in ase.Messages)
                    {
                        if (m is UserMessage u)
                            _chat.Add("user", u.Content);
                    }
                _scroll.Reset();
                break;

            case MessageStartEvent:
                _layout.Status = "running";
                _isStreaming = true;
                _streamBuffer = string.Empty;
                _thinkBuffer = string.Empty;
                break;

            case MessageUpdateEvent mu:
                switch (mu.LlmEvent)
                {
                    case TextDeltaEvent td: _streamBuffer += td.Delta; break;
                    case ThinkingDeltaEvent thd: _thinkBuffer += thd.Delta; break;
                    case ToolCallStartEvent tcs: _chat.Add("tool", $"→ {tcs.ToolName}"); break;
                    case StepFinishEvent sf when sf.Usage is not null:
                        _layout.TokensIn += sf.Usage.InputTokens;
                        _layout.TokensOut += sf.Usage.OutputTokens;
                        _cost += EstimateCost(sf.Usage.InputTokens, sf.Usage.OutputTokens);
                        _layout.Cost = _cost;
                        break;
                }
                break;

            case MessageEndEvent:
                if (!string.IsNullOrEmpty(_thinkBuffer)) _chat.Add("thinking", _thinkBuffer.Trim());
                if (!string.IsNullOrEmpty(_streamBuffer)) _chat.Add("assistant", _streamBuffer.Trim());
                _streamBuffer = string.Empty;
                _thinkBuffer = string.Empty;
                _isStreaming = false;
                _scroll.Reset();
                break;

            case ToolExecutionStartEvent tes:
                string args = tes.Args.GetRawText();
                _chat.Add("tool", string.IsNullOrEmpty(args) || args == "{}"
                    ? $"→ {tes.ToolName}"
                    : $"→ {tes.ToolName}  [dim]{Markup.Escape(args)}[/]");
                break;

            case ToolExecutionEndEvent tee:
                string label = tee.IsError ? "[red]✗[/]" : "[green]✓[/]";
                string preview = tee.Result.Output.Length > 600
                    ? tee.Result.Output[..600] + "..." : tee.Result.Output;
                _chat.Add("tool-result", $"{label} {Markup.Escape(preview.Trim())}");
                break;

            case CompactionStartedEvent: _layout.Status = "compacting"; break;

            case CompactionCompletedEvent cc:
                _layout.Status = "running";
                _chat.Add("system", $"[dim]compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens in {cc.Duration.TotalSeconds:F1}s[/]");
                break;

            case AgentErrorEvent err:
                _layout.Status = "error";
                _chat.Add("error", err.Message);
                break;

            case AgentEndEvent: _layout.Status = "idle"; break;
        }

        // Sync streaming state to layout
        _layout.IsStreaming = _isStreaming;
        _layout.StreamBuffer = _streamBuffer;
        _layout.ThinkBuffer = _thinkBuffer;
    }

    private static decimal EstimateCost(int inTok, int outTok)
        => inTok / 1_000_000m * 3m + outTok / 1_000_000m * 15m;

    private async Task RunInputLoopAsync(IAgent agent, CancellationToken ct)
    {
        while (!_stop)
        {
            if (agent.State.IsRunning)
            {
                _footer = "[yellow]⏳ Working…[/]  [grey](Esc = abort, wheel/PageUp/Down = scroll)[/]";
                _layout.Footer = _footer;
                await RunWaitLoopAsync(agent, ct).ConfigureAwait(false);
                continue;
            }

            _footer = _scroll.IsScrolling
                ? "[grey]Scroll mode: PageUp/Down or wheel to navigate, End to return. Type to resume.[/]"
                : "[grey]Type a message, or /help.  ↑↓ = history  Tab = autocomplete  Ctrl+L = clear  Esc = quit[/]";
            _layout.Footer = _footer;

            string? input = await ReadInputAsync(ct).ConfigureAwait(false);
            if (input is null)
            {
                _stop = true;
                return;
            }

            string trimmed = input.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed is "exit" or "quit" or ":q")
            {
                _stop = true;
                return;
            }

            if (trimmed.StartsWith('/'))
            {
                if (_slashHandler is not null) await _slashHandler(trimmed).ConfigureAwait(false);
                _chat.Add("system", $"[dim]/{trimmed[1..]}[/]");
                continue;
            }

            _input.Submit(trimmed);
            _chat.Add("user", trimmed);
            _footer = "[yellow]⏳ Working…[/]  [grey](Esc = abort, wheel/PageUp/Down = scroll)[/]";
            _layout.Footer = _footer;
            await agent.PromptAsync(trimmed, ct).ConfigureAwait(false);
        }
    }

    private async Task RunWaitLoopAsync(IAgent agent, CancellationToken ct)
    {
        while (agent.State.IsRunning && !_stop)
        {
            if (ct.IsCancellationRequested)
            {
                _stop = true;
                return;
            }
            if (!Console.KeyAvailable)
            {
                await Task.Delay(30, ct).ConfigureAwait(false);
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            if (key.KeyChar == '\x1b')
            {
                var mouse = MouseHandler.ParseSequence();
                if (mouse == MouseHandler.MouseAction.ScrollUp)
                {
                    DoScrollUp(3);
                    continue;
                }
                if (mouse == MouseHandler.MouseAction.ScrollDown)
                {
                    DoScrollDown(3);
                }
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    agent.AbortSource.Cancel();
                    _chat.Add("system", "[yellow]⏹ Aborted.[/]");
                    await agent.WaitForIdleAsync(ct).ConfigureAwait(false);
                    break;
                case ConsoleKey.PageUp: DoScrollUp(5); break;
                case ConsoleKey.PageDown: DoScrollDown(5); break;
                case ConsoleKey.UpArrow: DoScrollUp(1); break;
                case ConsoleKey.DownArrow: DoScrollDown(1); break;
                case ConsoleKey.Home: DoScrollToTop(); break;
                case ConsoleKey.End: DoScrollToBottom(); break;
                case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    agent.AbortSource.Cancel();
                    _chat.Add("system", "[yellow]⏹ Cancelled (Ctrl+C).[/]");
                    await agent.WaitForIdleAsync(ct).ConfigureAwait(false);
                    break;
                case ConsoleKey.L when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    Redraw();
                    break;
            }
        }
    }

    private async Task<string?> ReadInputAsync(CancellationToken ct)
    {
        _input.Clear();

        _layout.IsReadingInput = true;
        Redraw();

        while (true)
        {
            if (ct.IsCancellationRequested)
            {

                _layout.IsReadingInput = false;
                return null;
            }
            if (!Console.KeyAvailable)
            {
                await Task.Delay(15, ct).ConfigureAwait(false);
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            // Mouse wheel
            if (key.KeyChar == '\x1b')
            {
                var mouse = MouseHandler.ParseSequence();
                if (mouse == MouseHandler.MouseAction.ScrollUp)
                {
                    DoScrollUp(3);
                    continue;
                }
                if (mouse == MouseHandler.MouseAction.ScrollDown)
                {
                    DoScrollDown(3);
                }
                continue;
            }

            // Enter (submit)
            if (key.Key == ConsoleKey.Enter && (key.Modifiers & ConsoleModifiers.Alt) != 0)
            {
                _input.Append('\n');
                Redraw();
                continue;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                string result = _input.Consume();

                _layout.IsReadingInput = false;
                Redraw();
                return result;
            }

            // Escape
            if (key.Key == ConsoleKey.Escape)
            {
                if (_input.IsEmpty)
                {
                    _layout.IsReadingInput = false;
                    return null;
                }
                _input.Clear();
                Redraw();
                continue;
            }

            // Backspace
            if (key.Key == ConsoleKey.Backspace)
            {
                _input.Backspace();
                Redraw();
                continue;
            }

            // Ctrl+L
            if (key.Key == ConsoleKey.L && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                Redraw();
                continue;
            }

            // Ctrl+C
            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                _layout.IsReadingInput = false;
                _stop = true;
                return null;
            }

            // History navigation
            if (key.Key == ConsoleKey.UpArrow)
            {
                _input.NavigateUp();
                Redraw();
                continue;
            }
            if (key.Key == ConsoleKey.DownArrow)
            {
                _input.NavigateDown();
                Redraw();
                continue;
            }

            // Scroll keys
            if (key.Key == ConsoleKey.Home)
            {
                DoScrollToTop();
                continue;
            }
            if (key.Key == ConsoleKey.End)
            {
                DoScrollToBottom();
                continue;
            }
            if (key.Key == ConsoleKey.PageUp)
            {
                DoScrollUp(10);
                continue;
            }
            if (key.Key == ConsoleKey.PageDown)
            {
                DoScrollDown(10);
                continue;
            }

            // Tab autocomplete
            if (key.Key == ConsoleKey.Tab && _input.Length > 0 && _input[0] == '/')
            {
                string current = _input.Text;
                string? match = BuiltinCommands.FirstOrDefault(c => c.StartsWith(current, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    _input.Clear();
                    foreach (char c in match) _input.Append(c);
                    _input.Append(' ');
                    Redraw();
                }
                continue;
            }

            // Regular character (filter control chars that cause terminal bell)
            if (key.KeyChar is >= (char)32 and not (char)127)
            {
                _input.Append(key.KeyChar);
                if (_scroll.IsScrolling) _scroll.Reset();
                Redraw();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scroll delegation
    // ═══════════════════════════════════════════════════════════════

    private void DoScrollUp(int lines)
    {
        int total = ComputeTotalLines();
        _scroll.ScrollUp(lines, total, GetBodyHeight());
        Redraw();
    }

    private void DoScrollDown(int lines)
    {
        _scroll.ScrollDown(lines);
        Redraw();
    }

    private void DoScrollToTop()
    {
        int total = ComputeTotalLines();
        _scroll.ScrollToTop(total, GetBodyHeight());
        Redraw();
    }

    private void DoScrollToBottom()
    {
        _scroll.ScrollToBottom();
        Redraw();
    }

    private int ComputeTotalLines()
    {
        var visible = _chat.Lines.ToList();
        if (_isStreaming)
        {
            if (!string.IsNullOrEmpty(_thinkBuffer)) visible.Add(new ChatState.ChatLine("thinking", _thinkBuffer.Trim()));
            if (!string.IsNullOrEmpty(_streamBuffer)) visible.Add(new ChatState.ChatLine("assistant", _streamBuffer.Trim()));
        }
        int width = 80;
        try { width = Console.WindowWidth; }
        catch
        { /* Non-TTY */
        }
        int maxWidth = Math.Max(20, width - 6);
        return LayoutBuilder.GetTotalVisibleLines(visible.ToArray(), maxWidth);
    }

    private static int GetBodyHeight()
    {
        try { return Math.Max(3, Console.WindowHeight - 8); }
        catch { return 16; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Render
    // ═══════════════════════════════════════════════════════════════

    private void Redraw()
    {
        lock (_renderLock)
        {
            _liveCtx?.UpdateTarget(_layout.Build());
        }
    }

    internal void TestPushInputHistory(string text) => _input.Submit(text);
    internal void TestNavigateHistoryUp() => _input.NavigateUp();
    internal void TestNavigateHistoryDown() => _input.NavigateDown();
    internal void TestScrollUp(int lines) => DoScrollUp(lines);
    internal void TestScrollDown(int lines) => DoScrollDown(lines);
    internal void TestScrollToTop() => DoScrollToTop();
    internal void TestScrollToBottom() => DoScrollToBottom();
}

/// <summary>Render context shim.</summary>
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
