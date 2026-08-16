using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
namespace Harbor.Tui.RazorConsole;

/// <summary>
///     Full-screen interactive TUI renderer built on the RazorConsole.Core
///     Blazor-for-the-console framework. TEA architecture: all state lives in
///     <see cref="UiStore" />; the <see cref="ChatTui" /> component projects
///     <see cref="UiState" /> into Blazor markup and routes input through
///     <see cref="RazorConsoleTeaBridge" />.
/// </summary>
public sealed class RazorConsoleRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<RazorConsoleRenderer> _logger;
    private RazorConsoleTeaBridge? _teaBridge;
    private IHost? _host;
    private Func<string, Task>? _slashHandler;

    public RazorConsoleRenderer(ILogger<RazorConsoleRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new RazorConsoleRenderContext();
    }

    /// <inheritdoc />
    public override ITuiRenderContext Context { get; }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler)
        => _slashHandler = handler;

    /// <inheritdoc />
    public override Task<Result> InitializeAsync(CancellationToken ct = default)
        => base.InitializeAsync(ct);

    /// <inheritdoc />
    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _teaBridge?.Push(@event);
        return base.RenderAsync(@event, ct);
    }

    /// <inheritdoc />
    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting RazorConsole host");
        _teaBridge = new RazorConsoleTeaBridge(agent, _slashHandler, _logger, ct);
        _teaBridge.DiagnosticsPanel = host?.GetService(typeof(IDiagnosticsPanel)) as IDiagnosticsPanel;

        _host = new HostBuilder()
            .UseRazorConsole<ChatTui>()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_teaBridge);
                services.Configure<ConsoleAppOptions>(ConfigureConsoleAppOptions);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

        try
        {
            await _host.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "RazorConsole host stopped");
        }

        return 0;
    }

    /// <inheritdoc />
    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.WriteColored(prompt, TuiColor.Green);
        string? line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    /// <inheritdoc />
    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        Context.Write(text);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        Context.WriteLine(text);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        Context.Clear();
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _host?.Dispose();
        _host = null;
        base.Dispose();
    }

    /// <inheritdoc />
    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event) => false;

    private static void ConfigureConsoleAppOptions(ConsoleAppOptions o)
    {
        o.AutoClearConsole = true;
        o.EnableTerminalResizing = true;
    }
}

/// <summary>Render context shim over the console for non-interactive helpers.</summary>
internal sealed class RazorConsoleRenderContext : ITuiRenderContext
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
