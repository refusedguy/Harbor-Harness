using System.Collections.ObjectModel;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;

namespace Harbor.Tui.Maui;

/// <summary>
///     .NET MAUI renderer. Builds the MAUI host on the platform's UI thread,
///     projects <see cref="UiStore" /> snapshots into an
///     <see cref="ObservableCollection{T}" /> consumed by a
///     <c>CollectionView</c>, and forwards input submits to
///     <see cref="TuiEffectHost" />. Targets WinUI, Android, iOS, and Mac Catalyst
///     from one project.
/// </summary>
/// <remarks>
///     <para>
///         <b>When to use:</b> you want Harbor on a phone/tablet or want one
///         project that ships to WinUI + mobile + Mac Catalyst. Mobile layout is
///         single-column, touch-friendly, supports soft keyboard, and uses
///         <c>CollectionView</c> virtualization for long transcripts.
///     </para>
///     <para>
///         Select with <c>HARBOR_TUI=maui</c>. Requires
///         <c>dotnet workload install maui</c>.
///     </para>
/// </remarks>
public sealed class MauiTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<MauiTuiRenderer> _logger;
    private readonly ObservableCollection<ChatLineViewModel> _lines = new();
    private readonly ManualResetEventSlim _ready = new();
    private UiStore? _store;
    private TuiEffectHost? _effects;
    private Func<string, Task>? _slashHandler;
    private MauiApp? _mauiApp;
    private MainPage? _page;

    /// <summary>Construct a <see cref="MauiTuiRenderer" />.</summary>
    /// <param name="logger">Logger.</param>
    public MauiTuiRenderer(ILogger<MauiTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new MauiRenderContext();
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
        catch (Exception ex) { _logger.LogError(ex, "MAUI dispatch failed"); }
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

        _mauiApp = MauiProgram.CreateMauiApp(_store, _effects, _lines, _logger);
        var bridge = _mauiApp.Services.GetRequiredService<MauiBridge>();
        bridge.Ready += _ => _ready.Set();

        // MAUI's run loop is owned by the platform entry point (WinUI
        // Microsoft.UI.Xaml.Application.Start, iOS AppDelegate.FinishedLaunching,
        // Android MainActivity.OnCreate). The skeleton cannot start it portably
        // from a background thread, so block on the cancellation token until the
        // user closes the platform's main window. Production bring-up should
        // invoke the platform-specific launcher here.
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the host is shutting down.
        }

        return 0;
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
        try { _mauiApp?.Dispose(); }
        catch { }
        base.Dispose();
    }
}

/// <summary>Observable view model for one chat line in the MAUI CollectionView.</summary>
public sealed class ChatLineViewModel
{
    /// <summary>Role label (user, assistant, tool, …).</summary>
    public string Role { get; }

    /// <summary>Display text.</summary>
    public string Text { get; }

    /// <summary>Role color as a hex string consumable by XAML binding.</summary>
    public string ForegroundHex { get; }

    /// <summary>Construct a chat line view model.</summary>
    public ChatLineViewModel(ChatRole role, string text)
    {
        Role = role.ToString().ToLowerInvariant();
        Text = text;
        ForegroundHex = role switch
        {
            ChatRole.User => "#89DCEB",
            ChatRole.Assistant => "#CDD6F4",
            ChatRole.Thinking => "#6C7086",
            ChatRole.Tool => "#89B4FA",
            ChatRole.ToolResult => "#A6E3A1",
            ChatRole.Error => "#F38BA8",
            _ => "#BAC2DE"
        };
    }
}

/// <summary>Bridge singleton injected into MAUI DI so MainPage can observe the store.</summary>
public sealed class MauiBridge
{
    /// <summary>Raised once when the MAUI host has built the main page.</summary>
    public event Action<MainPage>? Ready;

    /// <summary>Set the constructed main page and raise <see cref="Ready" />.</summary>
    public void OnReady(MainPage page) => Ready?.Invoke(page);
}

/// <summary>Render context shim — MAUI owns its own rendering.</summary>
internal sealed class MauiRenderContext : ITuiRenderContext
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
