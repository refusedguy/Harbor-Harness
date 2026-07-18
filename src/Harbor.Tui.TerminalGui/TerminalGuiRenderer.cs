using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Timeout = System.Threading.Timeout;

namespace Harbor.Tui.TerminalGui;
/// <summary>
///     Full-screen interactive TUI renderer built on Terminal.Gui v2.
///     O(1) text assembly, hard 20-FPS throttle, smart scroll, forced dark theme.
/// </summary>
public sealed class TerminalGuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<TerminalGuiRenderer> _logger;
    private IApplication? _app;
    private TerminalGuiScreen? _screen;
    private Func<string, Task>? _slashHandler;
    private IDiagnosticsPanel? _diagnostics;

    public TerminalGuiRenderer(ILogger<TerminalGuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new TerminalGuiRenderContext();
    }

    /// <inheritdoc />
    public override ITuiRenderContext Context { get; }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler)
        => _slashHandler = handler;

    /// <inheritdoc />
    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try { return base.InitializeAsync(ct); }
        catch (Exception ex) { return Task.FromResult(Result.Failure(ex.Message)); }
    }

    /// <inheritdoc />
    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        if (_screen is null) return base.RenderAsync(@event, ct);
        _screen.ApplyEvent(@event);
        return base.RenderAsync(@event, ct);
    }

    /// <inheritdoc />
    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting Terminal.Gui app");
        // Resolve the shared IDiagnosticsPanel (registered by HostBuilder when an
        // interactive TUI is active). Null in tests / non-interactive modes.
        _diagnostics = host?.GetService(typeof(IDiagnosticsPanel)) as IDiagnosticsPanel;
        _app = Application.Create().Init();
        _screen = new TerminalGuiScreen(agent, _slashHandler, _app, _logger, _diagnostics);

        // Жестко задаем дарк-мод тему, чтобы убить дефолтный синий цвет Terminal.Gui
        var darkScheme = new Scheme
        {
            Normal = new Attribute(Color.White, Color.Black),
            Focus = new Attribute(Color.Black, Color.Gray),
            HotNormal = new Attribute(Color.BrightCyan, Color.Black),
            HotFocus = new Attribute(Color.BrightYellow, Color.Gray),
            Disabled = new Attribute(Color.DarkGray, Color.Black)
        };
        SchemeManager.AddScheme("dark", darkScheme);

        var window = new Window
        {
            Title = "⚓ Harbor",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        // 1. Top Header (Hints)
        var header = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            Text = " ⚓ Harbor Chat  |  Enter: Send  |  Shift+Enter: New Line  |  /help: Commands"
        };

        // 2. Chat Output
        var output = new TextView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(5), // Leave space for status + input
            ReadOnly = true,
            WordWrap = true,
            Title = "Conversation"
        };
        output.Border.LineStyle = LineStyle.Rounded;


        // 3. Status Bar
        var statusBar = new Label
        {
            X = 0,
            Y = Pos.Bottom(output),
            Width = Dim.Fill(),
            Height = 1

        };

        // ── 3a. Diagnostics overlay (F12) ───────────────────────────────────
        // Floating overlay covering the chat output region when visible. Hidden
        // by default. Renders the last 10 log entries from the shared
        // IDiagnosticsPanel on every dirty frame.
        var diagnostics = new TextView
        {
            X = 0,
            Y = 1, // below the header
            Width = Dim.Fill(),
            Height = Dim.Fill(2), // leave status bar + input visible
            ReadOnly = true,
            WordWrap = true,
            Title = "📋 Logs (F12 to close)",
            Visible = false
        };
        diagnostics.Border.LineStyle = LineStyle.Rounded;

        // 4. Multi-line Input
        var input = new TextView
        {
            X = 0,
            Y = Pos.Bottom(statusBar),
            Width = Dim.Fill(),
            Height = Dim.Fill(), // Fills the rest down to window border
            WordWrap = true,
            Title = "❯ Input"
        };
        input.Border.LineStyle = LineStyle.Rounded;

        input.KeyDown += (sender, key) =>
        {
            // F12 toggles the diagnostics overlay from anywhere.
            if (key.KeyCode == (KeyCode)ConsoleKey.F12)
            {
                _screen!.ToggleDiagnostics(diagnostics);
                key.Handled = true;
                return;
            }

            if (key.KeyCode == KeyCode.Enter)
            {
                if ((key.KeyCode & KeyCode.ShiftMask) != 0)
                {
                    return; // Let TextView handle Shift+Enter as newline
                }

                string? text = input.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    input.Text = string.Empty;
                    _screen.Submit(text);
                }
                key.Handled = true;
            }
        };

        _screen.Attach(output, statusBar);
        _screen.AttachDiagnostics(diagnostics);

        window.Add(header, output, statusBar, diagnostics, input);
        input.SetFocus();

        _screen.Start();
        _app.Run(window);
        _screen.Stop();

        _app.Dispose();
        _app = null;
        return Task.FromResult(0);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.WriteColored(prompt, TuiColor.Green);
        string? line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        Context.Write(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        Context.WriteLine(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        Context.Clear();
        return Task.FromResult(Result.Success());
    }

    public override void Dispose()
    {
        if (_app is not null)
        {
            try { _app.Dispose(); }
            catch (Exception ex) { _logger.LogError(ex, "Terminal.Gui app dispose failed"); }
            _app = null;
        }
        base.Dispose();
    }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event) => false;

    /// <summary>
    ///     Screen state with hard 20-FPS throttling and zero-allocation streaming.
    /// </summary>
    private sealed class TerminalGuiScreen
    {
        private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(50); // 20 FPS
        private const int DiagnosticsRows = 10;

        private readonly IAgent _agent;
        private readonly IApplication _app;
        private readonly IDiagnosticsPanel? _diagnostics;

        private readonly List<(string Role, string Text)> _chat = new();
        private readonly StringBuilder _finalizedText = new();
        private readonly ILogger _logger;

        // Throttle logic
        private readonly Timer _renderTimer;
        private readonly Func<string, Task>? _slash;

        // Spinner
        private readonly string[] _spinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        private readonly StringBuilder _streamBuffer = new();
        private readonly StringBuilder _thinkBuffer = new();
        private decimal _cost;
        private volatile bool _isDirty;

        private TextView? _output;
        private TextView? _diagnosticsView;
        private bool _diagnosticsVisible;
        private int _spinnerIdx;
        private string _status = "idle";
        private Label? _statusBar;
        private int _tokensIn;
        private int _tokensOut;

        private bool _wasAtBottom = true;

        public TerminalGuiScreen(IAgent agent, Func<string, Task>? slash, IApplication app, ILogger logger,
            IDiagnosticsPanel? diagnostics)
        {
            _agent = agent;
            _slash = slash;
            _app = app;
            _logger = logger;
            _diagnostics = diagnostics;

            _renderTimer = new Timer(_ => InvokeOnMainThread(RenderIfDirty), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start() => _renderTimer.Change(ThrottleInterval, ThrottleInterval);
        public void Stop() => _renderTimer.Change(Timeout.Infinite, Timeout.Infinite);

        public void Attach(TextView output, Label statusBar) => (_output, _statusBar) = (output, statusBar);

        public void AttachDiagnostics(TextView diagnosticsView) => _diagnosticsView = diagnosticsView;

        /// <summary>
        ///     Toggle the visibility of the F12 diagnostics overlay. No-op when
        ///     no <see cref="IDiagnosticsPanel" /> was registered (one-shot tests,
        ///     non-interactive mode). The overlay is a floating TextView that
        ///     covers the chat output region when visible.
        /// </summary>
        public void ToggleDiagnostics(TextView diagnosticsView)
        {
            if (_diagnostics is null)
            {
                _logger.LogWarning("F12 pressed but no IDiagnosticsPanel is registered — ignoring");
                return;
            }
            _diagnosticsVisible = !_diagnosticsVisible;
            diagnosticsView.Visible = _diagnosticsVisible;
            if (_diagnosticsVisible)
                diagnosticsView.SetFocus();
            MarkDirty();
        }

        public void ApplyEvent(AgentEvent @event)
        {
            switch (@event)
            {
                case AgentStartEvent ase:
                    _status = "running";
                    if (_chat.Count == 0)
                        foreach (var m in ase.Messages)
                        {
                            if (m is UserMessage u)
                                Add("user", u.Content);
                        }
                    MarkDirty();
                    break;

                case MessageStartEvent:
                    _status = "running";
                    _streamBuffer.Clear();
                    _thinkBuffer.Clear();
                    MarkDirty();
                    break;

                case MessageUpdateEvent mu:
                    switch (mu.LlmEvent)
                    {
                        case TextDeltaEvent td:
                            _streamBuffer.Append(td.Delta);
                            MarkDirty();
                            break;
                        case ThinkingDeltaEvent thd:
                            _thinkBuffer.Append(thd.Delta);
                            MarkDirty();
                            break;
                        case ToolCallStartEvent tcs: Add("tool", $"→ {tcs.ToolName}"); break;
                        case StepFinishEvent sf when sf.Usage is not null:
                            _tokensIn += sf.Usage.InputTokens;
                            _tokensOut += sf.Usage.OutputTokens;
                            _cost += EstimateCost(sf.Usage.InputTokens, sf.Usage.OutputTokens);
                            MarkDirty();
                            break;
                    }
                    break;

                case MessageEndEvent:
                    if (_thinkBuffer.Length > 0) Add("thinking", _thinkBuffer.ToString().Trim());
                    if (_streamBuffer.Length > 0) Add("assistant", _streamBuffer.ToString().Trim());
                    _streamBuffer.Clear();
                    _thinkBuffer.Clear();
                    MarkDirty();
                    break;

                case ToolExecutionStartEvent tes:
                    string args = tes.Args.GetRawText();
                    Add("tool", string.IsNullOrEmpty(args) || args == "{}"
                        ? $"→ {tes.ToolName}"
                        : $"→ {tes.ToolName}  {args}");
                    break;

                case ToolExecutionEndEvent tee:
                    string label = tee.IsError ? "✗" : "✓";
                    string preview = tee.Result.Output.Length > 600
                        ? tee.Result.Output[..600] + "..." : tee.Result.Output;
                    Add("tool-result", $"{label} {preview.Trim()}");
                    break;

                case CompactionStartedEvent:
                    _status = "compacting";
                    MarkDirty();
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
                    MarkDirty();
                    break;
            }
        }

        public void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text is "exit" or "quit" or ":q")
            {
                _app.RequestStop();
                return;
            }

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

            string formatted = role switch
            {
                "user" => $"🧑 {text}\n\n",
                "assistant" => $"🤖 {text}\n\n",
                "tool" => $"  🛠 {text}\n",
                "tool-result" => $"  ┌─ ✅ Result ──────────────\n  │ {text.Replace("\n", "\n  │ ")}\n  └──────────────────────────\n\n",
                "thinking" => $"  🧠 {text}\n",
                "system" => $"  ℹ️ {text}\n",
                "error" => $"  ┌─ ⚠️ ERROR ────────────────\n  │ {text.Replace("\n", "\n  │ ")}\n  └──────────────────────────\n\n",
                _ => $"{text}\n\n"
            };

            _finalizedText.Append(formatted);
            MarkDirty();
        }

        private void MarkDirty() => _isDirty = true;

        private void RenderIfDirty()
        {
            if (!_isDirty) return;
            _isDirty = false;
            ApplySnapshot();
        }

        private void ApplySnapshot()
        {
            if (_output is null) return;

            // Smart Scroll: Check if user is reading history before mutating text
            _wasAtBottom = _output.Lines == 0 || _output.CurrentRow >= _output.Lines - 2;

            var sb = new StringBuilder();
            sb.Append(_finalizedText);

            if (_thinkBuffer.Length > 0)
                sb.Append($"  🧠 {_thinkBuffer.ToString().Trim()}\n");
            if (_streamBuffer.Length > 0)
                sb.Append($"🤖 {_streamBuffer.ToString().Trim()}\n");

            _output.Text = sb.ToString();

            if (_wasAtBottom) _output.MoveEnd();

            if (_statusBar is not null)
            {
                string spinner = _status == "running" || _status == "generating…"
                    ? _spinnerFrames[_spinnerIdx++ % _spinnerFrames.Length]
                    : "⏳";

                _statusBar.Text = $" {spinner} {_status} | agent: {_agent.State.Agent.Name.Value} | model: {_agent.State.Agent.Model} | ${_cost:F4} | {_tokensIn}↑ {_tokensOut}↓ | F12 logs";
            }

            // Refresh the diagnostics overlay if it is visible.
            if (_diagnosticsVisible && _diagnosticsView is not null && _diagnostics is not null)
            {
                var view = new Harbor.Tui.TerminalGui.Views.DiagnosticsView();
                _diagnosticsView.Text = view.Render(_diagnostics, DiagnosticsRows);
                _diagnosticsView.MoveEnd();
            }
        }

        private void InvokeOnMainThread(Action action)
        {
            if (_app.MainThreadId is { } tid && tid != Environment.CurrentManagedThreadId)
                _app.Invoke(action);
            else
                action();
        }

        private static decimal EstimateCost(int inTok, int outTok)
            => inTok / 1_000_000m * 3m + outTok / 1_000_000m * 15m;
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
