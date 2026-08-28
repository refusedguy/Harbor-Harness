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

    private void Clamp(ref int col, ref int row) =>
        (col, row) = (
            Math.Clamp(col, 0, Math.Max(0, _screenCols - 1)),
            Math.Clamp(row, 0, Math.Max(0, _screenRows - 1)));
}
