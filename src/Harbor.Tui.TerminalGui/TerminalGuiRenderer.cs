using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.Logging;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Harbor.Tui.TerminalGui;

/// <summary>
///     Full-screen interactive TUI renderer built on Terminal.Gui v2. Owns the
///     Terminal.Gui application loop via <see cref="RunInteractiveAsync" />, rendering
///     chat history into a read-only <see cref="TextView" /> and reading user input
///     from a <see cref="TextField" /> at the bottom of the screen.
/// </summary>
public sealed class TerminalGuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<TerminalGuiRenderer> _logger;
    private Func<string, Task>? _slashHandler;
    private IApplication? _app;
    private TextView? _output;
    private TerminalGuiScreen? _screen;

    public override ITuiRenderContext Context { get; }

    public TerminalGuiRenderer(ILogger<TerminalGuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new TerminalGuiRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler)
        => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _screen?.ApplyEvent(@event);
        return base.RenderAsync(@event, ct);
    }

    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _app = Application.Create().Init();
        _screen = new TerminalGuiScreen(agent, _slashHandler, _app, _logger);

        var window = new Window
        {
            Title = "⚓ Harbor",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        var output = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true
        };

        var input = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill()
        };

        _output = output;
        _screen.Attach(output);

        output.Title = "conversation";

        input.KeyDown += (sender, key) =>
        {
            if (key == Key.Enter)
            {
                var text = input.Text.ToString() ?? string.Empty;
                input.Text = string.Empty;
                _screen.Submit(text);
                key.Handled = true;
            }
        };

        window.Add(output, input);
        _app.Run(window);
        _app.Dispose();
        _app = null;
        return Task.FromResult(0);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.WriteColored(prompt, TuiColor.Green);
        var line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    { Context.Write(text); return Task.FromResult(Result.Success()); }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    { Context.WriteLine(text); return Task.FromResult(Result.Success()); }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    { Context.Clear(); return Task.FromResult(Result.Success()); }

    public override void Dispose()
    {
        if (_app is not null)
        {
            try
            {
                _app.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Terminal.Gui app dispose failed");
            }

            _app = null;
        }

        base.Dispose();
    }

    /// <summary>The chat screen - owns the chat state and event handling logic.</summary>
    private sealed class TerminalGuiScreen
    {
        private readonly IAgent _agent;
        private readonly Func<string, Task>? _slash;
        private readonly IApplication _app;
        private readonly ILogger _logger;
        private readonly List<(string Role, string Text)> _chat = new();
        private readonly StringBuilder _buffer = new();
        private TextView? _output;
        private bool _streaming;
        private string _streamBuffer = string.Empty;
        private string _thinkBuffer = string.Empty;
        private decimal _cost;
        private int _tokensIn;
        private int _tokensOut;
        private string _status = "idle";

        public TerminalGuiScreen(IAgent agent, Func<string, Task>? slash, IApplication app, ILogger logger)
        {
            _agent = agent;
            _slash = slash;
            _app = app;
            _logger = logger;
        }

        public void Attach(TextView output) => _output = output;

        public void ApplyEvent(AgentEvent @event)
        {
            switch (@event)
            {
                case AgentStartEvent ase:
                    _status = "running";
                    if (_chat.Count == 0)
                        foreach (var m in ase.Messages)
                            if (m is UserMessage u) Add("user", u.Content);
                    break;
                case MessageStartEvent:
                    _status = "running";
                    _streaming = true;
                    _streamBuffer = string.Empty;
                    _thinkBuffer = string.Empty;
                    break;
                case MessageUpdateEvent mu:
                    switch (mu.LlmEvent)
                    {
                        case TextDeltaEvent td: _streamBuffer += td.Delta; break;
                        case ThinkingDeltaEvent thd: _thinkBuffer += thd.Delta; break;
                        case ToolCallStartEvent tcs: Add("tool", $"→ {tcs.ToolName}"); break;
                        case StepFinishEvent sf when sf.Usage is not null:
                            _tokensIn += sf.Usage.InputTokens;
                            _tokensOut += sf.Usage.OutputTokens;
                            _cost += EstimateCost(sf.Usage.InputTokens, sf.Usage.OutputTokens);
                            break;
                    }
                    break;
                case MessageEndEvent:
                    if (!string.IsNullOrEmpty(_thinkBuffer)) Add("thinking", _thinkBuffer.Trim());
                    if (!string.IsNullOrEmpty(_streamBuffer)) Add("assistant", _streamBuffer.Trim());
                    _streamBuffer = string.Empty;
                    _thinkBuffer = string.Empty;
                    _streaming = false;
                    break;
                case ToolExecutionStartEvent tes:
                    var args = tes.Args.GetRawText();
                    Add("tool", string.IsNullOrEmpty(args) || args == "{}"
                        ? $"→ {tes.ToolName}"
                        : $"→ {tes.ToolName}  {args}");
                    break;
                case ToolExecutionEndEvent tee:
                    var label = tee.IsError ? "✗" : "✓";
                    var preview = tee.Result.Output.Length > 600
                        ? tee.Result.Output[..600] + "..." : tee.Result.Output;
                    Add("tool-result", $"{label} {preview.Trim()}");
                    break;
                case CompactionStartedEvent: _status = "compacting"; break;
                case CompactionCompletedEvent cc:
                    _status = "running";
                    Add("system", $"compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens");
                    break;
                case AgentErrorEvent err:
                    _status = "error";
                    Add("error", err.Message);
                    break;
                case AgentEndEvent: _status = "idle"; break;
            }

            if (_streaming && _streamBuffer.Length > 0)
            {
                RenderStreaming();
            }
        }

        public void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text is "exit" or "quit" or ":q") { _app.RequestStop(); return; }

            if (text.StartsWith('/') && _slash is not null)
            {
                try
                {
                    _slash(text).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Slash handler failed");
                }

                Add("system", text[1..]);
                return;
            }

            Add("user", text);
            _status = "running";
            try
            {
                _agent.PromptAsync(text, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _status = "error";
                Add("error", ex.Message);
            }
        }

        private void Add(string role, string text)
        {
            _chat.Add((role, text));
            var prefix = role switch
            {
                "user" => $"\n\uD83D\uDC64 {text}\n",
                "assistant" => $"\n\U0001F916 {text}\n",
                "tool" => $"  \U0001F6E0 {text}\n",
                "tool-result" => $"  \u2705 {text}\n",
                "thinking" => $"  \U0001F9E0 {text}\n",
                "system" => $"  \u2139\uFE0F {text}\n",
                "error" => $"  \u26A0\uFE0F {text}\n",
                _ => $"{text}\n"
            };

            _buffer.Append(prefix);
            RenderFull();
        }

        private void RenderStreaming()
        {
            var header = $"\n\U0001F916 ";
            var body = _streamBuffer.TrimEnd();
            _output?.InsertText(header + body + "\n");
            _output?.MoveEnd();
        }

        private void RenderFull()
        {
            var sb = new StringBuilder();
            sb.Append($"status: {_status} | agent: {_agent.State.Agent.Name.Value} | model: {_agent.State.Agent.Model} | ${_cost:F4} | {_tokensIn}\u2191 {_tokensOut}\u2193\n\n");
            foreach (var (role, text) in _chat)
            {
                sb.Append(role switch
                {
                    "user" => $"\uD83D\uDC64 {text}\n",
                    "assistant" => $"\U0001F916 {text}\n",
                    "tool" => $"  \U0001F6E0 {text}\n",
                    "tool-result" => $"  \u2705 {text}\n",
                    "thinking" => $"  \U0001F9E0 {text}\n",
                    "system" => $"  \u2139\uFE0F {text}\n",
                    "error" => $"  \u26A0\uFE0F {text}\n",
                    _ => $"{text}\n"
                });
            }

            var snapshot = sb.ToString();
            if (_output is not null)
            {
                _output.Text = snapshot;
                _output.MoveEnd();
            }
        }

        private static decimal EstimateCost(int inTok, int outTok)
            => (decimal)inTok / 1_000_000m * 3m + (decimal)outTok / 1_000_000m * 15m;
    }
}

/// <summary>Render context shim over the console for non-interactive helpers.</summary>
internal sealed class TerminalGuiRenderContext : ITuiRenderContext
{
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public bool SupportsColor => true;
    public void Write(string text) => Console.Write(text);
    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
        => Console.Write($"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m{text}\x1b[0m");
    public void WriteStyled(string text, TuiStyle style) => Console.Write(text);
    public void SetCursorPosition(int row, int col) => Console.SetCursorPosition(col, row);
    public void ClearLine() => Console.Write("\x1b[2K\r");
    public void Clear() => Console.Write("\x1b[2J\x1b[H");
    public void HideCursor() => Console.Write("\x1b[?25l");
    public void ShowCursor() => Console.Write("\x1b[?25h");
    public void EnterAlternateScreen() => Console.Write("\x1b[?1049h");
    public void ExitAlternateScreen() => Console.Write("\x1b[?1049l");
    public void Flush() => Console.Out.Flush();
}
