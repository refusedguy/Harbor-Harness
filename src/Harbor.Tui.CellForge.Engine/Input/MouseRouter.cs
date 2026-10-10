using Harbor.Tui.CellForge.Rendering;

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

    // ── Wheel → framework-neutral key (CF-B-006 + CF-C-002, epic #33/T2) ─────
    // A wheel tick becomes a BCL-only UiKeyDto — the vocabulary #162 put in
    // Rendering.Input — and the HOST decides what a scroll key means. #435
    // deleted the Harbor.Ui.Framework.State edge this method used to ride, and
    // the deletion is the design: this method used to return AppMsg.KeyInput,
    // which made "a wheel scrolled up by one line" a fact about the chat store.
    // It is not. A wheel tick carries a DIRECTION; that the direction scrolls a
    // transcript by a line is the host's policy, and it lives next to the other
    // scroll bindings in VirtualizedChatTimeline. Nothing is lost by splitting
    // the two — UiKeyKind.Up / .Down / Unknown preserve the sign exactly.

    /// <summary>
    /// Maps a wheel tick to the framework-neutral key it stands for: positive
    /// <paramref name="delta"/> (wheel up) → <see cref="UiKeyKind.Up" />, negative →
    /// <see cref="UiKeyKind.Down" />, zero → <see cref="UiKeyDto.Unknown" />, which
    /// the reducer drops. Sign only — the magnitude stays host-side, because a
    /// host may accelerate by dispatching several keys per tick. The host turns
    /// the result into a store message through
    /// <c>Harbor.Ui.Framework.State.KeyEventAdapter</c>, the single
    /// Rendering→State crossing point (#33/T1).
    /// </summary>
    public static UiKeyDto WheelToKey(int delta)
    {
        if (delta > 0)
        {
            return new UiKeyDto(UiKeyKind.Up);
        }

        if (delta < 0)
        {
            return new UiKeyDto(UiKeyKind.Down);
        }

        return UiKeyDto.Unknown;
    }

    private void Clamp(ref int col, ref int row) =>
        (col, row) = (
            Math.Clamp(col, 0, Math.Max(0, _screenCols - 1)),
            Math.Clamp(row, 0, Math.Max(0, _screenRows - 1)));
}

/// <summary>
/// Wheel-only pointer target that forwards ticks to a host callback (CF-C-002):
/// bind it to the timeline rect and wheel events flow out as framework-neutral
/// <see cref="UiKeyDto" /> values via <see cref="MouseRouter.WheelToKey" />.
/// Press/release are intentional no-ops (selection lives elsewhere). The callback
/// is <c>Action&lt;UiKeyDto&gt;</c> rather than a store dispatch because the engine
/// does not know what a scroll key means — the host does, and converts through
/// <c>KeyEventAdapter</c>, the single Rendering→State crossing point (#33/T2).
/// AOT-clean: no reflection, no allocations beyond the DTO itself.
/// </summary>
public sealed class TimelineWheelTarget : IPointerTarget
{
    private readonly Action<UiKeyDto> _dispatch;

    /// <summary>Create a wheel-forwarding target bound to a timeline rect.</summary>
    /// <param name="id">Target id for hit-test diagnostics; falls back to "timeline-wheel".</param>
    /// <param name="dispatch">
    /// Host sink for the mapped key, e.g. <c>key =&gt; { _ = store.Dispatch(WheelMsg(key)); }</c>.
    /// </param>
    public TimelineWheelTarget(string id, Action<UiKeyDto> dispatch)
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

    public void OnWheel(int col, int row, int delta) => _dispatch(MouseRouter.WheelToKey(delta));
}
