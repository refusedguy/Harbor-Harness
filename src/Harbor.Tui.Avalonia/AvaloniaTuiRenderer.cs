using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Avalonia;

/// <summary>
///     Cross-platform desktop renderer built on Avalonia UI. Renders to a native
///     window on Windows (Win32), Linux (X11/Wayland GTK), and macOS (AppKit)
///     using a single Skia-backed scene graph. Same MVU store contract as
///     <c>SpectreTuiRenderer</c> — only the projection differs.
/// </summary>
/// <remarks>
///     <para>
///         <b>When to use:</b> you want a real desktop GUI on every major OS,
///         not just Windows. Lower memory than WPF (~60 MB) and same XAML-like
///         declarative UI + designer + hot reload.
///     </para>
///     <para>
///         Select with <c>HARBOR_TUI=avalonia</c>.
///     </para>
/// </remarks>
public sealed class AvaloniaTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<AvaloniaTuiRenderer> _logger;
    private readonly ObservableCollection<ChatLineViewModel> _lines = new();
    private readonly ManualResetEventSlim _ready = new();
    private UiStore? _store;
    private TuiEffectHost? _effects;
    private Func<string, Task>? _slashHandler;
    private MainWindow? _window;
    private Thread? _uiThread;

    /// <summary>Construct an <see cref="AvaloniaTuiRenderer" />.</summary>
    /// <param name="logger">Logger.</param>
    public AvaloniaTuiRenderer(ILogger<AvaloniaTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new AvaloniaRenderContext();
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
        catch (Exception ex) { _logger.LogError(ex, "Avalonia dispatch failed"); }
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

        _uiThread = new Thread(() =>
        {
            try
            {
                // Standard Avalonia boot: configure the App, use platform detection,
                // and inject our main window after setup completes so we can pass the
                // UiStore + effect host into the window constructor.
                AppBuilder.Configure<App>()
                    .UsePlatformDetect()
                    .LogToTrace()
                    .AfterSetup(_ =>
                    {
                        _window = new MainWindow(_store!, _effects!, _lines, _logger);
                        _store!.Changed += OnStoreChanged;
                        _ready.Set();
                    })
                    .StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnMainWindowClose);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Avalonia lifetime crashed");
                _ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Harbor.Avalonia.UI"
        };
        _uiThread.Start();

        _ready.Wait(ct);

        await using var registration = ct.Register(() =>
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        desktop.Shutdown();
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to shut down Avalonia app on cancellation");
            }
        });

        await Task.Run(() => _uiThread.Join(), ct).ConfigureAwait(false);
        return 0;
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        var state = e.State;
        Dispatcher.UIThread.Post(() =>
        {
            while (_lines.Count < state.Lines.Length)
            {
                var line = state.Lines[_lines.Count];
                _lines.Add(new ChatLineViewModel(line.Role, line.Text));
            }
            _window?.UpdateStreaming(state);
        });
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
        try
        {
            if (_store is not null)
                _store.Changed -= OnStoreChanged;
        }
        catch { }
        base.Dispose();
    }
}

/// <summary>Observable view model for one chat line in the Avalonia ItemsControl.</summary>
public sealed class ChatLineViewModel
{
    /// <summary>The semantic role label (user, assistant, tool, …).</summary>
    public string Role { get; }

    /// <summary>The display text.</summary>
    public string Text { get; }

    /// <summary>Hex color for the role's text, used by the XAML binding.</summary>
    public string ForegroundHex { get; }

    /// <summary>Construct a chat line view model.</summary>
    public ChatLineViewModel(ChatRole role, string text)
    {
        Role = role.ToString().ToLowerInvariant();
        Text = text;
        ForegroundHex = role switch
        {
            ChatRole.User => "#89DCEB",          // cyan
            ChatRole.Assistant => "#CDD6F4",     // base text
            ChatRole.Thinking => "#6C7086",      // dim
            ChatRole.Tool => "#89B4FA",          // blue
            ChatRole.ToolResult => "#A6E3A1",    // green
            ChatRole.Error => "#F38BA8",         // red
            _ => "#BAC2DE"
        };
    }
}

/// <summary>Render context shim — Avalonia owns its own rendering.</summary>
internal sealed class AvaloniaRenderContext : ITuiRenderContext
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
