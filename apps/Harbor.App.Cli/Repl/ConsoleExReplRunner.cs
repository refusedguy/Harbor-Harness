using System.Text;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.DesignSystem;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Ui.Framework.Projection;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     CE-4 интерактивный REPL поверх ConsoleEx-движка: второй путь рендера
///     рядом с legacy AnsiTuiRenderer. Владеет полным жизненным циклом экрана
///     (raw-режим, alt-screen, bracketed paste), кадровым циклом и submit-
///     пайплайном композера — тем же, что обслуживал старый рендер
///     (<see cref="SlashCommandDispatcher"/> для <c>/команд</c>,
///     <see cref="IAgentRunner.PromptAsync"/> для промптов).
/// </summary>
/// <remarks>
///     <para>
///         <b>Кадры:</b> event-driven — кадр собирается только после пробуждения
///         (ввод / событие агента / спиннер-тик 80 мс в Running), пустые кадры
///         DiffEngine отбрасывает сам. Порядок кадра — как в golden-тестах:
///         solve → prepare → begin → paint × panels → flush.
///     </para>
///     <para>
///         <b>Ctrl+C:</b> во время хода агента — прерывание через существующий
///         механизм <see cref="IAgentRunner.AbortSource"/>; в idle — двукратное
///         нажатие выходит из REPL (первое печатает подсказку).
///     </para>
/// </remarks>
internal sealed class ConsoleExReplRunner(
    IServiceProvider services,
    IAgent agent,
    Session sessionModel,
    ScreenSession screenSession,
    ChatScreen screen,
    ChatScreenBridge bridge,
    TerminalInputSource inputSource,
    ITerminalModeController modeController,
    ITerminalBackend backend,
    ILogger<ConsoleExReplRunner> logger)
{
    private const string SeqEnterAltScreen = "\x1B[?1049h\x1B[?25l\x1B[?2004h";
    private const string SeqLeaveAltScreen = "\x1B[?2004l\x1B[?25h\x1B[?1049l";

    /// <summary>Idle-Ctrl+C window for the «press again to quit» gesture.</summary>
    private const long QuitGestureWindowMs = 2000;

    /// <summary>Wheel tick ≈ three rows (xterm convention).</summary>
    private const int WheelScrollLines = 3;

    private readonly Channel<object?> _wake = Channel.CreateUnbounded<object?>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Real bus events marshalled onto the frame-loop thread: the
    /// bridge (and thus the whole timeline) is touched from THIS thread only.</summary>
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly StatusViewModel _status = screen.Status.Vm;
    private readonly ComposerController _composer = screen.Composer.Composer;
    private readonly VirtualizedChatTimeline _timeline = screen.Timeline.Timeline;
    private readonly CommandPaletteView _palette = new();
    private readonly LeaderKeyRouter _leader = new();
    private readonly VimComposerMode _vim = new();
    private readonly QuickSwitchSlots _quickSwitch = new();
    private readonly SelectionEngine _selection = new();
    private ThemeFileWatcher? _themeWatcher;

    /// <summary>Token-usage source (null when the host has no tracker —
    /// feed no-ops, the status bar just stays without token segments).</summary>
    private readonly ITokenTracker? _tokens = services.GetService<ITokenTracker>();

    /// <summary>Render-thread pull latch: <see cref="RunPromptAsync"/> flags it
    /// after a turn, the frame loop drains it before painting so all status /
    /// sidebar mutation stays on one thread.</summary>
    private volatile bool _usageDirty;

    /// <summary>Palette commit hand-off: OnCommit is sync (inside HandleKey),
    /// execution happens on the frame loop in <see cref="HandleKeyAsync" />.</summary>
    private CommandItem? _paletteCommitted;

    /// <summary>Leader chord hand-off for async slash commands (same pattern).</summary>
    private string? _leaderSlash;

    /// <summary>Leader digit hand-off: the quick-switch chord resolves into a
    /// session switch on the frame loop (async work can't run inside Bind actions).</summary>
    private char? _quickSwitchChord;

    /// <summary>Theme live-reload hand-off: the watcher's poll timer thread
    /// writes the line, the frame loop drains and appends it — the bridge is
    /// touched from the frame thread only.</summary>
    private volatile string? _themeReloadLine;

    /// <summary>True when a custom theme file exists — it owns the palette and
    /// the OSC 11 auto-detect must not override it (file wins, P3.2 > P3.3).</summary>
    private bool _themeFileApplied;

    private int _timelineViewportH;

    /// <summary>Retry-countdown clock (sprint UI-V2 P6.3): the agent loop owns
    /// the actual retry; the UI mirrors only the expected backoff window —
    /// attempt counter, wall-clock start of the latest transient error, and
    /// the exponential window it should burn down over.</summary>
    private int _retryAttempt;
    private long _retryErrorMs = -1;
    private int _retryTotalSec;

    /// <summary>Stream-retry budget mirrored from AgentLoop's C7 policy —
    /// kept in sync for the countdown's «n/3» display only.</summary>
    private const int MaxStreamRetries = 3;

    // -1, NOT long.MinValue: TickCount64 is non-negative uptime ms, so
    // `now − long.MinValue` overflows to a NEGATIVE value and the first idle
    // Ctrl+C would satisfy the quit-window check immediately (CE-5 PTY-suite
    // finding: paste scenario exited on the FIRST press with no hint).
    private long _lastIdleAbortMs = -1;
    private bool _quitRequested;
    private int? _slashExitCode;

    /// <summary>Cross-thread «prompt submitted, completion event not yet seen» latch
    /// so an stdin EOF cannot race the freshly spawned run into a premature exit.</summary>
    private volatile bool _promptInFlight;

    /// <summary>
    ///     Runs the REPL until quit. Returns the exit code
    ///     (slash <c>/exit</c> wins over the loop's own, mirroring legacy).
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        logger.LogInformation("ConsoleEx REPL starting — composer-driven frames over DiffEngine");
        try
        {
            modeController.Enter();
        }
        catch (PlatformNotSupportedException ex)
        {
            logger.LogWarning(ex, "ConsoleEx unavailable on this terminal/platform");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "ConsoleEx requires a real tty");
            return 1;
        }
        await backend.WriteAsync(Utf8(SeqEnterAltScreen), ct).ConfigureAwait(false);

        // OSC 11 auto-theme probe (sprint UI-V2 P3.3): ask the terminal for its
        // background; the answer surfaces as a Capability event and flips the
        // palette before the first frame. A custom JSON theme (ArmThemeWatcher)
        // applied later always wins over the auto pick.
        await backend.WriteAsync(Utf8(TerminalBackgroundProbe.Query), ct).ConfigureAwait(false);

        await PrintWelcomeAsync().ConfigureAwait(false);
        ArmThemeWatcher();

        var inputTask = inputSource.RunAsync(ct);
        BindLeaderKeys();
        using var busPump = services.GetRequiredService<IEventBus>()
            .Subscribe((evt, _) =>
            {
                _events.Writer.TryWrite(evt);
                _wake.Writer.TryWrite(null);
                return ValueTask.CompletedTask;
            });

        // One spinner heartbeat while the status bar animates; re-armed per frame.
        using var spinnerTimer = new Timer(
            _ => _wake.Writer.TryWrite(null),
            null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            await LoopAsync(inputTask, spinnerTimer, ct).ConfigureAwait(false);
        }
        finally
        {
            _themeWatcher?.Dispose();
            await backend.WriteAsync(Utf8(SeqLeaveAltScreen), CancellationToken.None).ConfigureAwait(false);
            modeController.Restore();
            inputSource.Dispose();
        }

        logger.LogInformation("ConsoleEx REPL ended (quit={Quit}, slashExit={SlashExit})", _quitRequested, _slashExitCode);
        return _slashExitCode ?? 0;
    }

    // ── Frame loop ─────────────────────────────────────────────────────────

    private async Task LoopAsync(Task inputTask, Timer spinnerTimer, CancellationToken ct)
    {
        var inputReader = inputSource.Events;

        await RenderFrameAsync(ct).ConfigureAwait(false);
        ArmSpinner(spinnerTimer);

        // EOF (pipe closed / Ctrl+D) stops INPUT waiting but must not cut off
        // an in-flight turn: the loop exits only when the queue is quiet.
        bool inputClosed = false;
        while (!_quitRequested && !ct.IsCancellationRequested)
        {
            Task<bool> inputWait = inputClosed
                ? Task.FromResult(false)
                : inputReader.WaitToReadAsync(ct).AsTask();
            Task<bool> wakeWait = _wake.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
            var completed = await Task.WhenAny(inputWait, wakeWait).ConfigureAwait(false);

            if (!inputClosed && completed == inputWait && inputWait.Result)
            {
                while (inputReader.TryRead(out var evt))
                {
                    await HandleInputAsync(evt, ct).ConfigureAwait(false);
                }
            }
            else if (completed == wakeWait && wakeWait.Result)
            {
                DrainWake();
            }

            if (!inputClosed && inputTask.IsCompleted)
            {
                inputClosed = true;
            }

            // Agent events replay onto the render thread in arrival order.
            while (_events.Reader.TryRead(out var agentEvt))
            {
                ObserveRetrySignal(agentEvt);
                await bridge.AcceptAsync(agentEvt, ct).ConfigureAwait(false);
            }

            bridge.Tick(Environment.TickCount64);
            UpdateRetryCountdown();
            if (_themeReloadLine is { } themeLine)
            {
                _themeReloadLine = null;
                bridge.AppendSystemLine(themeLine);
            }

            if (_usageDirty)
            {
                _usageDirty = false;
                RefreshUsage();
            }

            await RenderFrameAsync(ct).ConfigureAwait(false);
            ArmSpinner(spinnerTimer);

            if (inputClosed && !_promptInFlight && !agent.State.IsRunning)
            {
                logger.LogInformation("stdin EOF and agent idle — ConsoleEx REPL exiting");
                break;
            }
        }
    }

    /// <summary>Drains pending wakeup signals — one repaint coalesces them all.</summary>
    private void DrainWake()
    {
        int drained = 0;
        while (_wake.Reader.TryRead(out _))
        {
            drained++;
        }

        _ = drained; // count intentionally unused; coalescing is the point
    }

    /// <summary>80 ms heartbeat only while an animation is on screen.</summary>
    private void ArmSpinner(Timer spinnerTimer)
    {
        bool animating = _status.Mode is StatusBarMode.Running or StatusBarMode.Compacting;
        spinnerTimer.Change(animating ? 80 : Timeout.Infinite, Timeout.Infinite);
    }

    private async ValueTask RenderFrameAsync(CancellationToken ct)
    {
        screenSession.CheckAutoSize();
        int cols = screenSession.CurrentCols;
        int rows = screenSession.CurrentRows;
        screen.Tree.Solve(cols, rows);

        Rect tlRect = screen.Timeline.Rect;
        _timelineViewportH = Math.Max(0, tlRect.Height);
        _ = _timeline.PrepareFrame(tlRect.Width > 0 ? tlRect.Width : cols, _timelineViewportH);

        screenSession.BeginFrame();
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(screenSession.Back);
        }

        // Copy-on-select highlight (P6.4): transient Reverse overlay — the
        // next repaint without an active selection clears it for free.
        if (_selection.IsActive)
        {
            _selection.Paint(screenSession.Back);
        }

        if (_palette.Visible)
        {
            int w = Math.Clamp(cols - 8, 20, 56);
            int h = Math.Min(_palette.Results.Count + 5, Math.Max(5, rows - 4));
            int x = Math.Max(0, (cols - w) / 2);
            int y = Math.Max(0, (rows - h) / 3);
            _palette.Paint(screenSession.Back, new Rect(x, y, w, h));
        }

        await screenSession.FlushFrameAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Copy-on-select release (killer features §P6.4): extracts the
    /// selected text from the back buffer and ships it via OSC 52 — terminals
    /// that support the sequence copy it, everything else ignores silently.</summary>
    private async Task FinishSelectionAsync(int releaseX, int releaseY, CancellationToken ct)
    {
        int cols = Math.Max(1, screenSession.CurrentCols);
        int rows = Math.Max(1, screenSession.CurrentRows);
        string? text = _selection.OnRelease(
            releaseX, releaseY, cols, rows,
            (x, y) => x >= 0 && x < cols && y >= 0 && y < rows ? screenSession.Back.Get(x, y) : Cell.Blank);
        if (string.IsNullOrEmpty(text))
        {
            _wake.Writer.TryWrite(null);
            return;
        }

        await backend.WriteAsync(Utf8(Osc52Clipboard.Encode(text)), ct).ConfigureAwait(false);
        bridge.AppendSystemLine($"⧉ скопировано {text.Length} симв.");
        _wake.Writer.TryWrite(null);
    }

    /// <summary>OSC 11 auto-theme (sprint UI-V2 P3.3): bright terminal
    /// background → HarborLight, dark → HarborDark. Runs before any custom
    /// theme file applies, so an explicit theme always wins.</summary>
    private void ApplyAutoTheme(CapabilityEvent report)
    {
        if (_themeFileApplied)
        {
            return; // custom JSON owns the palette — auto-detect stands down
        }

        var background = new RgbColor(
            (byte)Math.Clamp(report.Red, 0, 255),
            (byte)Math.Clamp(report.Green, 0, 255),
            (byte)Math.Clamp(report.Blue, 0, 255));
        var theme = TerminalBackgroundProbe.RelativeLuminance(background)
                    >= TerminalBackgroundProbe.LightLuminanceThreshold
            ? HarborTheme.HarborLight
            : HarborTheme.HarborDark;
        TerminalColorPalette.Apply(theme);
    }

    // ── Input routing ──────────────────────────────────────────────────────

    private async Task HandleInputAsync(InputEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case InputEventKind.Key:
                await HandleKeyAsync(evt.Key, ct).ConfigureAwait(false);
                break;

            case InputEventKind.Capability when evt.Capability.Kind == CapabilityEventKind.Osc11BackgroundReport:
                ApplyAutoTheme(evt.Capability);
                break;

            case InputEventKind.Paste:
                // Paste payload is verbatim by parser contract — never re-parsed.
                _ = _composer.Buffer.InsertText(evt.Paste.Text);
                break;

            case InputEventKind.Resize:
                // Policy lives in ScreenSession: shrink ⇒ erase-in-display next frame.
                screenSession.Resize(Math.Max(1, evt.Resize.Width), Math.Max(1, evt.Resize.Height));
                break;

            case InputEventKind.Mouse when evt.Mouse.Type is MouseEventType.Press or MouseEventType.Click:
                // Click-to-decide: pending approval gates get first claim on a
                // left press; otherwise a press anchors a copy-on-select
                // selection (P6.4) — a plain click selects nothing on release.
                if (bridge.TryRouteApprovalClick(evt.Mouse))
                {
                    // claimed by the approval gate
                }
                else if (evt.Mouse.Type == MouseEventType.Press
                         && _selection.OnPress(evt.Mouse.Column, evt.Mouse.Row, evt.Mouse.Button))
                {
                    _wake.Writer.TryWrite(null);
                }

                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.Drag
                                           && evt.Mouse.Button == MouseButton.Left:
                _selection.OnDrag(evt.Mouse.Column, evt.Mouse.Row);
                _wake.Writer.TryWrite(null); // repaint the growing highlight
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.Release
                                           && evt.Mouse.Button == MouseButton.Left:
                await FinishSelectionAsync(evt.Mouse.Column, evt.Mouse.Row, ct).ConfigureAwait(false);
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.WheelUp:
                _timeline.ScrollBy(-WheelScrollLines);
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.WheelDown:
                _timeline.ScrollBy(WheelScrollLines);
                break;
        }
    }

    private async Task HandleKeyAsync(KeyEvent key, CancellationToken ct)
    {
        // Command palette: ctrl+p toggles; a visible palette claims keys first
        // so Enter/Esc/letters never leak into the approval gate or composer.
        if (key.Key == KeyCode.Char && key.Modifiers == KeyModifiers.Ctrl
            && char.ToLowerInvariant((char)key.Character.Value) == 'p')
        {
            if (_palette.Visible)
            {
                _palette.Hide();
            }
            else
            {
                OpenCommandPalette();
            }

            _wake.Writer.TryWrite(null);
            return;
        }

        if (_palette.Visible && _palette.HandleKey(key))
        {
            if (_paletteCommitted is { } committed)
            {
                _paletteCommitted = null;
                await ExecutePaletteCommandAsync(committed, ct).ConfigureAwait(false);
            }

            _wake.Writer.TryWrite(null);
            return;
        }

        // Leader chords (ctrl+x …): armed router consumes the leader press and
        // the chord; resolved sync actions run here, slash chords and quick-
        // switch digits hand off to the frame loop for async execution.
        if (_leader.HandleKey(key, Environment.TickCount64))
        {
            if (_leaderSlash is { } leaderSlash)
            {
                _leaderSlash = null;
                await ExecutePaletteCommandAsync(new CommandItem(leaderSlash, leaderSlash), ct).ConfigureAwait(false);
            }

            if (_quickSwitchChord is { } chord)
            {
                _quickSwitchChord = null;
                await SwitchToSlotAsync(chord, ct).ConfigureAwait(false);
            }

            _wake.Writer.TryWrite(null);
            return;
        }

        // Permission gate outranks the composer while one is pending: y/n/a/
        // Enter/Esc resolve the card and never leak into prompt editing.
        if (bridge.TryRouteApprovalKey(key))
        {
            _wake.Writer.TryWrite(null);
            return;
        }

        var action = _vim.HandleKey(key, _composer);
        switch (action)
        {
            case ComposerAction.Submitted:
                await SubmitAsync(ct).ConfigureAwait(false);
                break;

            case ComposerAction.Aborted:
                HandleAbortGesture();
                break;

            case ComposerAction.Edited:
                break;

            case ComposerAction.Ignored when key.Key == KeyCode.PageUp:
                _timeline.PageUp(Math.Max(1, _timelineViewportH));
                break;

            case ComposerAction.Ignored when key.Key == KeyCode.PageDown:
                _timeline.PageDown(Math.Max(1, _timelineViewportH));
                break;
        }
    }

    /// <summary>First idle Ctrl+C hints, second one within the window quits.
    /// While the agent runs, Ctrl+C aborts the current turn instead.</summary>
    private void HandleAbortGesture()
    {
        if (agent.State.IsRunning)
        {
            agent.AbortSource.Cancel();
            bridge.AppendSystemLine("^C — прерываю текущий ход…");
            _wake.Writer.TryWrite(null);
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastIdleAbortMs <= QuitGestureWindowMs)
        {
            logger.LogInformation("Second idle Ctrl+C — quitting ConsoleEx REPL");
            _quitRequested = true;
            _wake.Writer.TryWrite(null);
            return;
        }

        _lastIdleAbortMs = now;
        bridge.AppendSystemLine("^C — ещё раз для выхода");
        _wake.Writer.TryWrite(null);
    }

    // ── Command palette ────────────────────────────────────────────────────

    /// <summary>Suggested, arg-less slash commands that are safe to run from the palette.</summary>
    private void OpenCommandPalette()
    {
        _palette.OnCommit = item => _paletteCommitted = item;
        _palette.Show(
        [
            new CommandItem("help", "Help", "slash commands reference", "/help"),
            new CommandItem("sessions", "Sessions", "list stored sessions", "/sessions"),
            new CommandItem("providers", "Providers", "list configured providers", "/providers"),
            new CommandItem("plugins", "Plugins", "reload CS-source plugins", "/plugins"),
            new CommandItem("vim", "Toggle vim mode", "normal/insert editing layer", "<leader>v"),
            new CommandItem("exit", "Exit", "quit harbor", "ctrl+c ×2"),
        ]);
    }

    /// <summary>Routes palette/leader ids that are NOT slash commands.</summary>
    private bool TryRunLocalCommand(string id)
    {
        if (id == "vim")
        {
            ToggleVimMode();
            return true;
        }

        return false;
    }

    private void ToggleVimMode()
    {
        if (_vim.Enabled)
        {
            _vim.Enabled = false;
            _vim.Reset();
        }
        else
        {
            _vim.Enabled = true;
        }

        bridge.AppendSystemLine(_vim.Enabled
            ? "vim: on — Esc = normal, i/a/A/I = insert"
            : "vim: off");
    }

    /// <summary>Leader-chord bindings: scroll anchors, palette, vim, slash
    /// shortcuts, and quick-switch digits 1..9 (recent sessions, sprint UI-V2 P2.2).</summary>
    private void BindLeaderKeys()
    {
        _leader.Bind('g', () => { _timeline.ScrollToTop(); _wake.Writer.TryWrite(null); });
        _leader.Bind('e', () => { _timeline.ScrollToEnd(Math.Max(1, _timelineViewportH)); _wake.Writer.TryWrite(null); });
        _leader.Bind('p', () => { OpenCommandPalette(); _wake.Writer.TryWrite(null); });
        _leader.Bind('v', () => { ToggleVimMode(); _wake.Writer.TryWrite(null); });
        _leader.Bind('h', () => _leaderSlash = "help");
        _leader.Bind('s', () => _leaderSlash = "sessions");
        foreach (char d in "123456789")
        {
            _leader.Bind(d, () => _quickSwitchChord = d);
        }
    }

    /// <summary>
    ///     Quick-switch slot resolution (<c>&lt;leader&gt;1..9</c>, sprint UI-V2
    ///     P2.2): loads the bound session from the store and rebinds the idle
    ///     agent to it. The timeline shows only new traffic from the switch on.
    /// </summary>
    private async Task SwitchToSlotAsync(char chord, CancellationToken ct)
    {
        if (_quickSwitch.Resolve(chord) is not { } sessionId)
        {
            bridge.AppendSystemLine($"⇄ slot {chord}: пусто");
            return;
        }

        if (sessionId == sessionModel.Id)
        {
            bridge.AppendSystemLine("⇄ уже в этой сессии");
            return;
        }

        if (agent.State.IsRunning)
        {
            bridge.AppendSystemLine("⇄ агент занят — сессия не переключена");
            return;
        }

        var store = services.GetService<ISessionStore>();
        if (store is null)
        {
            bridge.AppendSystemLine("⇄ переключение недоступно: хост без хранилища сессий");
            return;
        }

        var loaded = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            bridge.AppendSystemLine("! " + loaded.Error);
            return;
        }

        var definition = services.GetRequiredService<IAgentRegistry>()
            .GetAgent(AgentName.Create(loaded.Value.Agent));
        if (definition.IsFailure)
        {
            bridge.AppendSystemLine("! " + definition.Error);
            return;
        }

        agent.Initialize(loaded.Value, definition.Value);
        sessionModel = loaded.Value;
        _quickSwitch.Push(loaded.Value.Id);
        if (screen.Sidebar is { } sidebar)
        {
            sidebar.State = sidebar.State with
            {
                SessionTitle = loaded.Value.Title,
                SessionId = loaded.Value.Id,
            };
        }

        bridge.AppendSystemLine($"⇄ сессия → {loaded.Value.Title} ({loaded.Value.Id[..Math.Min(8, loaded.Value.Id.Length)]})");
        _wake.Writer.TryWrite(null);
    }

    private async Task ExecutePaletteCommandAsync(CommandItem item, CancellationToken ct)
    {
        if (TryRunLocalCommand(item.Id))
        {
            _wake.Writer.TryWrite(null);
            return;
        }

        string slash = '/' + item.Id;
        var dispatcher = new SlashCommandDispatcher(services.GetRequiredService<ILogger<SlashCommandDispatcher>>());
        try
        {
            await dispatcher.HandleCoreAsync(
                slash, services,
                writer: line => { bridge.AppendSystemLine(line); _wake.Writer.TryWrite(null); },
                reader: prompt =>
                {
                    bridge.AppendSystemLine($"{prompt} — интерактивный ввод недоступен в consoleex, используйте legacy TUI (/exit)");
                    _wake.Writer.TryWrite(null);
                    return Task.FromResult(string.Empty);
                },
                agent, services.GetRequiredService<IAgentRegistry>(),
                services.GetRequiredService<IConfigStore>(),
                services.GetRequiredService<AuthStore>(),
                services.GetRequiredService<IProviderRegistry>(),
                sessionModel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Palette command /{Id} failed", item.Id);
            bridge.AppendSystemLine($"! {ex.Message}");
            _wake.Writer.TryWrite(null);
        }
    }

    // ── Submit pipeline (same commands as the legacy REPL) ────────────────

    private async Task SubmitAsync(CancellationToken ct)
    {
        string text = _composer.Buffer.TakeText().Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.StartsWith('/'))
        {
            var dispatcher = new SlashCommandDispatcher(services.GetRequiredService<ILogger<SlashCommandDispatcher>>());
            var outcome = await dispatcher.HandleCoreAsync(
                text, services,
                writer: line => { bridge.AppendSystemLine(line); _wake.Writer.TryWrite(null); },
                reader: prompt =>
                {
                    bridge.AppendSystemLine($"{prompt} — интерактивный ввод недоступен в consoleex, используйте legacy TUI (/exit)");
                    _wake.Writer.TryWrite(null);
                    return Task.FromResult(string.Empty);
                },
                agent, services.GetRequiredService<IAgentRegistry>(),
                services.GetRequiredService<IConfigStore>(),
                services.GetRequiredService<AuthStore>(),
                services.GetRequiredService<IProviderRegistry>(),
                sessionModel).ConfigureAwait(false);

            if (outcome.ShouldQuit)
            {
                _slashExitCode = outcome.ExitCode;
                _quitRequested = true;
            }

            return;
        }

        // Fresh abort token per prompt (no-op guard while a run is active).
        agent.ResetAbortSource();
        ResetRetryCountdown();
        bridge.NotifyLocalUserMessage();
        screen.Timeline.Timeline.Append(new UserBlock(text));
        _status.Mode = StatusBarMode.Running;
        _wake.Writer.TryWrite(null);

        _promptInFlight = true;
        _ = RunPromptAsync(text, ct);
    }

    /// <summary>Fire-and-forget WITH full observation: every failure lands in
    /// the timeline, cancellation is expected, the wake always fires.</summary>
    private async Task RunPromptAsync(string text, CancellationToken ct)
    {
        try
        {
            var result = await agent.PromptAsync(text, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                // AgentLoop converts an aborted run into Result.Failure (the
                // AgentEndEvent must stay Cancelled=true for renderers), so
                // cancelled turns land HERE, not in the OCE handler below.
                bridge.AppendSystemLine(IsCancellation(result.Error)
                    ? "ход прерван"
                    : "! " + result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            bridge.AppendSystemLine("ход прерван");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Prompt failed in ConsoleEx REPL");
            bridge.AppendSystemLine("! " + ex.Message);
        }
        finally
        {
            _promptInFlight = false;
            if (!agent.State.IsRunning)
            {
                _status.Mode = StatusBarMode.Idle;
                ResetRetryCountdown();
            }

            _usageDirty = true;
            _wake.Writer.TryWrite(null);
        }
    }

    /// <summary>Token/cost footer + sidebar mirror (sprint UI-V2 P6.2/P4):
    /// pulls cumulative usage from the tracker on the render thread. Cost is
    /// preserved when a richer source already reported it.</summary>
    private void RefreshUsage()
    {
        if (_tokens?.GetStats() is not { } stats)
        {
            return;
        }

        _status.SetUsage(stats.TotalInputTokens, stats.TotalOutputTokens);
        if (screen.Sidebar is { } sidebar)
        {
            sidebar.State = sidebar.State with
            {
                TokensIn = stats.TotalInputTokens,
                TokensOut = stats.TotalOutputTokens,
            };
        }
    }

    /// <summary>Retry countdown feed (sprint UI-V2 P6.3): a transient provider
    /// error while the agent runs starts the UI-side backoff clock. The agent
    /// loop retries on its own policy; the status bar only mirrors the window.</summary>
    private void ObserveRetrySignal(AgentEvent evt)
    {
        if (evt is MessageUpdateEvent { LlmEvent: ErrorEvent { Kind: var kind } }
            && ProviderErrors.IsTransient(kind)
            && agent.State.IsRunning)
        {
            _retryAttempt++;
            _retryErrorMs = Environment.TickCount64;
            _retryTotalSec = RetryCountdown.BackoffSeconds(Math.Min(_retryAttempt, MaxStreamRetries));
            _status.Retry = RetryCountdown.Line(_retryAttempt, MaxStreamRetries, _retryTotalSec);
        }
    }

    /// <summary>Recomputes the countdown from wall clock each frame; expires
    /// silently at zero (no timer — frames already fire on the 80 ms heartbeat).</summary>
    private void UpdateRetryCountdown()
    {
        if (_retryErrorMs < 0)
        {
            return;
        }

        int remaining = _retryTotalSec - (int)((Environment.TickCount64 - _retryErrorMs) / 1000);
        if (remaining > 0)
        {
            _status.Retry = RetryCountdown.Line(_retryAttempt, MaxStreamRetries, remaining);
        }
        else
        {
            _retryErrorMs = -1;
            _status.Retry = null;
        }
    }

    /// <summary>Clears the retry window — new prompt or finished turn.</summary>
    private void ResetRetryCountdown()
    {
        _retryAttempt = 0;
        _retryErrorMs = -1;
        _status.Retry = null;
    }

    /// <summary>True when the failure text represents a cancelled/aborted run
    /// (AgentLoop's "…cancelled." family) — rendered as the friendly abort line.</summary>
    private static bool IsCancellation(string error) =>
        error.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("canceled", StringComparison.OrdinalIgnoreCase);

    private async Task PrintWelcomeAsync()
    {        var configResult = await services.GetRequiredService<IConfigStore>()
            .LoadAsync().ConfigureAwait(false);
        string model = configResult.IsSuccess ? configResult.Value.EffectiveModel : "?";
        bridge.AppendSystemLine("Harbor — modular AI coding agent [consoleex]");
        bridge.AppendSystemLine($"model: {model} | ввод — текст, /help — команды, Ctrl+C×2 — выход");

        // Sidebar context (sprint UI-V2 P4): session identity + model before
        // the first frame paints; tokens arrive via RefreshUsage per turn.
        if (screen.Sidebar is { } sidebar)
        {
            sidebar.State = sidebar.State with
            {
                SessionTitle = sessionModel.Title,
                SessionId = sessionModel.Id,
                Model = model,
            };
        }

        // Quick-switch slots (sprint UI-V2 P2.2): the store lists most-recent-
        // first, so slot 1 gets the hottest session. Best-effort — hosts
        // without a session store (smoke tests) just skip slot seeding.
        if (services.GetService<ISessionStore>() is { } store
            && await store.ListAsync(ct: CancellationToken.None).ConfigureAwait(false) is { IsSuccess: true } listed)
        {
            var recent = listed.Value;
            for (int i = 0; i < recent.Count && i < QuickSwitchSlots.Count; i++)
            {
                _quickSwitch.Assign(i + 1, recent[i].Id);
            }
        }

        _wake.Writer.TryWrite(null);
    }

    /// <summary>
    ///     Custom JSON theme with live-reload (sprint UI-V2 P3.2): the file
    ///     applies once at startup, later edits flow through the watcher's
    ///     poll timer (parse failures keep the last applied theme). Path:
    ///     <c>HARBOR_THEME_FILE</c>, else <c>~/.harbor/theme.json</c> when present.
    /// </summary>
    private void ArmThemeWatcher()
    {
        string path = Environment.GetEnvironmentVariable("HARBOR_THEME_FILE")
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          ".harbor", "theme.json");
        if (!File.Exists(path))
        {
            return;
        }

        _themeFileApplied = true;

        var initial = JsonThemeLoader.LoadFile(path);
        if (initial.IsSuccess)
        {
            TerminalColorPalette.Apply(initial.Value);
            bridge.AppendSystemLine($"theme: {initial.Value.Name} ({path})");
        }

        _themeWatcher = new ThemeFileWatcher(
            path,
            onApplied: theme =>
            {
                _themeReloadLine = $"theme: live-reload → {theme.Name}";
                _wake.Writer.TryWrite(null);
            },
            onError: error =>
            {
                _themeReloadLine = "! theme: " + error;
                _wake.Writer.TryWrite(null);
            });
    }

    private static ReadOnlyMemory<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
