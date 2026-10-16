using System.Text;
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
///     Frame rendering behind the CellForge REPL (#95 slice, issue #1506):
///     the ENG7-gated frame, the gate baseline, the status-snapshot memo,
///     the full frame solve/paint/flush and the partial-scan damage hints.
///     Extracted verbatim from <see cref="ReplLifecycle"/>, which keeps thin
///     forwarders — run/loop orchestration, the sidebar spring policy, the
///     gate-glow arming and theme work stay there. Stateless service — all
///     mutable state stays on the runner and is reached through its internal
///     accessors, so the split moves code without moving behavior.
/// </summary>
internal sealed class ReplFrameRenderer(CellForgeReplRunner host)
{
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
    internal async Task RenderFrameGatedAsync(AnimationClock animationClock, CancellationToken ct)
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
    internal void SnapshotFrameModel()
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
        // #653: the "the core could not price this" bit rides along with the
        // number, or an unpriced model would render a bare 0 here while the
        // reducer's own state says otherwise.
        bool costUnpriced = storeChat.Cost.IsCostUnpriced;

        // #651: the occupied context rides in from the store, where the reducer
        // took it from the last step-finish. The tracker's pull feed has no
        // request size to offer — it only ever accumulates — so the synthetic
        // snapshot would otherwise be the one surface still printing the paid
        // total. It is part of the memo: a snapshot that kept a stale context
        // figure would paint a number nobody asked for.
        long contextTokens = storeChat.Cost.ContextTokens;

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
            && prev.Chat.Cost.ContextTokens == contextTokens
            && prev.Chat.Cost.CostUsd == costUsd
            && prev.Chat.Cost.IsCostUnpriced == costUnpriced
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
                Cost = new CostSnapshot(tokensIn, tokensOut, costUsd, costUnpriced, contextTokens)
            },
            Ui = new TerminalUiState
            {
                ScrollOffset = scrollOffset,
                ViewportLines = viewportLines,
                TotalLines = totalLines
            }
        };
    }

    internal async ValueTask RenderFrameAsync(CancellationToken ct)
    {
        host.ScreenSession.CheckAutoSize();
        int cols = host.ScreenSession.CurrentCols;
        int rows = host.ScreenSession.CurrentRows;
        host.Lifecycle.ApplySidebarResizePolicy(cols);
        // Issue #389: claim the strip's rows before the solve, so it is part of
        // this frame's layout rather than a post-paint overlay, and so a resize
        // re-derives the row count from the new height.
        host.Screen.SyncTabStrip(host._replStore.State.Chat.TabStrip, rows);
        // Markup overlay (issue #400): the store owns the session, the overlay
        // only paints the snapshot — same TEA mirror as the tab strip above.
        host.Screen.SyncMarkup(host._replStore.State.Chat.Markup);
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
        host.Screen.Tree.PaintAll(host.ScreenSession.PaintBuffer);

        // Copy-on-select highlight (P6.4): transient Reverse overlay — the
        // next repaint without an active selection clears it for free.
        if (host._selection.IsActive)
        {
            host._selection.Paint(host.ScreenSession.PaintBuffer);
        }

        if (host._palette.Visible)
        {
            int w = Math.Clamp(cols - 8, 20, 56);
            int h = Math.Min(host._palette.Results.Count + 5, Math.Max(5, rows - 4));
            int x = Math.Max(0, (cols - w) / 2);
            int y = Math.Max(0, (rows - h) / 3);
            host._palette.Paint(host.ScreenSession.PaintBuffer, new Rect(x, y, w, h));
        }

        // Partial-scan damage (renderer-moat): quiet animation frames narrow
        // the diff to the rects that can actually have changed; every other
        // frame — input, events, layout animation, theme swap — full scan.
        ApplyFrameDamageHints(cols);
        host.Lifecycle.ArmGateGlow();

        await frame.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translates the frame's change sources into diff hints. Conservative by
    /// construction: narrow hints only while the frame was NOT triggered by
    /// input/events, and only for regions with clock-driven animation. Any
    /// doubt falls back to the full scan (empty hints — the engine's default).
    /// </summary>
    internal void ApplyFrameDamageHints(int cols)
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
}
