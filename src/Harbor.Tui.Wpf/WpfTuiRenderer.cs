using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Wpf;

/// <summary>
///     Native Windows WPF renderer. Spins up a dedicated STA thread running the
///     WPF <c>Application</c>, marshals <see cref="UiStore.Changed" /> events to
///     the UI thread via <c>Dispatcher.Invoke</c>, and binds the chat history to
///     an <see cref="ObservableCollection{T}" /> consumed by the XAML ListBox.
/// </summary>
/// <remarks>
///     <para>
///         <b>When to use:</b> you want a real desktop window with mouse, full
///         keyboard editing, copy/paste, XAML designer, hot reload, DPI scaling,
///         and rich FlowDocument markdown — at the cost of Windows-only runtime
///         (~80 MB RSS).
///     </para>
///     <para>
///         Select with <c>HARBOR_TUI=wpf</c>. Requires .NET 10 Desktop Runtime
///         on Windows.
///     </para>
/// </remarks>
public sealed class WpfTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<WpfTuiRenderer> _logger;
    private readonly ObservableCollection<ChatLineViewModel> _lines = new();
    private readonly ManualResetEventSlim _ready = new();
    private System.Windows.Application? _app;
    private ChatWindow? _window;
    private UiStore? _store;
    private TuiEffectHost? _effects;
    private Func<string, Task>? _slashHandler;
    private Thread? _uiThread;

    /// <summary>Construct a <see cref="WpfTuiRenderer" />.</summary>
    /// <param name="logger">Logger.</param>
    public WpfTuiRenderer(ILogger<WpfTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new WpfRenderContext();
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
        try
        {
            _store?.Dispatch(@event);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WPF RenderAsync dispatch failed");
        }
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

        // Spin up the WPF application on a dedicated STA thread.
        _uiThread = new Thread(() =>
        {
            try
            {
                _app = new System.Windows.Application();
                _window = new ChatWindow(_store, _effects, _lines, _logger);
                _store.Changed += OnStoreChanged;
                _app.MainWindow = _window;
                _ready.Set();
                _app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                _app.Run(_window);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WPF application thread crashed");
                _ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Harbor.WPF.UI"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();

        _ready.Wait(ct);

        // Bridge cancellation token to WPF shutdown.
        await using var registration = ct.Register(() =>
        {
            try
            {
                _app?.Dispatcher.Invoke(() => _app?.Shutdown());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to shut down WPF app on cancellation");
            }
        });

        // Block until the WPF app exits (window closed).
        await Task.Run(() => _uiThread.Join(), ct).ConfigureAwait(false);
        return 0;
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        var state = e.State;
        var dispatcher = _app?.Dispatcher;
        if (dispatcher is null) return;

        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                // Reconcile the observable collection against the new state.
                // For simplicity: append any new lines that have appeared.
                while (_lines.Count < state.Lines.Length)
                {
                    var line = state.Lines[_lines.Count];
                    _lines.Add(new ChatLineViewModel(line.Role, line.Text));
                }

                // Update streaming buffer + status bar.
                _window?.UpdateStreaming(state);
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dispatcher.BeginInvoke failed");
        }
    }

    /// <inheritdoc />
    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        // For non-interactive use (ask command), fall back to the console.
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
            _app?.Dispatcher.Invoke(() => _app?.Shutdown());
        }
        catch
        {
            // Best-effort shutdown during dispose.
        }
        base.Dispose();
    }
}

/// <summary>
///     Observable view model for a single chat transcript line in the WPF ListBox.
/// </summary>
public sealed class ChatLineViewModel
{
    /// <summary>The semantic role of the line (user, assistant, tool, etc.).</summary>
    public string Role { get; }

    /// <summary>The display text.</summary>
    public string Text { get; }

    /// <summary>The role's foreground color as a WPF <see cref="Brush" />.</summary>
    public Brush Foreground { get; }

    /// <summary>Construct a chat line view model.</summary>
    public ChatLineViewModel(Harbor.Tui.Abstractions.State.ChatRole role, string text)
    {
        Role = role.ToString().ToLowerInvariant();
        Text = text;
        Foreground = role switch
        {
            Harbor.Tui.Abstractions.State.ChatRole.User => Brushes.Cyan,
            Harbor.Tui.Abstractions.State.ChatRole.Assistant => Brushes.White,
            Harbor.Tui.Abstractions.State.ChatRole.Thinking => Brushes.DarkGray,
            Harbor.Tui.Abstractions.State.ChatRole.Tool => Brushes.CornflowerBlue,
            Harbor.Tui.Abstractions.State.ChatRole.ToolResult => Brushes.MediumSeaGreen,
            Harbor.Tui.Abstractions.State.ChatRole.Error => Brushes.OrangeRed,
            _ => Brushes.LightGray
        };
    }
}

/// <summary>Render context shim — WPF owns its own rendering, this is for non-interactive helpers.</summary>
internal sealed class WpfRenderContext : ITuiRenderContext
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
