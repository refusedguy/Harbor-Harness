using System.Text;
using Harbor.Abstractions.Env;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Configuration;
using Harbor.DesignSystem;
using Harbor.Hosting.Themes;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.Logging;
using Harbor.Registries.Providers;
// #436: same shared-name pins as Harbor.Tui.CellForge/GlobalUsings.cs — the
// Rendering vocabulary keeps its historical meaning in this file; engine-typed
// sites qualify through EngineCells.
using Cell = Harbor.Ui.Framework.Rendering.Cell;
using Rect = Harbor.Ui.Framework.Rendering.Rect;
using ScreenBuffer = Harbor.Ui.Framework.Rendering.ScreenBuffer;
using CellStyle = Harbor.Ui.Framework.Rendering.CellStyle;
using PackedColor = Harbor.Ui.Framework.Rendering.PackedColor;
using StyleAttr = Harbor.Ui.Framework.Rendering.StyleAttr;
using UnicodeWidth = Harbor.Ui.Framework.Rendering.UnicodeWidth;
using KeyEvent = Harbor.Ui.Framework.Rendering.Input.KeyEvent;
using KeyCode = Harbor.Ui.Framework.Rendering.Input.KeyCode;
using KeyModifiers = Harbor.Ui.Framework.Rendering.Input.KeyModifiers;
using KeyEventType = Harbor.Ui.Framework.Rendering.Input.KeyEventType;
using IFocusTarget = Harbor.Ui.Framework.Rendering.Input.IFocusTarget;
using UiKeyDto = Harbor.Ui.Framework.Rendering.Input.UiKeyDto;
using UiKeyKind = Harbor.Ui.Framework.Rendering.Input.UiKeyKind;
using UiKeyMods = Harbor.Ui.Framework.Rendering.Input.UiKeyMods;
using EngineCells = Harbor.Tui.CellForge.Rendering;
// #436: the OSC-11 probe and its color were ALSO ported into the engine
// (Capabilities), so the host-side theme path pins the DesignSystem home.
using DsProbe = Harbor.DesignSystem.TerminalBackgroundProbe;
using DsRgb = Harbor.Ui.Framework.Projection.RgbColor;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Terminal lifecycle + frame loop behind the CellForge REPL (G2 split
///     of <see cref="CellForgeReplRunner"/>, issue #174): screen enter/leave,
///     the event-driven frame loop and the post-render glow pipeline, theme
///     probing and welcome text. Frame rendering itself (gated frame, status
///     snapshot, solve/paint/flush, damage hints) lives on
///     <see cref="ReplFrameRenderer"/> (#95 slice, issue #1506) — this class
///     keeps thin forwarders so existing callers stay untouched.
///     Inline-image capability wiring (issue #387) lives here too: the probe
///     verdict is handed to the session's image layer and to the timeline at
///     startup.
///     Stateless service — all mutable state stays on the runner and
///     is reached through its internal accessors, so the split moves code
///     without moving behavior.
/// </summary>
internal sealed class ReplLifecycle(CellForgeReplRunner host)
{
    /// <summary>Frame-render path (#95 slice, issue #1506) — see <see cref="ReplFrameRenderer"/>.</summary>
    private readonly ReplFrameRenderer _frames = new(host);

    /// <summary>
    ///     Runs the REPL until quit. Returns the exit code
    ///     (slash <c>/exit</c> wins over the loop's own, mirroring legacy).
    /// </summary>
    internal async Task<int> RunAsync(CancellationToken ct = default)
    {
        ReplLifecycleLog.ReplStarting(host.Log);
        try
        {
            host.ModeController.Enter();
        }
        catch (PlatformNotSupportedException ex)
        {
            ReplLifecycleLog.UnavailableOnTerminal(host.Log, ex);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            ReplLifecycleLog.RequiresTty(host.Log, ex);
            return 1;
        }
        await host.Backend.WriteAsync(Utf8(CellForgeReplRunner.SeqEnterAltScreen), ct).ConfigureAwait(false);

        // OSC 11 auto-theme probe (sprint UI-V2 P3.3): ask the terminal for its
        // background; the answer surfaces as a Capability event and flips the
        // palette before the first frame. A custom JSON theme (ArmThemeWatcher)
        // applied later always wins over the auto pick.
        await host.Backend.WriteAsync(Utf8(DsProbe.Query), ct).ConfigureAwait(false);

        // OSC 99 notification capability probe (osc-sprint §777): terminals
        // without the protocol ignore the query silently — answers flip the
        // notify transport to kitty's native family; the urxvt 777 family is
        // the env-detected fallback at fire time.
        await host.Backend.WriteAsync(Utf8(TerminalQueries.Osc99NotifyProbe), ct).ConfigureAwait(false);

        await PrintWelcomeAsync().ConfigureAwait(false);
        host._replStore.Dispatch(new ChatAppMsg.ConfigureRuntime(host.SessionModel.Model, host.SessionModel.ProviderId, host.SessionModel.Agent));
        ArmThemeWatcher();

        // Inline-image capability (issue #387): the probe ran once at startup,
        // so hand its verdict to the session's image layer and to the timeline
        // and image blocks can place their bytes. Both stay inert when the
        // probe said None — pipes, CI, tmux/screen keep the text card, byte
        // for byte.
        host.ScreenSession.Images.Kind = host._inlineImage;
        host.Screen.Timeline.Timeline.InlineImages = host.ScreenSession.Images;
        ReplLifecycleLog.InlineImageProtocol(
            host.Log,
            host._inlineImage,
            host._inlineImage == InlineImageKind.None ? "text cards only" : "inline graphics");

        // Setup guide (issue #383): local setup detection + first-run gating.
        // The provider probe it starts runs off-thread; a detection failure
        // leaves the checklist empty and never blocks the first frame.
        await host.Setup.InitializeAsync(ct).ConfigureAwait(false);

        // PX2 slice 2 (issue #1248): first-run enters CellForge with the setup
        // overlay up — the legacy wizard survives only behind
        // HARBOR_LEGACY_SETUP=1 (gated in ReplRunner). An unloadable config
        // counts as "never completed", mirroring HasCompletedSetupAsync. The
        // checklist above may have opened too; the input loop offers keys to
        // the onboarding flow first, so the dialog owns the keyboard while it
        // is up. Never throws: setup is a convenience surface, not startup.
        try
        {
            // IsSuccess ternary, not a bare `.Value` — the guarded shape
            // RunInteractiveAsync uses above. The default arm doubles as the
            // unloadable-config rule: HarborConfig.Default is !Onboarded, so a
            // config that cannot be read counts as "never completed",
            // mirroring HasCompletedSetupAsync.
            var firstRunConfig = await host.ConfigStore.LoadAsync(ct).ConfigureAwait(false);
            HarborConfig firstRun = firstRunConfig.IsSuccess ? firstRunConfig.Value : HarborConfig.Default;
            if (!firstRun.Onboarded && !host.Onboarding.Active)
            {
                await host.Onboarding.OpenAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // REPL is shutting down — nothing to set up.
        }
        catch (Exception ex)
        {
            ReplLifecycleLog.SetupOverlayFailed(host.Log, ex);
        }

        // #170: the heartbeat must outlive Idle while a mascot reaction/latch
        // still owes motion — the bridge polls both directors every frame.
        host.Bridge.TrackMascot(host.Screen.Mascot, host.Screen.Status);

        var inputTask = host.InputSource.RunAsync(ct);
        host.Commands.BindLeaderKeys();

        // Renderer-moat T3: approval-gate warn pulses bloom through the
        // post-render effect pipeline (diff → transform → SGR encode).
        host._timeline.EnablePostFx = true;

        using var busPump = host.EventBus
            .Subscribe((evt, _) =>
            {
                host._events.Writer.TryWrite(evt);
                host._wake.Writer.TryWrite(null);
                return ValueTask.CompletedTask;
            });

        // One spinner heartbeat while the status bar animates; re-armed per frame.
        using var spinnerTimer = new Timer(
            _ => host._wake.Writer.TryWrite(null),
            null, Timeout.Infinite, Timeout.Infinite);

        // ENG5 (issue #276): background ~10 Hz animation clock for the status
        // spinner/reactions — wall-clock ticks instead of frame-driven ones.
        // Runs exactly while the heartbeat does (see ArmSpinner); the panel
        // falls back to per-paint ticks when it is stopped.
        using var animationClock = new AnimationClock();
        host.Screen.Status.AnimationClock = animationClock;

        try
        {
            await LoopAsync(inputTask, spinnerTimer, animationClock, ct).ConfigureAwait(false);
        }
        finally
        {
            host.Screen.Status.AnimationClock = null;
            host.DisposePipeline();
            host._themeWatcher?.Dispose();
            host._themeDirectoryWatcher?.Dispose();
            // #674: stop pushing core diagnostics into a store the REPL is
            // leaving. The aggregator outlives the REPL (it is a process
            // singleton), so the handler has to come off explicitly.
            host._diagnosticsSync?.Dispose();
            try
            {
                await host.Backend.WriteAsync(Utf8(CellForgeReplRunner.SeqLeaveAltScreen), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Terminal may already be gone (kill, closed pipe). The mode
                // restore below must still run — a skipped Restore() leaves
                // Windows consoles stuck in raw/VT mode after exit.
                ReplLifecycleLog.LeaveAltScreenWriteFailed(host.Log, ex);
            }

            host.ModeController.Restore();
            host.InputSource.Dispose();
        }

        ReplLifecycleLog.ReplEnded(host.Log, host._quitRequested, host._slashExitCode);
        return host._slashExitCode ?? 0;
    }

    // ── Frame loop ─────────────────────────────────────────────────────────

    internal async Task LoopAsync(Task inputTask, Timer spinnerTimer, AnimationClock animationClock, CancellationToken ct)
    {
        var inputReader = host.InputSource.Events;

        await RenderFrameAsync(ct).ConfigureAwait(false);
        SnapshotFrameModel();
        host._frameTicker.MarkRendered(Environment.TickCount64);
        ArmSpinner(spinnerTimer, animationClock);

        // EOF (pipe closed / Ctrl+D) stops INPUT waiting but must not cut off
        // an in-flight turn: the loop exits only when the queue is quiet.
        bool inputClosed = false;
        while (!host._quitRequested && !ct.IsCancellationRequested)
        {
            // ENG7 (issue #278): per-iteration paint-state mutation ledger —
            // any true bumps host._frameDirtySeq before the gated frame, so a
            // wake with an unchanged model short-circuits with zero writes.
            bool inputChanged = false;
            bool eventsChanged = false;
            bool imagesChanged = false;
            bool themeChanged = false;
            bool notified = false;
            bool usageChanged = false;
            bool retryChanged = false;

            Task<bool> inputWait = inputClosed
                ? Task.FromResult(false)
                : inputReader.WaitToReadAsync(ct).AsTask();
            Task<bool> wakeWait = host._wake.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
            var completed = await Task.WhenAny(inputWait, wakeWait).ConfigureAwait(false);

            if (!inputClosed && completed == inputWait && inputWait.Result)
            {
                while (inputReader.TryRead(out var evt))
                {
                    await host.Input.HandleInputAsync(evt, ct).ConfigureAwait(false);
                    inputChanged = true;
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
            // Dual-write (epic C step 1): bridge paints today, the TEA store
            // accumulates for projection tomorrow. Behavior unchanged.
            while (host._events.Reader.TryRead(out var agentEvt))
            {
                host.Pipeline.ObserveEvent(agentEvt);
                host._selection.Clear();
                host._replStore.Dispatch(new ChatAppMsg.Agent(agentEvt));
                await host.Bridge.AcceptAsync(agentEvt, ct).ConfigureAwait(false);
                if (agentEvt is AgentEndEvent)
                {
                    await host.Titles.MaybeAutoTitleAsync(ct).ConfigureAwait(false);
                }

                eventsChanged = true;
            }

            // Inline images (issue #387): the hand-off queue is DRAINED, not
            // written. It started life as an out-of-band emit path (a separate
            // backend write racing the cell diff); the drawing now belongs to
            // ImageBlock, which hands its cached payload to
            // ScreenSession.Images during paint so the escape bytes ride the
            // frame's own single write. What is left of the queue is a
            // "something new landed" signal — one boolean, drained so it stays
            // bounded for the rest of the session.
            bool imageLanded = false;
            while (host.Bridge.TryTakePendingImage(out _))
            {
                imageLanded = true;
            }

            if (imageLanded)
            {
                imagesChanged = true;
            }

            host.Bridge.Tick(Environment.TickCount64);
            string? retryBefore = host._status.Retry;
            host.Pipeline.UpdateRetryCountdown();
            if (host._themeReloadLine is { } themeLine)
            {
                host._themeReloadLine = null;
                host.Bridge.AppendSystemLine(themeLine);
                host._broadDamageNextFrame = true; // live theme swap re-projects every style
                themeChanged = true;
            }

            // Long-turn notification (osc-sprint §777): staged by the timer
            // thread, written here where backend ownership lives.
            if (host.Pipeline.TakeNotifySequence() is { } notifySeq)
            {
                await host.Backend.WriteAsync(Utf8(notifySeq), ct).ConfigureAwait(false);
                host.Bridge.AppendSystemLine("⏱ ход идёт дольше 30 с — уведомление отправлено");
                host._broadDamageNextFrame = true;
                notified = true;
            }

            if (host.Pipeline.RefreshUsageIfDirty())
            {
                host._broadDamageNextFrame = true; // status + sidebar both re-render
                usageChanged = true;
            }

            // Setup checklist (issue #383): a task completing off-thread staged
            // its box rect — applied here, on the frame thread that owns the
            // diff engine's hint list.
            if (host._setupChecklistDamagePending)
            {
                host._setupChecklistDamagePending = false;
                Rect setupDamage = host._setupChecklistDamage;
                host._setupChecklistDamage = default;
                if (setupDamage.Width > 0 && setupDamage.Height > 0)
                {
                    host.ScreenSession.Damage(setupDamage);
                }
            }

            if (!string.Equals(retryBefore, host._status.Retry, StringComparison.Ordinal))
            {
                retryChanged = true;
            }

            if (inputChanged || eventsChanged || imagesChanged || themeChanged || notified || usageChanged || retryChanged)
            {
                host._frameDirtySeq++;
            }

            await RenderFrameGatedAsync(animationClock, ct).ConfigureAwait(false);
            ArmSpinner(spinnerTimer, animationClock);

            if (inputClosed && !host.Pipeline.IsBusy)
            {
                ReplLifecycleLog.StdinEofExiting(host.Log);
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
    internal void ApplySidebarResizePolicy(int cols)
    {
        if (host.Screen.Sidebar is null || cols == host._sidebarPolicyCols)
        {
            return;
        }

        host._sidebarPolicyCols = cols;
        if (host._sidebarPolicyWasApplied)
        {
            AnimateSidebarPolicy(cols);
        }

        host._sidebarPolicyWasApplied = true;
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
            host.Screen.Tree.AnimateMinWidth(ChatScreen.SidebarId, SideBarLayout.DefaultWidth);
            host.Screen.Tree.AnimateRatio(ChatScreen.TimelineId, (usable - SideBarLayout.DefaultWidth) / (float)usable);
        }
        else
        {
            host.Screen.Tree.AnimateMinWidth(ChatScreen.SidebarId, 0);
            host.Screen.Tree.AnimateRatio(ChatScreen.TimelineId, 1f);
        }
    }

    /// <summary>Drains pending wakeup signals — one repaint coalesces them all.</summary>
    private void DrainWake()
    {
        int drained = 0;
        while (host._wake.Reader.TryRead(out _))
        {
            drained++;
        }

        _ = drained; // count intentionally unused; coalescing is the point
    }

    /// <summary>80 ms heartbeat only while an animation is on screen.</summary>
    private void ArmSpinner(Timer spinnerTimer, AnimationClock animationClock)
    {
        bool keep = ShouldKeepHeartbeat(host._status.Mode, host.Bridge.IsMascotAnimating);
        spinnerTimer.Change(keep ? 80 : Timeout.Infinite, Timeout.Infinite);

        // ENG5: the background animation clock lives exactly as long as the
        // frame heartbeat — no timer thread in quiet Idle. Start is idempotent.
        if (keep)
        {
            animationClock.Start();
        }
        else
        {
            animationClock.Stop();
        }
    }

    /// <summary>
    /// Heartbeat condition (#170): the Running/Compacting spinner plus any
    /// mascot reaction/latch still playing out in Idle — the error/success face
    /// revives by itself once the latch expires instead of freezing mid-sequence
    /// forever. Internal for the heartbeat unit tests (InternalsVisibleTo).
    /// </summary>
    internal static bool ShouldKeepHeartbeat(StatusBarMode mode, bool mascotAnimating) =>
        mode is StatusBarMode.Running or StatusBarMode.Compacting || mascotAnimating;

    /// <summary>Forwarder — the ENG7-gated frame lives on <see cref="ReplFrameRenderer"/>.</summary>
    private Task RenderFrameGatedAsync(AnimationClock animationClock, CancellationToken ct) =>
        _frames.RenderFrameGatedAsync(animationClock, ct);

    /// <summary>Forwarder — the ENG7 gate baseline lives on <see cref="ReplFrameRenderer"/>.</summary>
    private void SnapshotFrameModel() => _frames.SnapshotFrameModel();

    /// <summary>Forwarder — the status-snapshot memo lives on <see cref="ReplFrameRenderer"/>.</summary>
    internal static UiState BuildStatusSnapshot(
        UiState? prev,
        UiState storeState,
        long tokensIn,
        long tokensOut,
        int viewportLines,
        int totalLines) =>
        ReplFrameRenderer.BuildStatusSnapshot(prev, storeState, tokensIn, tokensOut, viewportLines, totalLines);

    /// <summary>Forwarder — the frame solve/paint/flush lives on <see cref="ReplFrameRenderer"/>.</summary>
    private ValueTask RenderFrameAsync(CancellationToken ct) => _frames.RenderFrameAsync(ct);

    /// <summary>
    /// Post-render glow (renderer-moat T3): translates the timeline's gate
    /// glow ledger into the session's effect pipeline — warning accents on
    /// pending approval gates bloom toward a hot tone after the diff selects
    /// them and before SGR encoding. Zero pending gates → empty pipeline →
    /// frames byte-identical to the plain path.
    /// </summary>
    /// <remarks>
    /// Internal for the slot-aging unit tests (InternalsVisibleTo) — #889. This is the
    /// CONSUMER half of the gate glow: <c>PostFxTests</c> covers the ledger this reads
    /// (<c>VirtualizedChatTimeline.ConsumeGlowRegions</c>) and nothing covered the method
    /// that acts on it, so the drain below could be deleted with a fully green suite.
    /// It could not be reached from a test otherwise: the only other path in is
    /// <c>LoopAsync</c> → <c>RenderFrameGatedAsync</c>, which is gated by a 60 fps frame
    /// ticker, a version-equality short-circuit and an animation clock — a wall-clock
    /// test in the one domain that has already produced a flake (#648). This method
    /// touches no clock, no timer and no async, so opening it is the whole cost.
    /// </remarks>
    internal void ArmGateGlow()
    {
        int count = host._timeline.ConsumeGlowRegions(host._glowScratch);
        for (int i = 0; i < count; i++)
        {
            (host._glowEffects[i] ??= new GlowEffect()).Update(host._glowScratch[i]);
            host.ScreenSession.Effects.Set(i, host._glowEffects[i]);
        }

        for (int i = count; i < VirtualizedChatTimeline.MaxFxDamage; i++)
        {
            host.ScreenSession.Effects.Set(i, null);
        }
    }

    /// <summary>Forwarder — the damage-hint translation lives on <see cref="ReplFrameRenderer"/>.</summary>
    private void ApplyFrameDamageHints(int cols) => _frames.ApplyFrameDamageHints(cols);

    /// <summary>OSC 11 auto-theme (sprint UI-V2 P3.3): bright terminal
    /// background → HarborLight, dark → HarborDark. Runs before any custom
    /// theme file applies, so an explicit theme always wins.</summary>
    internal void ApplyAutoTheme(CapabilityEvent report)
    {
        if (host._themeFileApplied)
        {
            return; // custom JSON owns the palette — auto-detect stands down
        }

        var background = new DsRgb(
            (byte)Math.Clamp(report.Red, 0, 255),
            (byte)Math.Clamp(report.Green, 0, 255),
            (byte)Math.Clamp(report.Blue, 0, 255));
        var theme = DsProbe.RelativeLuminance(background)
                    >= DsProbe.LightLuminanceThreshold
            ? HarborTheme.HarborLight
            : HarborTheme.HarborDark;
        TerminalColorPalette.Apply(theme);
    }

    /// <summary>
    ///     Resolve the model's context window from the cached provider catalog
    ///     (no network — cache only). Unknown model/provider yields 0.
    /// </summary>
    internal async Task<int> ResolveContextWindowAsync(string providerId, string modelId, CancellationToken ct)
    {
        // Best-effort: hosts without a provider registry (smoke tests) get 0.
        if (host.ProviderRegistry is not { } registry)
        {
            return 0;
        }

        var pid = ProviderId.TryCreate(providerId);
        if (pid.IsFailure)
        {
            return 0;
        }

        var models = await registry.GetModelsCachedAsync(pid.Value, ct).ConfigureAwait(false);
        if (models.IsFailure)
        {
            return 0;
        }

        foreach (var m in models.Value)
        {
            if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(0, m.ContextWindow);
            }
        }

        return 0;
    }

    private async Task PrintWelcomeAsync()
    {
        var configResult = await host.ConfigStore
            .LoadAsync().ConfigureAwait(false);
        string model = configResult.IsSuccess ? configResult.Value.EffectiveModel : "?";
        host.Bridge.AppendSystemLine("Harbor — modular AI coding agent [consoleex]");
        host.Bridge.AppendSystemLine($"model: {model} | ввод — текст, /help — команды, Ctrl+C×2 — выход");

        // Sidebar context (sprint UI-V2 P4): session identity + model before
        // the first frame paints; tokens arrive via the pipeline feed per turn.
        // StatusViewModel.Model seeds assistant bubble headers (bridge reads it).
        host._status.Model = model;
        await host.Sessions.SeedWelcomeChromeAsync(model).ConfigureAwait(false);
    }

    /// <summary>
    ///     Custom JSON theme with live-reload (sprint UI-V2 P3.2): the file
    ///     applies once at startup, later edits flow through the watcher's
    ///     poll timer (parse failures keep the last applied theme). Path:
    ///     <c>HARBOR_THEME_FILE</c>, else <c>~/.harbor/theme.json</c> when present.
    /// </summary>
    // TODO(principles)[DIP]: the READ half now goes through IThemeStore (#668),
    // so the apply/watch roles are the only ones still wired directly. Routing
    // those needs IThemeService in DI, and Watch still swallows errors and
    // ThemeJsonApplied carries string.Empty, so direct wiring keeps behavior.
    private void ArmThemeWatcher()
    {
        string path = HarborEnv.Get(HarborEnv.ThemeFile)
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          ".harbor", "theme.json");
        if (File.Exists(path))
        {
            ArmThemeFile(path);
            return;
        }

        // #622: no explicit theme file, so the themes DIRECTORY is the axis. This
        // is the half of the theme extension point that was unreachable from any
        // product: `ThemeStore` and `ThemeDirectoryWatcher` existed, were
        // documented in docs/DESIGN_SYSTEM.md as a live marketplace, and nothing
        // outside their own test file ever constructed them. A user who followed
        // the documentation — drop a .json into ~/.harbor/themes/ — got no theme,
        // which is what makes an additive axis read as a frozen one.
        ArmThemeDirectory();
    }

    /// <summary>
    ///     #622 — arm the themes directory (<c>~/.harbor/themes</c>, override with
    ///     <c>HARBOR_THEMES_DIR</c>) so a theme is added by dropping a file into
    ///     it. This is the whole point of #622's carve-out from the #555 freeze: the
    ///     theme axis is DATA, so adding one must edit no code. (The freeze itself
    ///     was lifted on 2026-10-20; what #622 carved out is a new theme SOURCE,
    ///     which is still a new axis and a decision of its own.)
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Poll()</c> is called once synchronously rather than waiting for
    ///         the watcher's first 500 ms tick. The OSC 11 auto-detect is in flight
    ///         at this point and stands down only via <c>_themeFileApplied</c>, so
    ///         the applied theme has to be installed before the response can land —
    ///         the same ordering the single-file arm above relies on. The initial
    ///         poll records the directory's stamps, so the timer's first tick finds
    ///         nothing changed and does not re-apply.
    ///     </para>
    ///     <para>
    ///         <b>Which theme wins.</b> <c>Poll</c> applies every parseable
    ///         <c>*.json</c> in name order, so with several files present the last
    ///         one alphabetically is the effective theme. That ordering is
    ///         <see cref="ThemeDirectoryWatcher" />'s own existing contract, not a
    ///         choice made here: the alternative was to re-derive a selection rule
    ///         in the REPL and hold two answers to one question. A user who wants
    ///         a specific theme names it — one file, or <c>HARBOR_THEME_FILE</c>,
    ///         which takes the branch above.
    ///     </para>
    /// </remarks>
    private void ArmThemeDirectory()
    {
        var store = new ThemeStore();
        if (!Directory.Exists(store.ThemesDirectory))
        {
            return;
        }

        host._themeDirectoryWatcher = new ThemeDirectoryWatcher(
            store.ThemesDirectory,
            store,
            onApplied: theme =>
            {
                host._themeReloadLine = $"theme: live-reload → {theme.Name}";
                host._wake.Writer.TryWrite(null);
            },
            onError: error =>
            {
                host._themeReloadLine = "! theme: " + error;
                host._wake.Writer.TryWrite(null);
            });

        host._themeDirectoryWatcher.Poll();

        // Stands the OSC 11 auto-detect down exactly as the single-file arm does:
        // a theme the user placed wins over a theme the terminal guessed.
        if (host._themeDirectoryWatcher.LastApplied is { } applied)
        {
            host._themeFileApplied = true;
            host.Bridge.AppendSystemLine(
                $"theme: {applied.Name} ({store.ThemesDirectory})");
        }
    }

    private void ArmThemeFile(string path)
    {
        host._themeFileApplied = true;

        IThemeStore store = new ThemeStore();
        ThemeParseResult initial = store.LoadFile(path);
        if (initial.IsSuccess)
        {
            TerminalColorPalette.Apply(initial.Theme);
            host.Bridge.AppendSystemLine($"theme: {initial.Theme.Name} ({path})");
        }

        host._themeWatcher = new ThemeFileWatcher(
            path,
            store,
            onApplied: theme =>
            {
                host._themeReloadLine = $"theme: live-reload → {theme.Name}";
                host._wake.Writer.TryWrite(null);
            },
            onError: error =>
            {
                host._themeReloadLine = "! theme: " + error;
                host._wake.Writer.TryWrite(null);
            });
    }

    internal static ReadOnlyMemory<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);
}

/// <summary>
///     SG9: BCL <c>[LoggerMessage]</c> delegates for <see cref="ReplLifecycle" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ReplLifecycleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "CellForge REPL starting — composer-driven frames over DiffEngine")]
    public static partial void ReplStarting(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "CellForge unavailable on this terminal/platform")]
    public static partial void UnavailableOnTerminal(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "CellForge requires a real tty")]
    public static partial void RequiresTty(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Inline image protocol: {Kind} (image blocks render {Mode})")]
    public static partial void InlineImageProtocol(ILogger logger, InlineImageKind kind, string mode);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "First-run setup overlay failed to open — continuing without it")]
    public static partial void SetupOverlayFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Leave-alt-screen write failed — restoring console mode anyway")]
    public static partial void LeaveAltScreenWriteFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "CellForge REPL ended (quit={Quit}, slashExit={SlashExit})")]
    public static partial void ReplEnded(ILogger logger, bool quit, int? slashExit);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "stdin EOF and agent idle — CellForge REPL exiting")]
    public static partial void StdinEofExiting(ILogger logger);
}
