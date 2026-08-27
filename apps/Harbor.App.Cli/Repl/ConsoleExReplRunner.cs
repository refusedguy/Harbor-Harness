using System.Text;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.Tui.ConsoleEx.Input;
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

    private int _timelineViewportH;
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
        await PrintWelcomeAsync().ConfigureAwait(false);

        var inputTask = inputSource.RunAsync(ct);
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
                await bridge.AcceptAsync(agentEvt, ct).ConfigureAwait(false);
            }

            bridge.Tick(Environment.TickCount64);
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

        await screenSession.FlushFrameAsync(ct).ConfigureAwait(false);
    }

    // ── Input routing ──────────────────────────────────────────────────────

    private async Task HandleInputAsync(InputEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case InputEventKind.Key:
                await HandleKeyAsync(evt.Key, ct).ConfigureAwait(false);
                break;

            case InputEventKind.Paste:
                // Paste payload is verbatim by parser contract — never re-parsed.
                _ = _composer.Buffer.InsertText(evt.Paste.Text);
                break;

            case InputEventKind.Resize:
                // Policy lives in ScreenSession: shrink ⇒ erase-in-display next frame.
                screenSession.Resize(Math.Max(1, evt.Resize.Width), Math.Max(1, evt.Resize.Height));
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
        // Permission gate outranks the composer while one is pending: y/n/a/
        // Enter/Esc resolve the card and never leak into prompt editing.
        if (bridge.TryRouteApprovalKey(key))
        {
            _wake.Writer.TryWrite(null);
            return;
        }

        var action = _composer.HandleKey(key);
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
            }

            _wake.Writer.TryWrite(null);
        }
    }

    /// <summary>True when the failure text represents a cancelled/aborted run
    /// (AgentLoop's "…cancelled." family) — rendered as the friendly abort line.</summary>
    private static bool IsCancellation(string error) =>
        error.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("canceled", StringComparison.OrdinalIgnoreCase);

    private async Task PrintWelcomeAsync()
    {
        var configResult = await services.GetRequiredService<IConfigStore>()
            .LoadAsync().ConfigureAwait(false);
        string model = configResult.IsSuccess ? configResult.Value.EffectiveModel : "?";
        bridge.AppendSystemLine("Harbor — modular AI coding agent [consoleex]");
        bridge.AppendSystemLine($"model: {model} | ввод — текст, /help — команды, Ctrl+C×2 — выход");
        _wake.Writer.TryWrite(null);
    }

    private static ReadOnlyMemory<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
