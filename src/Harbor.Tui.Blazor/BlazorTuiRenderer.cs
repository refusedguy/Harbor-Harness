using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Blazor;

/// <summary>
///     Blazor Server renderer. Spins up a Kestrel HTTP host on
/// <c>localhost:5000</c>, opens the default browser, and serves a Razor-based
/// chat UI that streams agent activity to the browser via SignalR (the default
/// Blazor Server transport). Reads from the same <see cref="UiStore" /> as the
/// terminal renderers.
/// </summary>
/// <remarks>
///     <para>
///         <b>When to use:</b> you want to drive Harbor from any browser — on
///         the same machine, on a phone on the same LAN, or remotely over SSH
///         tunnel. Supports CSS, accessibility tooling, screen readers, and
///         copy/paste without terminal escape codes.
///     </para>
///     <para>
///         Select with <c>HARBOR_TUI=blazor</c>.
///     </para>
/// </remarks>
public sealed class BlazorTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<BlazorTuiRenderer> _logger;
    private WebApplication? _app;
    private UiStore? _store;
    private TuiEffectHost? _effects;
    private Func<string, Task>? _slashHandler;

    /// <summary>Construct a <see cref="BlazorTuiRenderer" />.</summary>
    /// <param name="logger">Logger.</param>
    public BlazorTuiRenderer(ILogger<BlazorTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new BlazorRenderContext();
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
        try { _store?.Dispatch(@event); }
        catch (Exception ex) { _logger.LogError(ex, "Blazor dispatch failed"); }
        return base.RenderAsync(@event, ct);
    }

    /// <inheritdoc />
    public async Task<int> RunInteractiveAsync(
        IAgent agent,
        IServiceProvider host,
        CancellationToken ct = default)
    {
        _store = new UiStore();
        _effects = new TuiEffectHost(agent, _store, _slashHandler, ct);
        _store.BindSession(
            agent.State.Agent.Model,
            agent.State.Agent.ProviderId,
            agent.State.Agent.Name.Value);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logger.ToLoggerProvider());

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_effects);
        builder.Services.AddSingleton(_logger);

        _app = builder.Build();
        _app.UseStaticFiles();
        _app.UseAntiforgery();
        _app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();

        var url = "http://localhost:5000";

        // Best-effort browser open. On Linux xdg-open, on macOS open, on Windows start.
        TryOpenBrowser(url);
        _logger.LogInformation("Blazor renderer listening on {Url}", url);

        // RunAsync blocks until the app shuts down (Ctrl+C in console, or
        // cancellation token fires).
        try
        {
            await _app.RunAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the host is shutting down via the cancellation token.
        }
        return 0;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Best-effort — the user can navigate manually.
        }
    }

    /// <inheritdoc />
    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.Write(prompt);
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
    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
        => false;

    /// <inheritdoc />
    public override void Dispose()
    {
        try { _app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch { }
        base.Dispose();
    }
}

internal static class LoggerExtensions
{
    public static ILoggerProvider ToLoggerProvider(this ILogger logger)
    {
        // Wraps a single ILogger in a minimal provider so the ASP.NET Core
        // logging pipeline forwards to the existing Harbor logger.
        return new ForwardingLoggerProvider(logger);
    }

    private sealed class ForwardingLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _forward;
        public ForwardingLoggerProvider(ILogger forward) => _forward = forward;
        public ILogger CreateLogger(string categoryName) => _forward;
        public void Dispose() { }
    }
}

/// <summary>Render context shim — Blazor owns its own rendering.</summary>
internal sealed class BlazorRenderContext : ITuiRenderContext
{
    /// <inheritdoc />
    public int Width => Console.IsOutputRedirected ? 80 : Console.WindowWidth;

    /// <inheritdoc />
    public int Height => Console.IsOutputRedirected ? 24 : Console.WindowHeight;

    /// <inheritdoc />
    public bool SupportsColor => !Console.IsOutputRedirected;

    /// <inheritdoc />
    public void Write(string text) => Console.Write(text);

    /// <inheritdoc />
    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);

    /// <inheritdoc />
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
        => Console.Write($"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m{text}\x1b[0m");

    /// <inheritdoc />
    public void WriteStyled(string text, TuiStyle style) => Console.Write(text);

    /// <inheritdoc />
    public void SetCursorPosition(int row, int col) => Console.SetCursorPosition(col, row);

    /// <inheritdoc />
    public void ClearLine() => Console.Write("\x1b[2K\r");

    /// <inheritdoc />
    public void Clear() => Console.Clear();

    /// <inheritdoc />
    public void HideCursor() => Console.Write("\x1b[?25l");

    /// <inheritdoc />
    public void ShowCursor() => Console.Write("\x1b[?25h");

    /// <inheritdoc />
    public void EnterAlternateScreen() => Console.Write("\x1b[?1049h");

    /// <inheritdoc />
    public void ExitAlternateScreen() => Console.Write("\x1b[?1049l");

    /// <inheritdoc />
    public void Flush() => Console.Out.Flush();
}
