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
using Termina.Hosting;
using Termina.Terminal;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tui.Termina;

/// <summary>
///     Full-screen interactive TUI renderer built on Termina 0.15.0 (R3-reactive MVVM).
///     TEA architecture: all state lives in <see cref="UiStore" />; the
///     <see cref="ChatPage" /> projects <see cref="UiState" /> into Termina nodes
///     and routes keys through <see cref="TerminaTeaBridge" />.
/// </summary>
public sealed class TerminaRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly TerminaRenderContext _context;
    private readonly ILogger<TerminaRenderer> _logger;
    private TerminaTeaBridge? _teaBridge;
    private Func<string, Task>? _slashHandler;

    public TerminaRenderer(ILogger<TerminaRenderer> logger) : base(logger)
    {
        _logger = logger;
        _context = new TerminaRenderContext();
    }

    public override ITuiRenderContext Context => _context;

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try { return base.InitializeAsync(ct); }
        catch (Exception ex) { return Task.FromResult(Result.Failure(ex.Message)); }
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _teaBridge?.Push(@event);
        return base.RenderAsync(@event, ct);
    }

    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _teaBridge = new TerminaTeaBridge(agent, _slashHandler, _logger, ct);
        _teaBridge.DiagnosticsPanel = host?.GetService(typeof(IDiagnosticsPanel)) as IDiagnosticsPanel;

        _logger.LogInformation("Starting Termina host with route /chat");

        var terminaHost = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_teaBridge);
                services.AddTermina("/chat", termina => termina.RegisterRoute<ChatPage, ChatViewModel>("/chat"));
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

        await terminaHost.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        _context.WriteColored(prompt, TuiColor.Green);
        string? line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        _context.Write(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        _context.WriteLine(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        _context.Clear();
        return Task.FromResult(Result.Success());
    }

    public override void Dispose()
    {
        _teaBridge?.Dispose();
        base.Dispose();
    }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event) => false;
}

/// <summary>Render context shim over the console for non-interactive helpers.</summary>
internal sealed class TerminaRenderContext : ITuiRenderContext
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
