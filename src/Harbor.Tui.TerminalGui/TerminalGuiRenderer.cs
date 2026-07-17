using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.Views;
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
    private TerminalGuiScreen? _screen;

    /// <inheritdoc />
    public override ITuiRenderContext Context { get; }

    /// <summary>Creates a new Terminal.Gui renderer.</summary>
    /// <param name="logger">Logger for renderer diagnostics.</param>
    public TerminalGuiRenderer(ILogger<TerminalGuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new TerminalGuiRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler)
        => _slashHandler = handler;

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _logger.LogDebug("RenderAsync: {EventType}", @event.GetType().Name);
        if (_screen is null)
            return base.RenderAsync(@event, ct);

        _screen.ApplyEvent(@event);

        return base.RenderAsync(@event, ct);
    }

    /// <summary>
    ///     Suppress placement-driven rendering — the <see cref="TerminalGuiScreen" /> owns the
    ///     display and renders into its <see cref="TextView" />. The base class would otherwise
    ///     write status/history lines straight to the console and corrupt the Terminal.Gui screen.
    /// </summary>
    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event) => false;

    /// <inheritdoc />
    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting Terminal.Gui app");
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
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = true
        };

        var input = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(),
            Width = Dim.Fill(),
            Height = 1
        };

        var statusBar = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false
        };

        _screen.Attach(output, statusBar);

        output.Title = "conversation";

        input.Title = "you> ";

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

        window.Add(statusBar, output, input);

        // Ensure the input field has focus so the cursor is visible
        input.SetFocus();

        // Render initial state so the screen isn't blank. All agent-driven UI
        // updates are marshalled onto the Terminal.Gui main thread (see
        // TerminalGuiScreen.ApplyEvent) so the screen repaints correctly.
        _screen.Invalidate();

        _app.Run(window);
        _app.Dispose();
        _app = null;
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.WriteColored(prompt, TuiColor.Green);
        var line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    /// <inheritdoc />
    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    { Context.Write(text); return Task.FromResult(Result.Success()); }

    /// <inheritdoc />
    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    { Context.WriteLine(text); return Task.FromResult(Result.Success()); }

    /// <inheritdoc />
    public override Task<Result> ClearAsync(CancellationToken ct = default)
    { Context.Clear(); return Task.FromResult(Result.Success()); }

    /// <inheritdoc />
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
        private TextView? _output;
        private Label? _statusBar;
        private bool _streaming;
        private string _streamBuffer = string.Empty;
        private string _thinkBuffer = string.Empty;
        private bool _flushQueued;
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

        public void Attach(TextView output, Label statusBar) => (_output, _statusBar) = (output, statusBar);

        public void ApplyEvent(AgentEvent @event)
        {
            _logger.LogDebug("ApplyEvent: {EventType}", @event.GetType().Name);
            switch (@event)
            {
                case AgentStartEvent ase:
                    _status = "running";
                    if (_chat.Count == 0)
                        foreach (var m in ase.Messages)
                            if (m is UserMessage u) Add("user", u.Content);
                    else
                        Invalidate();
                    break;
                case MessageStartEvent:
                    _status = "running";
                    _streaming = true;
                    _streamBuffer = string.Empty;
                    _thinkBuffer = string.Empty;
                    Invalidate();
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
                            Invalidate();
                            break;
                    }
                    break;
                case MessageEndEvent:
                    if (!string.IsNullOrEmpty(_thinkBuffer)) Add("thinking", _thinkBuffer.Trim());
                    if (!string.IsNullOrEmpty(_streamBuffer)) Add("assistant", _streamBuffer.Trim());
                    _streamBuffer = string.Empty;
                    _thinkBuffer = string.Empty;
                    _streaming = false;
                    _status = "generating…".Equals(_status, StringComparison.Ordinal) ? "running" : _status;
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
                case CompactionStartedEvent:
                    _status = "compacting";
                    Invalidate();
                    break;
                case CompactionCompletedEvent cc:
                    _status = "running";
                    Add("system", $"compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens");
                    break;
                case AgentErrorEvent err:
                    _status = "error";
                    Add("error", err.Message);
                    break;
                case AgentEndEvent:
                    _status = "idle";
                    Invalidate();
                    break;
            }

            // During streaming, queue the newly arrived delta for a throttled flush
            // instead of touching the TextView on every token. This keeps the main
            // loop free to process key input so typing stays responsive.
            if (_streaming)
            {
                QueueStreamDelta();
            }
        }

        public void Submit(string text)
        {
            _logger.LogInformation("Submitting: {Text}", text);
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text is "exit" or "quit" or ":q") { _app.RequestStop(); return; }

            if (text.StartsWith('/') && _slash is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await _slash(text).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "Slash handler failed"); }
                });
                Add("system", text[1..]);
                return;
            }

            Add("user", text);
            _status = "running";
            Invalidate();
            _ = Task.Run(async () =>
            {
                try { await _agent.PromptAsync(text, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _status = "error";
                    Add("error", ex.Message);
                }
            });
        }

        private void Add(string role, string text)
        {
            _chat.Add((role, text));
            Invalidate();
        }

        /// <summary>
        ///     Marks the screen dirty. Coalesces every state change (chat append, status
        ///     change, stream delta) into a single throttled repaint on the main thread, so a
        ///     burst of agent events can never queue more than one repaint at a time and the
        ///     main loop stays free to process key input — this is what kept typing laggy.
        ///     <see cref="BuildSnapshot" /> already includes the live stream buffers, so the
        ///     single repaint renders both chat and status with no duplication.
        /// </summary>
        internal void Invalidate()
        {
            if (_flushQueued) return;
            _flushQueued = true;
            InvokeOnMainThread(() =>
            {
                _flushQueued = false;
                ApplySnapshot(BuildSnapshot());
            });
        }

        /// <summary>
        ///     Marks the live streaming/thinking buffers dirty and requests a coalesced
        ///     repaint (same path as <see cref="Invalidate" />), so we never call Invoke per
        ///     token.
        /// </summary>
        private void QueueStreamDelta()
        {
            Invalidate();
        }

        private void ApplySnapshot(string snapshot)
        {
            if (_output is not null)
            {
                _output.Text = snapshot;
                _output.MoveEnd();
            }

            // The status bar is the single source of truth for agent state. The window
            // title stays static ("⚓ Harbor") to avoid showing two status lines with
            // potentially divergent state.
            if (_statusBar is not null)
                _statusBar.Text = $"status: {_status} | agent: {_agent.State.Agent.Name.Value} | model: {_agent.State.Agent.Model} | ${_cost:F4} | {_tokensIn}↑ {_tokensOut}↓";
        }

        private void InvokeOnMainThread(Action action)
        {
            if (_app.MainThreadId is { } tid && tid != Environment.CurrentManagedThreadId)
                _app.Invoke(action);
            else
                action();
        }

        private string BuildSnapshot()
        {
            var sb = new StringBuilder();
            foreach (var (role, text) in _chat)
            {
                sb.Append(role switch
                {
                    "user" => $"🧑 {text}\n\n",
                    "assistant" => $"🤖 {text}\n\n",
                    "tool" => $"  🛠 {text}\n",
                    "tool-result" => $"  ✅ {text}\n",
                    "thinking" => $"  🧠 {text}\n",
                    "system" => $"  ℹ️ {text}\n",
                    "error" => $"  ⚠️ {text}\n",
                    _ => $"{text}\n\n"
                });
            }

            // Live streaming/thinking text is appended separately (see FlushStream),
            // so it is not double-rendered here.
            if (!string.IsNullOrEmpty(_thinkBuffer))
                sb.Append($"  🧠 {_thinkBuffer.Trim()}\n");
            if (!string.IsNullOrEmpty(_streamBuffer))
                sb.Append($"🤖 {_streamBuffer.Trim()}\n");

            return sb.ToString();
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

