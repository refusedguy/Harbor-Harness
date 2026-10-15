using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
// #436: GlowRegion is engine-typed; its accent/rect convert exactly below.
using EngineCells = Harbor.Tui.CellForge.Rendering;
// #436: the wheel payload comes from the engine input vocabulary rather
// than its grid vocabulary. The shared alias below pins the other twin,
// so this file names the engine side through a dedicated alias.
using EngineInput = Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Widgets;

public sealed partial class VirtualizedChatTimeline
{
    // ── Store-driven scroll (CF-B-006 + CF-C-002/C-003) ────────────────────
    // UiState (Harbor.Ui.Framework.State) is the single source of truth for scroll
    // position: ScrollOffset (0 = pinned to the live tail, grows toward the top),
    // ViewportLines (visible history rows) and TotalLines (wrapped transcript rows).
    // These helpers only *build* AppMsg values for the host to dispatch via
    // UiStore.Dispatch and *read* UiState snapshots — dispatch stays with the host,
    // so the widget keeps no second scroll authority and the reducer stays pure.
    // Tail-follow is derived, never stored twice: ScrollOffset == 0 means pinned.
    // NOTE: CellForgeViewport has no Refresh/PrepareLayout methods (only
    // RefreshFromConsole/Resize/SetViewportLines/Apply) — layout runs through
    // TimelineLayoutCache.PrepareLayout via PrepareFrame below; the viewport
    // object itself is only read by the host, never mutated here.

    /// <summary>PageUp key → store page-up scroll (reducer clamps via SetScroll).</summary>
    public static AppMsg PageUpMsg() => new AppMsg.KeyInput(ChatAction.ScrollUpPage, new UiKey(UiKeyCode.PageUp));

    /// <summary>PageDown key → store page-down scroll (reducer clamps via SetScroll).</summary>
    public static AppMsg PageDownMsg() => new AppMsg.KeyInput(ChatAction.ScrollDownPage, new UiKey(UiKeyCode.PageDown));

