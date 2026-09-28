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
///     post-render glow pipeline, theme probing, welcome text and inline-image
///     emission. Stateless service — all mutable state stays on the runner and
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
        host._replStore.Dispatch(new UiMsg.ConfigureRuntime(host.SessionModel.Model, host.SessionModel.ProviderId, host.SessionModel.Agent));
        ArmThemeWatcher();

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

        try
        {
            await LoopAsync(inputTask, spinnerTimer, ct).ConfigureAwait(false);
        }
        finally
        {
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

    internal async Task LoopAsync(Task inputTask, Timer spinnerTimer, CancellationToken ct)
    {
        var inputReader = host.InputSource.Events;

        await RenderFrameAsync(ct).ConfigureAwait(false);
        ArmSpinner(spinnerTimer);

        // EOF (pipe closed / Ctrl+D) stops INPUT waiting but must not cut off
        // an in-flight turn: the loop exits only when the queue is quiet.
        bool inputClosed = false;
        while (!host._quitRequested && !ct.IsCancellationRequested)
        {
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
                host._replStore.Dispatch(new UiMsg.Agent(agentEvt));
                await host.Bridge.AcceptAsync(agentEvt, ct).ConfigureAwait(false);
                if (agentEvt is AgentEndEvent)
                {
                    await host.Titles.MaybeAutoTitleAsync(ct).ConfigureAwait(false);
                }
            }

            // Inline images (osc-sprint §1337): attachments drained on the
            // frame thread — the only thread allowed to write the backend.
            while (host.Bridge.TryTakePendingImage(out var image))
            {
                await EmitInlineImageAsync(image, ct).ConfigureAwait(false);
            }

            host.Bridge.Tick(Environment.TickCount64);
            host.Pipeline.UpdateRetryCountdown();
            if (host._themeReloadLine is { } themeLine)
            {
                host._themeReloadLine = null;
                host.Bridge.AppendSystemLine(themeLine);
                host._broadDamageNextFrame = true; // live theme swap re-projects every style
            }

            // Long-turn notification (osc-sprint §777): staged by the timer
            // thread, written here where backend ownership lives.
            if (host.Pipeline.TakeNotifySequence() is { } notifySeq)
            {
                await host.Backend.WriteAsync(Utf8(notifySeq), ct).ConfigureAwait(false);
                host.Bridge.AppendSystemLine("⏱ ход идёт дольше 30 с — уведомление отправлено");
                host._broadDamageNextFrame = true;
            }

            if (host.Pipeline.RefreshUsageIfDirty())
            {
                host._broadDamageNextFrame = true; // status + sidebar both re-render
            }

            await RenderFrameAsync(ct).ConfigureAwait(false);
            ArmSpinner(spinnerTimer);

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
    private void ArmSpinner(Timer spinnerTimer)
    {
        spinnerTimer.Change(ShouldKeepHeartbeat(host._status.Mode, host.Bridge.IsMascotAnimating) ? 80 : Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Heartbeat condition (#170): the Running/Compacting spinner plus any
    /// mascot reaction/latch still playing out in Idle — the error/success face
    /// revives by itself once the latch expires instead of freezing mid-sequence
    /// forever. Internal for the heartbeat unit tests (InternalsVisibleTo).
    /// </summary>
    internal static bool ShouldKeepHeartbeat(StatusBarMode mode, bool mascotAnimating) =>
        mode is StatusBarMode.Running or StatusBarMode.Compacting || mascotAnimating;

    private async ValueTask RenderFrameAsync(CancellationToken ct)
    {
        host.ScreenSession.CheckAutoSize();
        int cols = host.ScreenSession.CurrentCols;
        int rows = host.ScreenSession.CurrentRows;
        ApplySidebarResizePolicy(cols);
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
        decimal costUsd = 0m;
        if (host.Tokens?.GetStats() is { } stats)
        {
            tokensIn = stats.TotalInputTokens;
            tokensOut = stats.TotalOutputTokens;
        }

        // Read-switching step 4a: chrome identity (status/model/provider/agent)
        // comes from the TEA store (seeded + dual-written); Cost stays on
        // ITokenTracker and geometry stays measured (unified under goldens).
        // Memoized: identical inputs reuse the instance so the projector's
        // reference-equality fast path skips re-projection on quiet frames.
        var storeChat = host._replStore.State.Chat;
        int frameTotal = Math.Max(rows, host.Screen.Timeline.Timeline.Count);
        if (host._lastStatusSnapshot is not { } prev
            || prev.Chat.Status != storeChat.Status
            || prev.Chat.Model != storeChat.Model
            || prev.Chat.Provider != storeChat.Provider
            || prev.Chat.AgentName != storeChat.AgentName
            || prev.Cost.TokensIn != tokensIn
            || prev.Cost.TokensOut != tokensOut
            || prev.Cost.CostUsd != costUsd
            || prev.ViewportLines != rows
            || prev.TotalLines != frameTotal)
        {
            prev = new UiState
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
                    ScrollOffset = 0,
                    ViewportLines = rows,
                    TotalLines = frameTotal
                }
            };
            host._lastStatusSnapshot = prev;
        }

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
            _ = host._replStore.Dispatch(new UiMsg.Viewport(rows));
            _ = host._replStore.Dispatch(new UiMsg.HistoryMeasured(frameTotal));
            _ = host._replStore.Dispatch(new UiMsg.ScrollClamp(Math.Max(0, frameTotal - rows)));
        }
        _ = host._timeline.PrepareFrame(tlRect.Width > 0 ? tlRect.Width : cols, host._timelineViewportH);

        host.ScreenSession.BeginFrame();
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

        await host.ScreenSession.FlushFrameAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Inline-image emission (osc-sprint §1337): routes the attachment bytes
    /// through the session's detected protocol — kitty APC for PNG, OSC 1337
    /// for the iTerm2 family. Unsupported protocol/format/oversize → no
    /// emission; the timeline keeps the text description card as fallback.
    /// </summary>
    private async Task EmitInlineImageAsync(ChatScreenBridge.InlineImage image, CancellationToken ct)
    {
        string name = Path.GetFileName(image.Path);
        byte[]? bytes = host._inlineImage switch
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

        await host.Backend.WriteAsync(bytes, ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine($"◆ inline {name} ({host._inlineImage})");
        host._wake.Writer.TryWrite(null);
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
