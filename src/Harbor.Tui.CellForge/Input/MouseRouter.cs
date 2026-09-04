using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Input;

/// <summary>Pointer-event sink for a hit-tested region (celldiff §5.2).</summary>
public interface IPointerTarget
{
    string Id { get; }

    void OnPress(int col, int row);

    void OnRelease(int col, int row);

    /// <summary>Positive delta = wheel up.</summary>
    void OnWheel(int col, int row, int delta);
}

/// <summary>
/// SGR mouse routing scaffold: hit-tests press/release/wheel against resolved
/// layout rects and dispatches to the owning target. Out-of-screen coordinates
/// (drag release outside the window, §3.3) are clamped before the hit test.
/// </summary>
public sealed class MouseRouter
{
    private readonly List<(IPointerTarget Target, Rect Rect)> _regions = [];
    private readonly int _screenCols;
    private readonly int _screenRows;

    /// <summary>Creates a router with screen bounds for coordinate clamping.</summary>
    /// <param name="screenCols">Visible width for out-of-range clamping (§3.3).</param>
    /// <param name="screenRows">Visible height for out-of-range clamping.</param>
    public MouseRouter(int screenCols = 4096, int screenRows = 4096)
    {
        _screenCols = screenCols;
        _screenRows = screenRows;
    }

    public void Bind(IPointerTarget target, Rect rect) => _regions.Add((target, rect));

    public void Rebind(IPointerTarget target, Rect rect)
    {
        _regions.RemoveAll(r => ReferenceEquals(r.Target, target));
        Bind(target, rect);
    }

    public void Clear() => _regions.Clear();

    public void Press(int col, int row)
    {
        Clamp(ref col, ref row);
        if (HitTest(col, row) is var (target, rect))
        {
            target.OnPress(col - rect.X, row - rect.Y);
        }
    }

    public void Release(int col, int row)
    {
        Clamp(ref col, ref row);
        if (HitTest(col, row) is var (target2, rect2))
        {
            target2.OnRelease(col - rect2.X, row - rect2.Y);
        }
    }

    public void Wheel(int col, int row, int delta)
    {
        Clamp(ref col, ref row);
        if (HitTest(col, row) is var (target3, rect3))
        {
            target3.OnWheel(col - rect3.X, row - rect3.Y, delta);
        }
    }

    /// <summary>Returns the owning target and its bound rect, or null.</summary>
    public (IPointerTarget Target, Rect Rect)? HitTest(int col, int row)
    {
        foreach (var (target, rect) in _regions)
        {
            if (rect.Contains(col, row))
            {
                return (target, rect);
            }
        }

        return null;
    }

    // ── Store-driven wheel scroll (CF-B-006 + CF-C-002) ────────────────────
    // Wheel ticks become UiMsg.KeyInput line-scrolls for UiStore.Dispatch; the
    // existing Press/Release/Wheel routing above is untouched (targets keep
    // working). Positive delta = wheel up per the IPointerTarget contract.

    /// <summary>
    /// Maps a wheel tick to the store scroll message: positive
    /// <paramref name="delta"/> (wheel up) → <c>ScrollUpLine</c>, negative →
    /// <c>ScrollDownLine</c>, zero → a <c>ChatAction.None</c> no-op the reducer
    /// drops. Same mapping as <c>VirtualizedChatTimeline.WheelMsg</c>, kept local
    /// so Input never depends on Widgets. The host dispatches the result
    /// (once per tick, or in a loop for acceleration).
    /// </summary>
    public static UiMsg WheelToMessage(int delta)
    {
        if (delta > 0)
        {
            return new UiMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up));
        }

        if (delta < 0)
        {
            return new UiMsg.KeyInput(ChatAction.ScrollDownLine, new UiKey(UiKeyCode.Down));
        }

        return new UiMsg.KeyInput(ChatAction.None, UiKey.Unknown);
    }

    private void Clamp(ref int col, ref int row) =>
        (col, row) = (
            Math.Clamp(col, 0, Math.Max(0, _screenCols - 1)),
            Math.Clamp(row, 0, Math.Max(0, _screenRows - 1)));
}

/// <summary>
/// Wheel-only pointer target that forwards ticks to a store-dispatch callback
/// (CF-C-002): bind it to the timeline rect and wheel events flow into the store
/// as <c>KeyInput</c> line-scrolls via <see cref="MouseRouter.WheelToMessage"/>.
/// Press/release are intentional no-ops (selection lives elsewhere). AOT-clean:
/// no reflection, no allocations beyond the message itself.
/// </summary>
public sealed class TimelineWheelTarget : IPointerTarget
{
    private readonly Action<UiMsg> _dispatch;

    /// <summary>Create a wheel-forwarding target bound to a timeline rect.</summary>
    /// <param name="id">Target id for hit-test diagnostics; falls back to "timeline-wheel".</param>
    /// <param name="dispatch">Store dispatch, e.g. <c>msg => { _ = store.Dispatch(msg); }</c>.</param>
    public TimelineWheelTarget(string id, Action<UiMsg> dispatch)
    {
        Id = string.IsNullOrWhiteSpace(id) ? "timeline-wheel" : id;
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public string Id { get; }

    public void OnPress(int col, int row)
    {
    }

    public void OnRelease(int col, int row)
    {
    }

    public void OnWheel(int col, int row, int delta) => _dispatch(MouseRouter.WheelToMessage(delta));
}
