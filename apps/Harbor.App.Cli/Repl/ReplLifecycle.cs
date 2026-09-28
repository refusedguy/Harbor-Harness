using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.DesignSystem;
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

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Terminal lifecycle + frame loop behind the CellForge REPL (G2 split
///     of <see cref="CellForgeReplRunner"/>, issue #174): screen enter/leave,
///     the event-driven frame loop, frame rendering with damage hints and the
///     post-render glow pipeline, theme probing and welcome text. Inline-image
///     capability wiring (issue #387) lives here too: the probe verdict is
///     handed to the session's image layer and to the timeline at startup.
///     Stateless service — all mutable state stays on the runner and
///     is reached through its internal accessors, so the split moves code
///     without moving behavior.
/// </summary>
internal sealed class ReplLifecycle(CellForgeReplRunner host)
{
    /// <summary>
    ///     Runs the REPL until quit. Returns the exit code
    ///     (slash <c>/exit</c> wins over the loop's own, mirroring legacy).
    /// </summary>
    internal async Task<int> RunAsync(CancellationToken ct = default)
    {
        host.Log.LogInformation("CellForge REPL starting — composer-driven frames over DiffEngine");
        try
        {
            host.ModeController.Enter();
        }
        catch (PlatformNotSupportedException ex)
        {
            host.Log.LogWarning(ex, "CellForge unavailable on this terminal/platform");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            host.Log.LogWarning(ex, "CellForge requires a real tty");
            return 1;
        }
        await host.Backend.WriteAsync(Utf8(CellForgeReplRunner.SeqEnterAltScreen), ct).ConfigureAwait(false);

        // OSC 11 auto-theme probe (sprint UI-V2 P3.3): ask the terminal for its
        // background; the answer surfaces as a Capability event and flips the
        // palette before the first frame. A custom JSON theme (ArmThemeWatcher)
        // applied later always wins over the auto pick.
        await host.Backend.WriteAsync(Utf8(TerminalBackgroundProbe.Query), ct).ConfigureAwait(false);

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
        host.Log.LogInformation(
            "Inline image protocol: {Kind} (image blocks render {Mode})",
            host._inlineImage,
            host._inlineImage == InlineImageKind.None ? "text cards only" : "inline graphics");

        // Setup guide (issue #383): local setup detection + first-run gating.
        // The provider probe it starts runs off-thread; a detection failure
        // leaves the checklist empty and never blocks the first frame.
        await host.Setup.InitializeAsync(ct).ConfigureAwait(false);

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
            try
            {
                await host.Backend.WriteAsync(Utf8(CellForgeReplRunner.SeqLeaveAltScreen), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Terminal may already be gone (kill, closed pipe). The mode
                // restore below must still run — a skipped Restore() leaves
                // Windows consoles stuck in raw/VT mode after exit.
                host.Log.LogWarning(ex, "Leave-alt-screen write failed — restoring console mode anyway");
            }

            host.ModeController.Restore();
            host.InputSource.Dispose();
        }

        host.Log.LogInformation("CellForge REPL ended (quit={Quit}, slashExit={SlashExit})", host._quitRequested, host._slashExitCode);
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
                host.Log.LogInformation("stdin EOF and agent idle — CellForge REPL exiting");
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

    /// <summary>
    /// ENG7 (issue #278) gated frame — render on tick, not on event. A wake
    /// whose model version (TEA store revision + pump dirty seq + geometry)
    /// equals the last rendered one short-circuits: no solve/paint/flush, so
    /// idle produces zero backend writes. Forced frames (ENG5 animation clock
    /// running, layout springs settling, geometry change) bypass the version
    /// check — wall-clock visuals must never freeze — but still pace through
    /// the 60 fps ticker: a dirty frame inside the interval is deferred with
    /// <see cref="FrameTicker.MsUntilDue"/>, never dropped. Painted output is
    /// byte-identical to the ungated loop: every mutation still renders, only
    /// redundant frames disappear.
    /// </summary>
    private async Task RenderFrameGatedAsync(AnimationClock animationClock, CancellationToken ct)
    {
        long nowMs = Environment.TickCount64;
        // Idempotent (ApplyResize dedups); RenderFrameAsync repeats it — the
        // gate must see geometry changes before deciding to skip.
        host.ScreenSession.CheckAutoSize();
        int cols = host.ScreenSession.CurrentCols;
        int rows = host.ScreenSession.CurrentRows;

        bool versionChanged = host._replStore.State.Revision != host._lastFrameStoreRevision
            || host._frameDirtySeq != host._lastFrameDirtySeq;
        bool force = animationClock.IsRunning
            || host.Screen.Tree.IsAnimating
            || cols != host._lastFrameCols
            || rows != host._lastFrameRows;

        if (!versionChanged && !force)
        {
            host._frameTicker.MarkSuppressed();
            return;
        }

        long waitMs = host._frameTicker.MsUntilDue(nowMs);
        if (waitMs > 0)
        {
            host._frameTicker.MarkPaced();
            await Task.Delay((int)Math.Min(waitMs, int.MaxValue), ct).ConfigureAwait(false);
        }

        await RenderFrameAsync(ct).ConfigureAwait(false);
        SnapshotFrameModel();
        host._frameTicker.MarkRendered(Environment.TickCount64);
    }

    /// <summary>Adopts the current model version as last-rendered (ENG7 gate baseline).</summary>
    private void SnapshotFrameModel()
    {
        host._lastFrameStoreRevision = host._replStore.State.Revision;
        host._lastFrameDirtySeq = host._frameDirtySeq;
        host._lastFrameCols = host.ScreenSession.CurrentCols;
        host._lastFrameRows = host.ScreenSession.CurrentRows;
    }

    /// <summary>
    ///     Builds the status snapshot the footer projects from
    ///     (<see cref="StatusProjector" />), reusing <paramref name="prev" />
    ///     when no projected input changed: the footer caches on reference
    ///     equality, so an identical instance is what makes quiet frames free.
    ///     Internal for the status-projection unit tests (InternalsVisibleTo).
    /// </summary>
    /// <param name="prev">Last projected snapshot (<c>null</c> on the first frame).</param>
    /// <param name="storeState">Live TEA store state — chrome, accumulated cost, scroll.</param>
    /// <param name="tokensIn">Cumulative input tokens from the token tracker.</param>
    /// <param name="tokensOut">Cumulative output tokens from the token tracker.</param>
    /// <param name="viewportLines">Rows the terminal can show.</param>
    /// <param name="totalLines">Measured timeline extent for the scroll range.</param>
    /// <remarks>
    ///     Cost rides the store, not a local zero (#457): <see cref="ChatAppReducer" />
    ///     accumulates <c>CostUsd</c> per finished step, so the reducer's value is
    ///     the only one that moves. Scroll likewise: the store counts rows
    ///     <em>lifted from the tail</em> (0 = live tail, reset on every new
    ///     message) while the projector reports the position from the top of the
    ///     range, so the offset is mapped here — the same
    ///     <c>ScrollY = max - offset</c> convention
    ///     <see cref="VirtualizedChatTimeline" /> uses in
    ///     <c>ApplyStoreState</c>.
    /// </remarks>
    internal static UiState BuildStatusSnapshot(
        UiState? prev,
        UiState storeState,
        long tokensIn,
        long tokensOut,
        int viewportLines,
        int totalLines)
    {
        var storeChat = storeState.Chat;
        decimal costUsd = storeChat.Cost.CostUsd;

        // Same range the projector derives from the snapshot it is handed, so
        // the memo key and the painted text can never disagree.
        int maxScroll = Math.Max(0, totalLines - Math.Max(1, viewportLines));
        int scrollOffset = Math.Clamp(maxScroll - storeState.Ui.ScrollOffset, 0, maxScroll);

        if (prev is not null
            && prev.Chat.Status == storeChat.Status
            && prev.Chat.Model == storeChat.Model
            && prev.Chat.Provider == storeChat.Provider
            && prev.Chat.AgentName == storeChat.AgentName
            && prev.Chat.Cost.TokensIn == tokensIn
            && prev.Chat.Cost.TokensOut == tokensOut
            && prev.Chat.Cost.CostUsd == costUsd
            && prev.Ui.ScrollOffset == scrollOffset
            && prev.Ui.ViewportLines == viewportLines
            && prev.Ui.TotalLines == totalLines)
        {
            return prev;
        }

        return new UiState
        {
            Chat = new ChatDomainState
            {
                Status = storeChat.Status,
                Model = storeChat.Model,
                Provider = storeChat.Provider,
                AgentName = storeChat.AgentName,
                Cost = new CostSnapshot(tokensIn, tokensOut, costUsd)
            },
            Ui = new TerminalUiState
            {
                ScrollOffset = scrollOffset,
                ViewportLines = viewportLines,
                TotalLines = totalLines
            }
        };
    }

    private async ValueTask RenderFrameAsync(CancellationToken ct)
    {
        host.ScreenSession.CheckAutoSize();
        int cols = host.ScreenSession.CurrentCols;
        int rows = host.ScreenSession.CurrentRows;
        ApplySidebarResizePolicy(cols);
        // Issue #389: claim the strip's rows before the solve, so it is part of
        // this frame's layout rather than a post-paint overlay, and so a resize
        // re-derives the row count from the new height.
        host.Screen.SyncTabStrip(host._replStore.State.Chat.TabStrip, rows);
        host.Screen.Tree.Solve(cols, rows);

        // CF-D-002: feed projected state from the view-model snapshot so
        // StatusPanel renders through StatusProjector (glyphs, scroll segment,
        // token/cost formatting) instead of the legacy BuildSegments path.
        // #76: the retry slot renders through SetProjectedRetry (structured
        // attempt/max/remaining from the pipeline mirror); null clears it.
        if (host.Pipeline.TryGetRetryProjection(out int retryAttempt, out int retryMax, out int retryRemaining))
        {
            host.Screen.Status.SetProjectedRetry(retryAttempt, retryMax, retryRemaining);
        }
        else
        {
            host.Screen.Status.ProjectedRetry = null;
        }
        long tokensIn = 0;
        long tokensOut = 0;
        if (host.Tokens?.GetStats() is { } stats)
        {
            tokensIn = stats.TotalInputTokens;
            tokensOut = stats.TotalOutputTokens;
        }

        // Read-switching step 4a: chrome identity (status/model/provider/agent)
        // comes from the TEA store (seeded + dual-written); token counts stay on
        // ITokenTracker and geometry stays measured (unified under goldens).
        // Memoized: identical inputs reuse the instance so the projector's
        // reference-equality fast path skips re-projection on quiet frames.
        // #457: cost and scroll used to be hardcoded to 0 here, so the reducer's
        // accumulated CostUsd never reached the footer and the scroll segment
        // was pinned. Both now come from the store snapshot.
        var storeState = host._replStore.State;
        int frameTotal = Math.Max(rows, host.Screen.Timeline.Timeline.Count);
        var prev = BuildStatusSnapshot(
            host._lastStatusSnapshot,
            storeState,
            tokensIn,
            tokensOut,
            rows,
            frameTotal);
        host._lastStatusSnapshot = prev;
        host.Screen.Status.ProjectedState = prev;

        // Spring resize (P1.6): while a layout spring is in flight the rects
        // move every frame — self-wake keeps frames flowing until it settles.
        if (host.Screen.Tree.IsAnimating)
        {
            host._wake.Writer.TryWrite(null);
        }

        Rect tlRect = host.Screen.Timeline.Rect;
        host._timelineViewportH = Math.Max(0, tlRect.Height);

        // Epic C accumulation: viewport geometry flows into the TEA store
        // (changed-only, no per-frame alloc). Nothing reads it yet — the
        // timeline keeps local scroll until the golden-backed flip.
        // frameTotal is computed above for the status snapshot; reuse it here.
        // The trailing ScrollClamp mirrors SpectreTuiRenderer.RenderCore: the
        // reducer's Viewport/HistoryMeasured arms do not clamp, so a shrunken
        // viewport would otherwise leave a stale out-of-range offset.
        if (frameTotal != host._lastStoreTotal || rows != host._lastStoreViewport)
        {
            host._lastStoreTotal = frameTotal;
            host._lastStoreViewport = rows;
            _ = host._replStore.Dispatch(new AppMsg.Viewport(rows));
            _ = host._replStore.Dispatch(new AppMsg.HistoryMeasured(frameTotal));
            _ = host._replStore.Dispatch(new AppMsg.ScrollClamp(Math.Max(0, frameTotal - rows)));
        }
        _ = host._timeline.PrepareFrame(tlRect.Width > 0 ? tlRect.Width : cols, host._timelineViewportH);

        // The frame scope owns the palette pin: everything between here and
        // the flush can throw (a widget at the layout boundary), and an
        // exception used to leave the pin armed on the render thread for
        // good — the theme never updated again until restart (#458).
        using var frame = host.ScreenSession.BeginFrameScope();
        // PRIM2c: dialog/toast paint through LayoutTree.Overlays (hidden layers
        // stay off the stack, so quiet frames are byte-identical to panels-only).
        host.Screen.SyncOverlays(new Rect(0, 0, cols, rows));
        host.Screen.Tree.PaintAll(host.ScreenSession.Back);

        // Copy-on-select highlight (P6.4): transient Reverse overlay — the
        // next repaint without an active selection clears it for free.
        if (host._selection.IsActive)
        {
            host._selection.Paint(host.ScreenSession.Back);
        }

        if (host._palette.Visible)
        {
            int w = Math.Clamp(cols - 8, 20, 56);
            int h = Math.Min(host._palette.Results.Count + 5, Math.Max(5, rows - 4));
            int x = Math.Max(0, (cols - w) / 2);
            int y = Math.Max(0, (rows - h) / 3);
            host._palette.Paint(host.ScreenSession.Back, new Rect(x, y, w, h));
        }

        // Partial-scan damage (renderer-moat): quiet animation frames narrow
        // the diff to the rects that can actually have changed; every other
        // frame — input, events, layout animation, theme swap — full scan.
        ApplyFrameDamageHints(cols);
        ArmGateGlow();

        await frame.FlushAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Translates the frame's change sources into diff hints. Conservative by
    /// construction: narrow hints only while the frame was NOT triggered by
    /// input/events, and only for regions with clock-driven animation. Any
    /// doubt falls back to the full scan (empty hints — the engine's default).
    /// </summary>
    private void ApplyFrameDamageHints(int cols)
    {
        bool broad = host._broadDamageNextFrame || host.Screen.Tree.IsAnimating;
        int fxCount = 0;
        if (!broad)
        {
            broad = host._timeline.ConsumeFrameDamage(host._fxDamageScratch, out fxCount);
        }
        else
        {
            host._timeline.ConsumeFrameDamage(host._fxDamageScratch, out _);
        }

        host._broadDamageNextFrame = false;
        if (broad)
        {
            return; // hints empty → engine runs the fused full scan
        }

        // Status bar: 1 row — spinner frames, mascot animation, mode
        // crossfades and the retry countdown all live here. Hinting one row
        // is negligible next to the 500-row feed it protects.
        if (host.Screen.Status.Rect.Height > 0)
        {
            host.ScreenSession.Damage(new Rect(0, host.Screen.Status.Rect.Y, cols, host.Screen.Status.Rect.Height));
        }

        // Panel-mode mascot (mascot-brand T2): 3 animated rows beside the
        // composer — same rationale as the status row hint.
        Rect mascotRect = host.Screen.Mascot?.Rect ?? default;
        if (mascotRect.Height > 0)
        {
            host.ScreenSession.Damage(mascotRect);
        }

        if (host._palette.Visible && host.ScreenSession.CurrentRows > 0)
        {
            int w = Math.Clamp(cols - 8, 20, 56);
            int h = Math.Min(host._palette.Results.Count + 5, Math.Max(5, host.ScreenSession.CurrentRows - 4));
            int x = Math.Max(0, (cols - w) / 2);
            int y = Math.Max(0, (host.ScreenSession.CurrentRows - h) / 3);
            host.ScreenSession.Damage(new Rect(x, y, w, h));
        }

        for (int i = 0; i < fxCount; i++)
        {
            host.ScreenSession.Damage(host._fxDamageScratch[i]);
        }
    }

    /// <summary>OSC 11 auto-theme (sprint UI-V2 P3.3): bright terminal
    /// background → HarborLight, dark → HarborDark. Runs before any custom
    /// theme file applies, so an explicit theme always wins.</summary>
    internal void ApplyAutoTheme(CapabilityEvent report)
    {
        if (host._themeFileApplied)
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
    // TODO(principles)[DIP]: route through IThemeService once it is registered
    // in DI and Watch surfaces errors/names — today Watch swallows errors and
    // ThemeJsonApplied carries string.Empty, so direct wiring keeps behavior.
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

        host._themeFileApplied = true;

        var initial = JsonThemeLoader.LoadFile(path);
        if (initial.IsSuccess)
        {
            TerminalColorPalette.Apply(initial.Value);
            host.Bridge.AppendSystemLine($"theme: {initial.Value.Name} ({path})");
        }

        host._themeWatcher = new ThemeFileWatcher(
            path,
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
