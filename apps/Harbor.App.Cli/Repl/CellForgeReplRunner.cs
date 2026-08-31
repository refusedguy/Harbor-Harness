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
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.Projection;
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
                // Paste payload is verbatim by parser contract — never re-parsed.
                _ = _composer.Buffer.InsertText(evt.Paste.Text);
                break;

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
