using System.Text;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Tui.TerminalGui.Views;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Harbor.Tui.TerminalGui;

/// <summary>
///     Full-screen interactive TUI renderer built on Terminal.Gui v2.
///     TEA architecture: all state lives in <see cref="UiStore" />; this class
///     only projects <see cref="UiState" /> into Terminal.Gui widgets and routes
///     keys through <see cref="TerminalGuiTeaBridge" />.
/// </summary>
public sealed class TerminalGuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<TerminalGuiRenderer> _logger;
    private IApplication? _app;
    private TerminalGuiTeaBridge? _bridge;
    private IDiagnosticsPanel? _diagnostics;
    private Func<string, Task>? _slashHandler;

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
        _bridge?.Push(@event);
        return base.RenderAsync(@event, ct);
    }

    /// <inheritdoc />
    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting Terminal.Gui app");
        _diagnostics = host?.GetService(typeof(IDiagnosticsPanel)) as IDiagnosticsPanel;
        _bridge = new TerminalGuiTeaBridge(agent, _slashHandler, _logger, ct);

        _app = Application.Create().Init("Console");

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

        var header = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };

        var output = new TextView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(5),
            ReadOnly = true,
            WordWrap = true,
            Title = "Conversation"
        };
        output.Border.LineStyle = LineStyle.Rounded;

        var statusBar = new Label
        {
            X = 0,
            Y = Pos.Bottom(output),
            Width = Dim.Fill(),
            Height = 1
        };

        var diagnostics = new TextView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = true,
            Title = "📋 Logs (F12 to close)",
            Visible = false
        };
        diagnostics.Border.LineStyle = LineStyle.Rounded;

        var input = new TextView
        {
            X = 0,
            Y = Pos.Bottom(statusBar),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
            Title = "❯ Input"
        };
        input.Border.LineStyle = LineStyle.Rounded;

        bool diagnosticsVisible = false;
        var chatView = new ChatView();
        var statusBarView = new Views.StatusBarView();
        var viewport = new TerminalGuiUiViewport(
            chatView, statusBarView, output, header, statusBar, input);
        var projector = new DefaultUiProjector();

        input.KeyDown += (sender, key) =>
        {
            var info = ToConsoleKeyInfo(key);
            var effect = _bridge!.HandleKey(info);

            if (effect is TuiEffect.QuitApp)
            {
                _app!.RequestStop();
                key.Handled = true;
                return;
            }

            if (info.Key == ConsoleKey.F12)
            {
                diagnosticsVisible = !diagnosticsVisible;
                diagnostics.Visible = diagnosticsVisible;
                if (diagnosticsVisible)
                {
                    if (_diagnostics is not null)
                    {
                        var view = new DiagnosticsView();
                        diagnostics.Text = view.Render(_diagnostics);
                        diagnostics.MoveEnd();
                    }
                    diagnostics.SetFocus();
                }
                else
                {
                    input.SetFocus();
                }
                key.Handled = true;
                return;
            }

            _bridge.Effects.Run(effect);
            key.Handled = true;
        };

        window.Add(header, output, statusBar, diagnostics, input);
        input.SetFocus();

        bool[] dirty = [true];
        _bridge.Store.Changed += (_, _) => dirty[0] = true;

        void RenderFrame()
        {
            var screen = projector.Project(_bridge!.Store.State);
            viewport.Apply(screen);

            if (screen.Header.IsAgentRunning && screen.Transcript.StreamingBlockId != null)
                output.MoveEnd();

            if (screen.Header.ShouldQuit)
                _app!.RequestStop();
        }

        object? timeoutToken = _app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            try
            {
                if (!dirty[0]) return true;
                dirty[0] = false;
                RenderFrame();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RenderFrame failed");
            }
            return true;
        });

        _logger.LogInformation("Terminal.Gui timer created, starting Run()");
        _app.Run(window);
        if (timeoutToken is not null)
            _app.RemoveTimeout(timeoutToken);
        _app.Dispose();
        _app = null;

        return 0;
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

    private static ConsoleKeyInfo ToConsoleKeyInfo(Key key)
    {
        var baseCode = key.KeyCode & ~(KeyCode.ShiftMask | KeyCode.CtrlMask | KeyCode.AltMask);
        var consoleKey = (ConsoleKey)(int)baseCode;
        char keyChar = key.AsRune != default ? (char)key.AsRune.Value : '\0';
        return new ConsoleKeyInfo(keyChar, consoleKey, key.IsShift, key.IsAlt, key.IsCtrl);
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