    /// <summary>Up-arrow key → store single-line scroll up.</summary>
    public static AppMsg LineUpMsg() => new AppMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up));

    /// <summary>Down-arrow key → store single-line scroll down.</summary>
    public static AppMsg LineDownMsg() => new AppMsg.KeyInput(ChatAction.ScrollDownLine, new UiKey(UiKeyCode.Down));

    /// <summary>Home key → store jump to the oldest row (offset = max).</summary>
    public static AppMsg ScrollTopMsg() => new AppMsg.KeyInput(ChatAction.ScrollTop, new UiKey(UiKeyCode.Home));

    /// <summary>End key → store pin to the live tail (offset = 0).</summary>
    public static AppMsg ScrollBottomMsg() => new AppMsg.KeyInput(ChatAction.ScrollBottom, new UiKey(UiKeyCode.End));

    /// <summary>Pin to the live tail (offset = 0); the reducer also sets WasRunning.</summary>
    public static AppMsg ResetToTailMsg() => new AppMsg.ScrollResetToTail();

    /// <summary>
    /// Maps a mouse-wheel tick to the store scroll message. Positive
    /// <paramref name="delta"/> = wheel up (the <c>IPointerTarget</c> contract) →
    /// <c>ScrollUpLine</c>; negative → <c>ScrollDownLine</c>; zero → a
    /// <c>ChatAction.None</c> no-op. Line (not page) granularity: the reducer
    /// treats both identically (both clamp via <c>SetScroll</c>), and a full page
    /// per wheel tick is too coarse — hosts that want page steps dispatch
    /// <see cref="PageUpMsg"/> / <see cref="PageDownMsg"/> (possibly several line
    /// messages per tick for acceleration).
    /// </summary>
    /// <remarks>
    /// #33/T2: the tick→direction step is <c>MouseRouter.WheelToKey</c>'s, not
    /// this file's. It used to be spelled here as well, and the engine's copy
    /// carried a comment admitting the duplication ("Same mapping as
    /// VirtualizedChatTimeline.WheelMsg") — two edits to keep in step for one
    /// decision. Delegating makes the direction single-sourced and leaves this
    /// method owning only what is genuinely the host's: which action a
    /// direction resolves to.
    /// </remarks>
    public static AppMsg WheelMsg(int delta) => WheelMsg(MouseRouter.WheelToKey(delta));

    /// <summary>
    /// The other half of <see cref="WheelMsg(int)" />: a wheel tick the engine has
    /// already reduced to a direction becomes the store message. Hosts that bind
    /// <c>TimelineWheelTarget</c> (whose callback is <c>Action&lt;UiKeyDto&gt;</c>,
    /// because the engine cannot name <c>AppMsg</c> since #435) call this
    /// overload. The parameter is the ENGINE dto (#436: the wheel mapping lives
    /// in the standalone leaf); the kinds compared below are its twins.
    /// </summary>
    public static AppMsg WheelMsg(EngineInput.UiKeyDto key) =>
        key.Kind switch
        {
            EngineInput.UiKeyKind.Up => LineUpMsg(),
            EngineInput.UiKeyKind.Down => LineDownMsg(),
            _ => new AppMsg.KeyInput(ChatAction.None, UiKey.Unknown),
        };

    /// <summary>
    /// Mirrors a store snapshot into <see cref="ScrollY"/> / <see cref="FollowTail"/>
    /// and runs layout. Viewport height precedence: explicit
    /// <paramref name="viewportH"/> when positive, else
    /// <c>state.Ui.ViewportLines</c>. <c>state.Ui.TotalLines</c> is informational only —
    /// the authoritative total is the cache's <see cref="TotalHeight"/>, reported
    /// back to the store via <see cref="MeasureMsgs"/> (geometry flows
    /// timeline → store, never the reverse). Store offset maps to timeline space
    /// as <c>ScrollY = max - offset</c> (same convention as
    /// <c>CellForgeViewport.FirstVisibleRow</c>); a zero offset re-pins the tail.
    /// Post-layout the view is re-clamped to the freshly measured range without
    /// re-pinning, so a growing streaming tail cannot yank an unpinned view.
    /// </summary>
    public LayoutOutcome ApplyStoreState(UiState state, int width, int viewportH)
    {
        ArgumentNullException.ThrowIfNull(state);
        int viewH = viewportH > 0 ? viewportH : Math.Max(0, state.Ui.ViewportLines);
        FollowTail = state.Ui.ScrollOffset <= 0;
        if (!FollowTail)
        {
            // Pre-layout snap on the (possibly stale) range keeps the measure
            // window near the target; the authoritative snap below re-asserts
            // the store offset against the freshly settled total.
            SnapScroll(_cache.ClampScrollY(ScrollY, viewH));
        }

        var outcome = PrepareFrame(width, viewH);
        if (!FollowTail)
        {
            // Fresh max: TotalHeight settles only inside PrepareLayout
            // (post-Append _virtual[_count] is stale until patched), so the
            // store-driven position is mapped here. A FullRebuild anchor
            // restore is intentionally overridden: the store is the source
            // of truth; clamping (never re-pinning) keeps a growing
            // streaming tail from yanking an unpinned view.
            long max = _cache.MaxScrollFor(viewH);
            SnapScroll(Math.Clamp(max - (long)state.Ui.ScrollOffset, 0, max));
        }

        return outcome;
    }

    /// <summary>
    /// Builds the geometry messages the host dispatches after layout so the store
    /// tracks the measured viewport (resize path, CF-C-003): <c>Viewport</c> with
    /// the visible height, <c>HistoryMeasured</c> with the settled total
    /// (clamped to <c>int.MaxValue</c> — <c>UiState</c> totals are <c>int</c>),
    /// then <c>ScrollClamp</c> with the measured maximum. Order matters: the
    /// reducer's <c>Viewport</c>/<c>HistoryMeasured</c> arms do not clamp, so the
    /// host must always dispatch the trailing <c>ScrollClamp</c> (a shrunken
    /// viewport otherwise leaves a stale out-of-range offset).
    /// </summary>
    public AppMsg[] MeasureMsgs(int viewportH)
    {
        int viewH = Math.Max(0, viewportH);
        int total = (int)Math.Min(TotalHeight, int.MaxValue);
        int max = (int)Math.Min(_cache.MaxScrollFor(viewH), int.MaxValue);
        return new AppMsg[] { new AppMsg.Viewport(viewH), new AppMsg.HistoryMeasured(total), new AppMsg.ScrollClamp(max) };
    }
}
