using System.Text;
using System.Threading.Channels;
using System.Linq;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Commands;
using Harbor.Application.Configuration;
using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     CE-4 интерактивный REPL поверх CellForge-движка: второй путь рендера
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
internal sealed class CellForgeReplRunner(
    IServiceProvider services,
    IAgent agent,
    Session sessionModel,
    ScreenSession screenSession,
    ChatScreen screen,
    ChatScreenBridge bridge,
    TerminalInputSource inputSource,
    ITerminalModeController modeController,
    ITerminalBackend backend,
    ILogger<CellForgeReplRunner> logger)
{
    private const string SeqEnterAltScreen = "\x1B[?1049h\x1B[?25l\x1B[?2004h\x1B[?1000h\x1B[?1002h\x1B[?1006h";
    private const string SeqLeaveAltScreen = "\x1B[?2004l\x1B[?25h\x1B[?1049l\x1B[?1006l\x1B[?1002l\x1B[?1000l";

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
    private SlashCommandDispatcher? _slashDispatcher;

    private SlashCommandDispatcher GetDispatcher()
    {
        _slashDispatcher ??= new SlashCommandDispatcher(
            services.GetRequiredService<ILogger<SlashCommandDispatcher>>());
        return _slashDispatcher;
    }
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

    /// <summary>Optional async action for palette commits (used by slash commands
    /// that need custom handling instead of the default ExecutePaletteCommandAsync).</summary>
    private Func<CommandItem, Task>? _paletteAction;

    /// <summary>Stack of parent async actions restored when a drill-down frame is popped.</summary>
    private readonly Stack<Func<CommandItem, Task>?> _paletteActionStack = new();

    /// <summary>Async action for the current input frame (string = submitted value).</summary>
    private Func<string, Task>? _paletteInputAction;

    /// <summary>Stack of parent input actions restored when a drill-down frame is popped.</summary>
    private readonly Stack<Func<string, Task>?> _paletteInputActionStack = new();

    /// <summary>Leader chord hand-off for async slash commands (same pattern).</summary>
    private string? _leaderSlash;

    /// <summary>Leader digit hand-off: the quick-switch chord resolves into a
    /// session switch on the frame loop (async work can't run inside Bind actions).</summary>
    private char? _quickSwitchChord;

    /// <summary>Theme live-reload hand-off: the watcher's poll timer thread
    /// writes the line, the frame loop drains and appends it — the bridge is
    /// touched from the frame thread only.</summary>
    private volatile string? _themeReloadLine;

    /// <summary>Inline-image protocol for this session (osc-sprint §1337):
    /// detected once at startup — kitty → APC, iTerm2/WezTerm/Konsole/mintty
    /// → OSC 1337, everything else keeps the text description card.</summary>
    private readonly InlineImageKind _inlineImage = InlineImageProbe.Detect();

    /// <summary>Desktop-notification transport (osc-sprint §777): Osc99 once
    /// the startup probe answer arrives; otherwise the 777 family via env
    /// detection, resolved lazily at first fire. None suppresses entirely.</summary>
    private DesktopNotifyKind _notify;

    /// <summary>Long-turn notify hand-off: the 30 s timer thread stages the
    /// sequence, the frame loop writes it — the backend stays single-threaded
    /// (same discipline as <see cref="_themeReloadLine" />).</summary>
    private volatile string? _pendingNotifySequence;

    private Timer? _notifyTimer;

    /// <summary>True when a custom theme file exists — it owns the palette and
    /// the OSC 11 auto-detect must not override it (file wins, P3.2 > P3.3).</summary>
    private bool _themeFileApplied;

    private int _timelineViewportH;

    /// <summary>Partial-scan damage ledger (renderer-moat sprint): frames
    /// triggered by user input or event-driven state changes repaint via the
    /// plain full scan; only quiet animation frames (spinner, gate pulse,
    /// entrance fades) narrow the diff to hinted rects.</summary>
    private bool _broadDamageNextFrame = true;
    private readonly Rect[] _fxDamageScratch = new Rect[VirtualizedChatTimeline.MaxFxDamage];

    /// <summary>Post-render glow slots (renderer-moat T3): preallocated effect
    /// instances + the frame's glow-region scratch — the pipeline itself lives
    /// on the <see cref="ScreenSession"/> and is refreshed per frame.</summary>
    private readonly GlowEffect[] _glowEffects = new GlowEffect[VirtualizedChatTimeline.MaxFxDamage];
    private readonly GlowRegion[] _glowScratch = new GlowRegion[VirtualizedChatTimeline.MaxFxDamage];

    /// <summary>Width the sidebar spring policy was last applied for (P1.6
    /// spring resize): 0 until the first frame so cold start snaps instead
    /// of replaying the static geometry as motion.</summary>
    private int _sidebarPolicyCols;

    /// <summary>False until the first policy application — cold start snaps
    /// (static solve already matches the targets), only real width changes
    /// afterwards are animated.</summary>
    private bool _sidebarPolicyWasApplied;

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
        logger.LogInformation("CellForge REPL starting — composer-driven frames over DiffEngine");
        try
        {
            modeController.Enter();
        }
        catch (PlatformNotSupportedException ex)
        {
            logger.LogWarning(ex, "CellForge unavailable on this terminal/platform");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "CellForge requires a real tty");
            return 1;
        }
        await backend.WriteAsync(Utf8(SeqEnterAltScreen), ct).ConfigureAwait(false);

        // OSC 11 auto-theme probe (sprint UI-V2 P3.3): ask the terminal for its
        // background; the answer surfaces as a Capability event and flips the
        // palette before the first frame. A custom JSON theme (ArmThemeWatcher)
        // applied later always wins over the auto pick.
        await backend.WriteAsync(Utf8(TerminalBackgroundProbe.Query), ct).ConfigureAwait(false);

        // OSC 99 notification capability probe (osc-sprint §777): terminals
        // without the protocol ignore the query silently — answers flip the
        // notify transport to kitty's native family; the urxvt 777 family is
        // the env-detected fallback at fire time.
        await backend.WriteAsync(Utf8(TerminalQueries.Osc99NotifyProbe), ct).ConfigureAwait(false);

        await PrintWelcomeAsync().ConfigureAwait(false);
        ArmThemeWatcher();

        var inputTask = inputSource.RunAsync(ct);
        BindLeaderKeys();

        _palette.FramePopped += (_, _) =>
        {
            _paletteAction = _paletteActionStack.Count > 0 ? _paletteActionStack.Pop() : null;
            _paletteInputAction = _paletteInputActionStack.Count > 0 ? _paletteInputActionStack.Pop() : null;
        };

        // Renderer-moat T3: approval-gate warn pulses bloom through the
        // post-render effect pipeline (diff → transform → SGR encode).
        _timeline.EnablePostFx = true;

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
            _notifyTimer?.Dispose();
            _themeWatcher?.Dispose();
            try
            {
                await backend.WriteAsync(Utf8(SeqLeaveAltScreen), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Terminal may already be gone (kill, closed pipe). The mode
                // restore below must still run — a skipped Restore() leaves
                // Windows consoles stuck in raw/VT mode after exit.
                logger.LogWarning(ex, "Leave-alt-screen write failed — restoring console mode anyway");
            }

            modeController.Restore();
            inputSource.Dispose();
        }

        logger.LogInformation("CellForge REPL ended (quit={Quit}, slashExit={SlashExit})", _quitRequested, _slashExitCode);
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

            // Inline images (osc-sprint §1337): attachments drained on the
            // frame thread — the only thread allowed to write the backend.
            while (bridge.TryTakePendingImage(out var image))
            {
                await EmitInlineImageAsync(image, ct).ConfigureAwait(false);
            }

            bridge.Tick(Environment.TickCount64);
            UpdateRetryCountdown();
            if (_themeReloadLine is { } themeLine)
            {
                _themeReloadLine = null;
                bridge.AppendSystemLine(themeLine);
                _broadDamageNextFrame = true; // live theme swap re-projects every style
            }

            // Long-turn notification (osc-sprint §777): staged by the timer
            // thread, written here where backend ownership lives.
            if (_pendingNotifySequence is { } notifySeq)
            {
                _pendingNotifySequence = null;
                await backend.WriteAsync(Utf8(notifySeq), ct).ConfigureAwait(false);
                bridge.AppendSystemLine("⏱ ход идёт дольше 30 с — уведомление отправлено");
                _broadDamageNextFrame = true;
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
                logger.LogInformation("stdin EOF and agent idle — CellForge REPL exiting");
                break;
            }
        }
    }

    /// <summary>
    /// Spring-driven sidebar resize policy (P1.6, Bubble Tea harmonica):
    /// re-pins the 42-column sidebar with spring physics on every width
    /// change, and on a crossing of the auto-show threshold glides the
    /// show/hide transition over frames — min-width spring to/from 0 plus
    /// the timeline/sidebar ratio — instead of the solver's binary collapse.
    /// Idempotent per width: settled springs make repeat calls no-ops.
    /// </summary>
    private void ApplySidebarResizePolicy(int cols)
    {
        if (screen.Sidebar is null || cols == _sidebarPolicyCols)
        {
            return;
        }

        _sidebarPolicyCols = cols;
        if (_sidebarPolicyWasApplied)
        {
            AnimateSidebarPolicy(cols);
        }

        _sidebarPolicyWasApplied = true;
    }

    /// <summary>Drives the springs toward the policy targets for
    /// <paramref name="cols" />. Cold start skips this: the static solve
    /// already produces the target geometry (collapse below the threshold,
    /// clamp-pinned 42 columns above), so springs would only replay it.</summary>
    private void AnimateSidebarPolicy(int cols)
    {
        bool shown = cols >= SideBarLayout.AutoShowMinWidth;
        int usable = Math.Max(1, cols - 1); // split gap 1
        if (shown)
        {
            screen.Tree.AnimateMinWidth(ChatScreen.SidebarId, SideBarLayout.DefaultWidth);
            screen.Tree.AnimateRatio(ChatScreen.TimelineId, (usable - SideBarLayout.DefaultWidth) / (float)usable);
        }
        else
        {
            screen.Tree.AnimateMinWidth(ChatScreen.SidebarId, 0);
            screen.Tree.AnimateRatio(ChatScreen.TimelineId, 1f);
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
        ApplySidebarResizePolicy(cols);
        screen.Tree.Solve(cols, rows);

        // CF-D-002: feed projected state from the view-model snapshot so
        // StatusPanel renders through StatusProjector (glyphs, scroll segment,
        // token/cost formatting) instead of the legacy BuildSegments path.
        string statusText = _status.Mode.ToString().ToLowerInvariant();
        screen.Status.ProjectedRetry = _status.Retry;
        long tokensIn = 0;
        long tokensOut = 0;
        decimal costUsd = 0m;
        if (_tokens?.GetStats() is { } stats)
        {
            tokensIn = stats.TotalInputTokens;
            tokensOut = stats.TotalOutputTokens;
        }

        screen.Status.ProjectedState = new UiState
        {
            Status = statusText,
            Model = _status.Model,
            Provider = sessionModel.ProviderId,
            AgentName = sessionModel.Agent,
            Cost = new CostSnapshot(tokensIn, tokensOut, costUsd),
            ScrollOffset = 0,
            ViewportLines = rows,
            TotalLines = Math.Max(rows, screen.Timeline.Timeline.Count)
        };

        // Spring resize (P1.6): while a layout spring is in flight the rects
        // move every frame — self-wake keeps frames flowing until it settles.
        if (screen.Tree.IsAnimating)
        {
            _wake.Writer.TryWrite(null);
        }

        Rect tlRect = screen.Timeline.Rect;
        _timelineViewportH = Math.Max(0, tlRect.Height);
        _ = _timeline.PrepareFrame(tlRect.Width > 0 ? tlRect.Width : cols, _timelineViewportH);

        screenSession.BeginFrame();
        screen.Tree.PaintAll(screenSession.Back);

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

        // Partial-scan damage (renderer-moat): quiet animation frames narrow
        // the diff to the rects that can actually have changed; every other
        // frame — input, events, layout animation, theme swap — full scan.
        ApplyFrameDamageHints(cols);
        ArmGateGlow();

        await screenSession.FlushFrameAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Post-render glow (renderer-moat T3): translates the timeline's gate
    /// glow ledger into the session's effect pipeline — warning accents on
    /// pending approval gates bloom toward a hot tone after the diff selects
    /// them and before SGR encoding. Zero pending gates → empty pipeline →
    /// frames byte-identical to the plain path.
    /// </summary>
    private void ArmGateGlow()
    {
        int count = _timeline.ConsumeGlowRegions(_glowScratch);
        for (int i = 0; i < count; i++)
        {
            (_glowEffects[i] ??= new GlowEffect()).Update(_glowScratch[i]);
            screenSession.Effects.Set(i, _glowEffects[i]);
        }

        for (int i = count; i < VirtualizedChatTimeline.MaxFxDamage; i++)
        {
            screenSession.Effects.Set(i, null);
        }
    }

    /// <summary>
    /// Translates the frame's change sources into diff hints. Conservative by
    /// construction: narrow hints only while the frame was NOT triggered by
    /// input/events, and only for regions with clock-driven animation. Any
    /// doubt falls back to the full scan (empty hints — the engine's default).
    /// </summary>
    private void ApplyFrameDamageHints(int cols)
    {
        bool broad = _broadDamageNextFrame || screen.Tree.IsAnimating;
        int fxCount = 0;
        if (!broad)
        {
            broad = _timeline.ConsumeFrameDamage(_fxDamageScratch, out fxCount);
        }
        else
        {
            _timeline.ConsumeFrameDamage(_fxDamageScratch, out _);
        }

        _broadDamageNextFrame = false;
        if (broad)
        {
            return; // hints empty → engine runs the fused full scan
        }

        // Status bar: 1 row — spinner frames, mascot animation, mode
        // crossfades and the retry countdown all live here. Hinting one row
        // is negligible next to the 500-row feed it protects.
        if (screen.Status.Rect.Height > 0)
        {
            screenSession.Damage(new Rect(0, screen.Status.Rect.Y, cols, screen.Status.Rect.Height));
        }

        // Panel-mode mascot (mascot-brand T2): 3 animated rows beside the
        // composer — same rationale as the status row hint.
        Rect mascotRect = screen.Mascot?.Rect ?? default;
        if (mascotRect.Height > 0)
        {
            screenSession.Damage(mascotRect);
        }

        if (_palette.Visible && screenSession.CurrentRows > 0)
        {
            int w = Math.Clamp(cols - 8, 20, 56);
            int h = Math.Min(_palette.Results.Count + 5, Math.Max(5, screenSession.CurrentRows - 4));
            int x = Math.Max(0, (cols - w) / 2);
            int y = Math.Max(0, (screenSession.CurrentRows - h) / 3);
            screenSession.Damage(new Rect(x, y, w, h));
        }

        for (int i = 0; i < fxCount; i++)
        {
            screenSession.Damage(_fxDamageScratch[i]);
        }
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

    /// <summary>
    /// Inline-image emission (osc-sprint §1337): routes the attachment bytes
    /// through the session's detected protocol — kitty APC for PNG, OSC 1337
    /// for the iTerm2 family. Unsupported protocol/format/oversize → no
    /// emission; the timeline keeps the text description card as fallback.
    /// </summary>
    private async Task EmitInlineImageAsync(ChatScreenBridge.InlineImage image, CancellationToken ct)
    {
        string name = Path.GetFileName(image.Path);
        byte[]? bytes = _inlineImage switch
        {
            InlineImageKind.KittyApc when image.MimeType.EndsWith("png", StringComparison.OrdinalIgnoreCase)
                => Graphics.KittyPngInline(image.Data),
            InlineImageKind.Osc1337 => Osc1337Image.Encode(name, image.Data),
            _ => null,
        };
        if (bytes is null)
        {
            return;
        }

        await backend.WriteAsync(bytes, ct).ConfigureAwait(false);
        bridge.AppendSystemLine($"◆ inline {name} ({_inlineImage})");
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

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="col" /> /
    ///     <paramref name="row" /> falls inside the sidebar's model section.
    ///     Used by the mouse handler to open the model picker on click.
    /// </summary>
    private bool TryHandleSidebarModelClick(int col, int row)
    {
        if (screen.Sidebar is null || screenSession.CurrentCols < SideBarLayout.AutoShowMinWidth)
        {
            return false;
        }

        int sidebarX = screenSession.CurrentCols - SideBarLayout.DefaultWidth;
        if (col < sidebarX || col >= screenSession.CurrentCols)
        {
            return false;
        }

        // MODEL section is the second section in the sidebar paint order,
        // typically at visual rows 3-5 inside the sidebar rect (0-indexed).
        // This is an approximate hit-box — good enough for a click target.
        return row is >= 3 and <= 6;
    }

    private async Task HandleInputAsync(InputEvent evt, CancellationToken ct)
    {
        // Any user input can mutate composer, palette, scroll or selection —
        // the next frame takes the conservative full-scan path.
        _broadDamageNextFrame = true;
        switch (evt.Kind)
        {
            case InputEventKind.Key:
                await HandleKeyAsync(evt.Key, ct).ConfigureAwait(false);
                break;

            case InputEventKind.Capability when evt.Capability.Kind == CapabilityEventKind.Osc11BackgroundReport:
                ApplyAutoTheme(evt.Capability);
                break;

            case InputEventKind.Capability when evt.Capability.Kind == CapabilityEventKind.Osc99NotifyReport:
                _notify = DesktopNotifyKind.Osc99;
                break;

            case InputEventKind.Paste:
            {
                // Paste payload is verbatim by parser contract — sanitize it
                // BEFORE it reaches the composer and, through submit, the agent
                // (osc-sprint): escape sequences and control bytes stripped, a
                // sanitized preview lands in the timeline. No new permissions —
                // after sanitization the paste is trusted input.
                PasteSanitizeResult sanitized = PasteSanitizer.Sanitize(evt.Paste.Text);
                if (sanitized.Modified)
                {
                    bridge.AppendSystemLine(
                        $"⎘ paste: снято {sanitized.EscapeSequences} escape-последоват., {sanitized.ControlChars} control-символов");
                }
                else if (evt.Paste.Text.Contains('\n'))
                {
                    int lines = evt.Paste.Text.Count('\n') + 1;
                    bridge.AppendSystemLine($"⎘ paste: {lines} строк — вставлено как текст, Enter не исполняется");
                }

                // The sanitized buffer IS the preview — the composer shows the
                // cleaned text; submit routes exactly what the user sees.
                _ = _composer.Buffer.InsertText(sanitized.Text);
                break;
            }

            case InputEventKind.Resize:
                // Policy lives in ScreenSession: shrink ⇒ erase-in-display next frame.
                int resizeW = Math.Max(1, evt.Resize.Width);
                screenSession.Resize(resizeW, Math.Max(1, evt.Resize.Height));
                ApplySidebarResizePolicy(resizeW);
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
                else if (evt.Mouse.Button == MouseButton.Left
                         && TryHandleSidebarModelClick(evt.Mouse.Column, evt.Mouse.Row))
                {
                    await OpenModelPaletteAsync(ct).ConfigureAwait(false);
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
        // Key routing can mutate gates, palette, vim state or the composer —
        // the next frame takes the conservative full-scan path.
        _broadDamageNextFrame = true;

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
                if (_paletteAction is not null)
                {
                    var palAction = _paletteAction;
                    _paletteAction = null;
                    await palAction(committed).ConfigureAwait(false);
                }
                else
                {
                    await ExecutePaletteCommandAsync(committed, ct).ConfigureAwait(false);
                }
            }
            else if (!string.IsNullOrEmpty(_palette.LastInputValue) && _paletteInputAction is not null)
            {
                var input = _palette.LastInputValue;
                _palette.LastInputValue = string.Empty;
                var inputAction = _paletteInputAction;
                _paletteInputAction = null;
                await inputAction(input).ConfigureAwait(false);
            }

            _wake.Writer.TryWrite(null);
            return;
        }

        // Slash shortcut: typing '/' on an empty composer opens the command
        // palette directly, skipping manual entry.
        if (key.Key == KeyCode.Char
            && key.Modifiers is KeyModifiers.None or KeyModifiers.Shift
            && (char)key.Character.Value == '/'
            && _composer.Buffer.IsEmpty)
        {
            OpenSlashPalette();
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
            logger.LogInformation("Second idle Ctrl+C — quitting CellForge REPL");
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
        _paletteAction = null;
        _paletteInputAction = null;
        _paletteActionStack.Clear();
        _paletteInputActionStack.Clear();
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

    /// <summary>Opens the palette pre-populated with every registered slash command.</summary>
    private void OpenSlashPalette()
    {
        _paletteAction = null;
        _paletteInputAction = null;
        _paletteActionStack.Clear();
        _paletteInputActionStack.Clear();
        _palette.OnCommit = item => _paletteCommitted = item;
        var commands = GetDispatcher().GetRegisteredCommands();
        var items = commands.Select(cmd => new CommandItem(
            Id: cmd.Name,
            Title: cmd.Name,
            Detail: cmd.Description,
            Shortcut: cmd.Usage,
            Group: cmd.Name is "help" or "exit" or "quit" ? "General"
                : cmd.Name is "setup" or "auth" ? "Config"
                : cmd.Name is "model" or "agent" or "tui" or "renderer" or "storage" ? "Runtime"
                : "Other"
        )).ToArray();
        _palette.Show(items);
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
        _leader.Bind('m', () => _leaderSlash = "model");
        _leader.Bind('a', () => _leaderSlash = "agent");
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
    private async Task SwitchToSessionAsync(string sessionId, CancellationToken ct)
    {
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

    private async Task SwitchToSlotAsync(char chord, CancellationToken ct)
    {
        if (_quickSwitch.Resolve(chord) is not { } sessionId)
        {
            bridge.AppendSystemLine($"⇄ slot {chord}: пусто");
            return;
        }

        await SwitchToSessionAsync(sessionId, ct).ConfigureAwait(false);
    }

    private async Task OpenModelPaletteAsync(CancellationToken ct)
    {
        var providers = services.GetRequiredService<IProviderRegistry>();
        var allModels = await providers.GetAllModelsAsync(ct).ConfigureAwait(false);
        if (allModels.IsFailure)
        {
            bridge.AppendSystemLine($"! {allModels.Error}");
            _wake.Writer.TryWrite(null);
            return;
        }

        var items = new List<CommandItem>();
        foreach (var group in allModels.Value.GroupBy(m => m.ProviderId))
        {
            foreach (var m in group)
            {
                items.Add(new CommandItem(
                    m.Id,
                    m.Id,
                    m.DisplayName,
                    string.Empty,
                    group.Key));
            }
        }

        _paletteActionStack.Push(_paletteAction);
        _paletteAction = async item =>
        {
            var configStore = services.GetRequiredService<IConfigStore>();
            var result = await configStore.UpdateAsync(c =>
            {
                c.Model = item.Id;
                if (item.Id.Contains('/'))
                    c.Provider = item.Id.Split('/')[0];
                return c;
            }, ct).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                bridge.AppendSystemLine($"✓ Model switched to {item.Id}");
                _status.Model = item.Id;
                if (screen.Sidebar is { } sb) sb.State = sb.State with { Model = item.Id };
                var agentDef = services.GetRequiredService<IAgentRegistry>()
                    .GetAgent(AgentName.Create(sessionModel.Agent));
                if (agentDef.IsSuccess)
                    agent.Initialize(sessionModel, agentDef.Value);
            }
            else
            {
                bridge.AppendSystemLine($"✗ Failed: {result.Error}");
            }
            _palette.Hide();
            _wake.Writer.TryWrite(null);
        };

        _palette.OnCommit = item => _paletteCommitted = item;
        _palette.PushFrame(new PaletteFrame("Select Model", "model", items, _palette.OnCommit));
        _wake.Writer.TryWrite(null);
    }

    private async Task OpenAgentPaletteAsync(CancellationToken ct)
    {
        var registry = services.GetRequiredService<IAgentRegistry>();
        var items = registry.GetAllAgents()
            .Select(a => new CommandItem(
                a.Name.Value,
                a.Name.Value,
                a.Description,
                string.Empty,
                "Agents"))
            .ToList();

        _paletteActionStack.Push(_paletteAction);
        _paletteAction = async item =>
        {
            var configStore = services.GetRequiredService<IConfigStore>();
            var result = await configStore.UpdateAsync(c =>
            {
                c.Agent = item.Id;
                return c;
            }, ct).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                bridge.AppendSystemLine($"✓ Switched to agent: {item.Id}");
                var agentDef = registry.GetAgent(AgentName.Create(item.Id));
                if (agentDef.IsSuccess)
                    agent.Initialize(sessionModel, agentDef.Value);
            }
            else
            {
                bridge.AppendSystemLine($"✗ Failed: {result.Error}");
            }
            _palette.Hide();
            _wake.Writer.TryWrite(null);
        };

        _palette.OnCommit = item => _paletteCommitted = item;
        _palette.PushFrame(new PaletteFrame("Select Agent", "agent", items, _palette.OnCommit));
        _wake.Writer.TryWrite(null);
    }

    private async Task OpenSessionsPaletteAsync(CancellationToken ct)
    {
        var store = services.GetService<ISessionStore>();
        if (store is null)
        {
            bridge.AppendSystemLine("⇄ переключение недоступно: хост без хранилища сессий");
            _wake.Writer.TryWrite(null);
            return;
        }

        _paletteActionStack.Push(_paletteAction);
        _paletteAction = async item =>
        {
            if (item.Id == "switch")
            {
                var result = await store.ListAsync().ConfigureAwait(false);
                if (result.IsFailure)
                {
                    bridge.AppendSystemLine($"! {result.Error}");
                    _palette.Hide();
                    _wake.Writer.TryWrite(null);
                    return;
                }

                var sessionItems = result.Value
                    .Select(s => new CommandItem(
                        s.Id,
                        s.Title,
                        $"{s.ProviderId}/{s.Model} · {s.Id[..Math.Min(8, s.Id.Length)]}",
                        string.Empty,
                        "Sessions"))
                    .ToList();

                _paletteActionStack.Push(_paletteAction);
                _paletteAction = async sessionItem =>
                {
                    await SwitchToSessionAsync(sessionItem.Id, ct).ConfigureAwait(false);
                    _palette.Hide();
                    _wake.Writer.TryWrite(null);
                };

                _palette.OnCommit = item => _paletteCommitted = item;
                _palette.PushFrame(new PaletteFrame("Switch Session", "sessions / switch", sessionItems, _palette.OnCommit));
            }
            else if (item.Id == "tree")
            {
                var built = await SessionTreeRunner.BuildAsync(store, sessionModel.Id).ConfigureAwait(false);
                if (built.IsFailure)
                {
                    bridge.AppendSystemLine($"Cannot list sessions: {built.Error}");
                }
                else if (built.Value.Count == 0)
                {
                    bridge.AppendSystemLine("No sessions.");
                }
                else
                {
                    foreach (var line in built.Value)
                        bridge.AppendSystemLine(line);
                }
                _palette.Hide();
            }
            else if (item.Id == "new")
            {
                await StartNewSessionAsync(ct).ConfigureAwait(false);
                _palette.Hide();
            }

            _wake.Writer.TryWrite(null);
        };

        _palette.OnCommit = item => _paletteCommitted = item;
        _palette.PushFrame(new PaletteFrame("Sessions", "sessions", new List<CommandItem>
        {
            new("switch", "Switch Session", "Browse and switch to recent chat session", string.Empty, "Actions"),
            new("tree", "Branch Tree", "Show session fork / lineage tree", string.Empty, "Actions"),
            new("new", "New Session", "Start a fresh chat session", string.Empty, "Actions")
        }, _palette.OnCommit));
        _wake.Writer.TryWrite(null);
    }

    private async Task OpenAuthPaletteAsync(CancellationToken ct)
    {
        var authStore = services.GetRequiredService<AuthStore>();

        _paletteActionStack.Push(_paletteAction);
        _paletteAction = async item =>
        {
            if (item.Id == "list")
            {
                var keysResult = await authStore.ListApiKeysAsync(ct).ConfigureAwait(false);
                if (keysResult.IsSuccess)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Configured API keys:");
                    foreach (var kv in keysResult.Value)
                    {
                        sb.AppendLine($"  {kv.Key}: {(kv.Value ? "set" : "missing")}");
                    }
                    bridge.AppendSystemLine(sb.ToString());
                }
                else
                {
                    bridge.AppendSystemLine($"! {keysResult.Error}");
                }
                _palette.Hide();
            }
            else if (item.Id == "reset")
            {
                var keysResult = await authStore.ListApiKeysAsync(ct).ConfigureAwait(false);
                if (keysResult.IsFailure)
                {
                    bridge.AppendSystemLine($"! {keysResult.Error}");
                    _palette.Hide();
                    _wake.Writer.TryWrite(null);
                    return;
                }

                var providerItems = keysResult.Value.Keys
                    .Select(k => new CommandItem(k, k, string.Empty, string.Empty, "Providers"))
                    .ToList();

                _paletteActionStack.Push(_paletteAction);
                _paletteAction = async providerItem =>
                {
                    var result = await authStore.RemoveApiKeyAsync(providerItem.Id, ct).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        bridge.AppendSystemLine($"✓ API key removed for {providerItem.Id}");
                    }
                    else
                    {
                        bridge.AppendSystemLine($"✗ Failed: {result.Error}");
                    }
                    _palette.Hide();
                    _wake.Writer.TryWrite(null);
                };

                _palette.OnCommit = item => _paletteCommitted = item;
                _palette.PushFrame(new PaletteFrame("Remove API Key", "auth / reset", providerItems, _palette.OnCommit));
            }
            else if (item.Id == "set")
            {
                var providerItems = ProviderPresets.All
                    .Select(p => new CommandItem(p.Id, p.DisplayName, p.Description, string.Empty, "Providers"))
                    .ToList();

                _paletteActionStack.Push(_paletteAction);
                _paletteAction = async providerItem =>
                {
                    _paletteActionStack.Push(_paletteAction);
                    _paletteInputActionStack.Push(_paletteInputAction);
                    _paletteInputAction = async key =>
                    {
                        var result = await authStore.SetApiKeyAsync(providerItem.Id, key, ct).ConfigureAwait(false);
                        if (result.IsSuccess)
                        {
                            bridge.AppendSystemLine($"✓ API key saved for {providerItem.Id}");
                        }
                        else
                        {
                            bridge.AppendSystemLine($"✗ Failed: {result.Error}");
                        }
                        _palette.Hide();
                        _wake.Writer.TryWrite(null);
                    };

                    _palette.OnCommit = item => _paletteCommitted = item;
                    _palette.PushFrame(new PaletteFrame($"auth / set / {providerItem.Id}", $"auth / set / {providerItem.Id}", [], _palette.OnCommit,
                        IsInput: true, InputPlaceholder: "paste API key..."));
                };

                _palette.OnCommit = item => _paletteCommitted = item;
                _palette.PushFrame(new PaletteFrame("Set API Key", "auth / set", providerItems, _palette.OnCommit));
            }

            _wake.Writer.TryWrite(null);
        };

        _palette.OnCommit = item => _paletteCommitted = item;
        _palette.PushFrame(new PaletteFrame("Auth", "auth", new List<CommandItem>
        {
            new("list", "List Configured Keys", "Show configured providers", string.Empty, "Actions"),
            new("set", "Set API Key", "Configure API key for a provider", string.Empty, "Actions"),
            new("reset", "Remove API Key", "Clear stored key for a provider", string.Empty, "Actions")
        }, _palette.OnCommit));
        _wake.Writer.TryWrite(null);
    }

    private async Task OpenConfigPaletteAsync(CancellationToken ct)
    {
        var configStore = services.GetRequiredService<IConfigStore>();
        var configResult = await configStore.LoadAsync(ct).ConfigureAwait(false);

        _paletteActionStack.Push(_paletteAction);
        _paletteAction = async item =>
        {
            if (item.Id == "view")
            {
                if (configResult.IsSuccess)
                {
                    var c = configResult.Value;
                    var sb = new StringBuilder();
                    sb.AppendLine("Current configuration:");
                    sb.AppendLine($"  model: {c.Model}");
                    sb.AppendLine($"  provider: {c.Provider}");
                    sb.AppendLine($"  agent: {c.Agent}");
                    sb.AppendLine($"  tui: {c.Tui}");
                    sb.AppendLine($"  storage: {c.Storage}");
                    sb.AppendLine($"  maxSteps: {c.MaxSteps}");
                    sb.AppendLine($"  costLimit: {c.CostLimit}");
                    bridge.AppendSystemLine(sb.ToString());
                }
                else
                {
                    bridge.AppendSystemLine($"! {configResult.Error}");
                }
                _palette.Hide();
            }
            else if (item.Id == "path")
            {
                string path = JsonConfigStore.GetDefaultPath();
                bridge.AppendSystemLine($"Config path: {path}");
                _palette.Hide();
            }
            else if (item.Id == "set")
            {
                var keyItems = new List<CommandItem>
                {
                    new("model", "Model", "LLM model id", string.Empty, "Keys"),
                    new("provider", "Provider", "LLM provider id", string.Empty, "Keys"),
                    new("agent", "Agent", "Agent name", string.Empty, "Keys"),
                    new("tui", "Tui", "TUI renderer", string.Empty, "Keys"),
                    new("storage", "Storage", "Storage backend", string.Empty, "Keys"),
                    new("maxsteps", "MaxSteps", "Max steps per turn", string.Empty, "Keys"),
                    new("costlimit", "CostLimit", "Cost limit per session", string.Empty, "Keys")
                };

                _paletteActionStack.Push(_paletteAction);
                _paletteAction = async keyItem =>
                {
                    _paletteActionStack.Push(_paletteAction);
                    _paletteInputActionStack.Push(_paletteInputAction);
                    _paletteInputAction = async value =>
                    {
                        var updateResult = await configStore.UpdateAsync(c =>
                        {
                            switch (keyItem.Id)
                            {
                                case "model": c.Model = value; break;
                                case "provider": c.Provider = value; break;
                                case "agent": c.Agent = value; break;
                                case "tui": c.Tui = value; break;
                                case "storage": c.Storage = value; break;
                                case "maxsteps": c.MaxSteps = int.Parse(value); break;
                                case "costlimit": c.CostLimit = decimal.Parse(value); break;
                            }
                            return c;
                        }, ct).ConfigureAwait(false);

                        if (updateResult.IsSuccess)
                        {
                            bridge.AppendSystemLine($"✓ Set {keyItem.Id} = {value}");
                        }
                        else
                        {
                            bridge.AppendSystemLine($"✗ Failed: {updateResult.Error}");
                        }
                        _palette.Hide();
                        _wake.Writer.TryWrite(null);
                    };

                    _palette.OnCommit = item => _paletteCommitted = item;
                    _palette.PushFrame(new PaletteFrame($"config / set / {keyItem.Id}", $"config / set / {keyItem.Id}", [], _palette.OnCommit,
                         IsInput: true, InputPlaceholder: "new value..."));
                };

                _palette.OnCommit = item => _paletteCommitted = item;
                _palette.PushFrame(new PaletteFrame("Set Option", "config / set", keyItems, _palette.OnCommit));
            }

            _wake.Writer.TryWrite(null);
        };

        _palette.OnCommit = item => _paletteCommitted = item;
        _palette.PushFrame(new PaletteFrame("Config", "config", new List<CommandItem>
        {
            new("view", "View Configuration", "Show current runtime configuration", string.Empty, "Actions"),
            new("set", "Set Option", "Change a configuration parameter", string.Empty, "Actions"),
            new("path", "Config Path", "Show filesystem location of config.json", string.Empty, "Actions")
        }, _palette.OnCommit));
        _wake.Writer.TryWrite(null);
    }

    private async Task StartNewSessionAsync(CancellationToken ct)
    {
        if (agent.State.IsRunning)
        {
            bridge.AppendSystemLine("⚠ Cannot create session while running");
            _wake.Writer.TryWrite(null);
            return;
        }

        var store = services.GetService<ISessionStore>();
        if (store is null)
        {
            bridge.AppendSystemLine("⇄ сессия недоступна: хост без хранилища сессий");
            _wake.Writer.TryWrite(null);
            return;
        }

        var configResult = await services.GetRequiredService<IConfigStore>()
            .LoadAsync(ct).ConfigureAwait(false);
        string provider = configResult.IsSuccess ? configResult.Value.Provider : "kilocode";
        string model = configResult.IsSuccess ? configResult.Value.Model : "tencent/hy3:free";

        var newSession = await store.CreateAsync(Environment.CurrentDirectory, sessionModel.Agent, provider, model, ct).ConfigureAwait(false);
        if (newSession.IsFailure)
        {
            bridge.AppendSystemLine($"! {newSession.Error}");
            _wake.Writer.TryWrite(null);
            return;
        }

        var agentDef = services.GetRequiredService<IAgentRegistry>()
            .GetAgent(AgentName.Create(sessionModel.Agent));
        if (agentDef.IsFailure)
        {
            bridge.AppendSystemLine($"! {agentDef.Error}");
            _wake.Writer.TryWrite(null);
            return;
        }

        agent.Initialize(newSession.Value, agentDef.Value);
        sessionModel = newSession.Value;
        bridge.ResetMessageTracking();
        screen.Timeline.Timeline.ReplaceLast(new SystemBlock("Fresh session started."));
        if (screen.Sidebar is { } sb)
        {
            sb.State = sb.State with
            {
                SessionId = newSession.Value.Id,
                SessionTitle = newSession.Value.Title,
            };
        }
        bridge.AppendSystemLine($"✓ Started fresh session: {newSession.Value.Id[..Math.Min(8, newSession.Value.Id.Length)]}");
        _wake.Writer.TryWrite(null);
    }

    private async Task ExecuteInfoCommandAsync(string text, CancellationToken ct)
    {
        var captured = new List<string>();
        var writer = new Action<string>(s => captured.Add(s));

        var dispatcher = new SlashCommandDispatcher(services.GetRequiredService<ILogger<SlashCommandDispatcher>>());
        try
        {
            var outcome = await dispatcher.HandleCoreAsync(
                text, services,
                writer: writer,
                reader: prompt =>
                {
                    captured.Add($"{prompt} — interactive input unavailable in consoleex");
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
        }
        catch (Exception ex)
        {
            captured.Add($"Error: {ex.Message}");
        }

        if (captured.Count == 0)
        {
            _wake.Writer.TryWrite(null);
            return;
        }

        string cmd = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string formatted = FormatInfoBlock(cmd, captured);
        bridge.AppendSystemLine(formatted);
        _wake.Writer.TryWrite(null);
    }

    private static string FormatInfoBlock(string title, IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        sb.Append("┌─ ").Append(title).Append(' ');
        int pad = Math.Max(0, 56 - title.Length - 4);
        sb.Append('─', pad);
        sb.AppendLine("┐");
        foreach (var line in lines)
        {
            sb.Append("│ ").Append(line);
            int trail = Math.Max(0, 56 - line.Length - 2);
            if (trail > 0) sb.Append(' ', trail);
            sb.AppendLine(" │");
        }
        sb.Append("└").Append('─', 56).AppendLine("┘");
        return sb.ToString();
    }

    private async Task ExecutePaletteCommandAsync(CommandItem item, CancellationToken ct)
    {
        if (TryRunLocalCommand(item.Id))
        {
            _wake.Writer.TryWrite(null);
            return;
        }

        switch (item.Id)
        {
            case "model":
            case "m":
                await OpenModelPaletteAsync(ct).ConfigureAwait(false);
                return;

            case "agent":
            case "a":
            case "mode":
                await OpenAgentPaletteAsync(ct).ConfigureAwait(false);
                return;

            case "sessions":
                await OpenSessionsPaletteAsync(ct).ConfigureAwait(false);
                return;

            case "auth":
            case "key":
                await OpenAuthPaletteAsync(ct).ConfigureAwait(false);
                return;

            case "config":
            case "cfg":
                await OpenConfigPaletteAsync(ct).ConfigureAwait(false);
                return;

            case "new":
            case "new-session":
                await StartNewSessionAsync(ct).ConfigureAwait(false);
                return;

            case "help":
            case "h":
                OpenSlashPalette();
                _wake.Writer.TryWrite(null);
                return;

            case "setup":
                bridge.AppendSystemLine("⚠ Setup wizard requires a direct console. Use '/config', '/model', '/auth' or run 'harbor setup' from terminal.");
                _palette.Hide();
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
            string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

            switch (cmd)
            {
                case "help":
                case "h":
                    OpenSlashPalette();
                    return;

                case "model":
                case "m":
                    await OpenModelPaletteAsync(ct).ConfigureAwait(false);
                    return;

                case "agent":
                case "a":
                case "mode":
                    await OpenAgentPaletteAsync(ct).ConfigureAwait(false);
                    return;

                case "sessions":
                    await OpenSessionsPaletteAsync(ct).ConfigureAwait(false);
                    return;

                case "new":
                case "new-session":
                    await StartNewSessionAsync(ct).ConfigureAwait(false);
                    return;

                case "auth":
                    await OpenAuthPaletteAsync(ct).ConfigureAwait(false);
                    return;

                case "config":
                    await OpenConfigPaletteAsync(ct).ConfigureAwait(false);
                    return;

                case "providers":
                case "plugins":
                case "permissions":
                    await ExecuteInfoCommandAsync(text, ct).ConfigureAwait(false);
                    return;

                default:
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
            }
        }

        // Fresh abort token per prompt (no-op guard while a run is active).
        agent.ResetAbortSource();
        ResetRetryCountdown();
        screen.Timeline.Timeline.Append(new UserBlock(text));
        _status.Mode = StatusBarMode.Running;
        _wake.Writer.TryWrite(null);

        _promptInFlight = true;
        ArmLongTurnNotify();
        _ = RunPromptAsync(text, ct);
    }

    /// <summary>
    /// Long-turn desktop notification (osc-sprint §777): a run still active
    /// after 30 s fires one notification through the terminal — kitty OSC 99
    /// when the probe answered, OSC 777 for the urxvt family, nothing when
    /// the terminal gave no signal (suppression is the conservative default).
    /// </summary>
    private void ArmLongTurnNotify()
    {
        _notifyTimer ??= new Timer(OnLongTurnNotifyFire, null, Timeout.Infinite, Timeout.Infinite);
        _notifyTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }

    private void DisarmLongTurnNotify() => _notifyTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    private void OnLongTurnNotifyFire(object? state)
    {
        DisarmLongTurnNotify();
        if (!agent.State.IsRunning)
        {
            return; // turn finished inside the window — nothing to notify about
        }

        var kind = _notify is DesktopNotifyKind.Osc99 ? _notify : NotifyProbe.Detect();
        _notify = kind;
        if (kind is DesktopNotifyKind.None)
        {
            return;
        }

        _pendingNotifySequence = kind == DesktopNotifyKind.Osc99
            ? Osc99Notify.Encode("Harbor", "ход всё ещё выполняется (дольше 30 с)")
            : Osc777Notify.Encode("Harbor", "ход всё ещё выполняется (дольше 30 с)");
        _wake.Writer.TryWrite(null);
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
                // Abort path surfaces as Result failure "The operation was canceled"
                // rather than OCE — normalize to the same user-facing line.
                if (result.Error.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                    || result.Error.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                    bridge.AppendSystemLine("ход прерван");
                else
                    bridge.AppendSystemLine("! " + result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            bridge.AppendSystemLine("ход прерван");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Prompt failed in CellForge REPL");
            bridge.AppendSystemLine("! " + ex.Message);
        }
        finally
        {
            DisarmLongTurnNotify();
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
        _broadDamageNextFrame = true; // status + sidebar both re-render
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
    {
        var configResult = await services.GetRequiredService<IConfigStore>()
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
