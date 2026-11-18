namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Pure 2D camera arithmetic for tile worlds. Holds a world-space origin and a
/// screen viewport size, and offers bounds clamping, deadzone following and
/// world/screen coordinate conversion. No rendering, no field-of-view or
/// following strategy, no shared state: the game decides when to call each
/// method. BCL-only and allocation-free, so it is safe under NativeAOT.
/// </summary>
public sealed class TileCamera
{
    /// <summary>Creates a camera for a viewport measured in terminal cells.</summary>
    public TileCamera(int viewportWidth, int viewportHeight)
    {
        if (viewportWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "Viewport width must be positive.");
        }
        if (viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight), "Viewport height must be positive.");
        }

        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
    }

    /// <summary>World-space x of the viewport's top-left tile.</summary>
    public int OriginX { get; private set; }

    /// <summary>World-space y of the viewport's top-left tile.</summary>
    public int OriginY { get; private set; }

    /// <summary>Viewport width in cells.</summary>
    public int ViewportWidth { get; }

    /// <summary>Viewport height in cells.</summary>
    public int ViewportHeight { get; }

    /// <summary>The visible world rectangle at the current origin.</summary>
    public Rect View => new(OriginX, OriginY, ViewportWidth, ViewportHeight);

    /// <summary>True when a world tile is inside the current view.</summary>
    public bool IsVisible(int worldX, int worldY) => View.Contains(worldX, worldY);

    /// <summary>Converts a world tile to viewport-local cell coordinates.</summary>
    public (int X, int Y) WorldToScreen(int worldX, int worldY) => (worldX - OriginX, worldY - OriginY);

    /// <summary>Converts viewport-local cell coordinates to a world tile. Inverse of <see cref="WorldToScreen"/>.</summary>
    public (int X, int Y) ScreenToWorld(int screenX, int screenY) => (screenX + OriginX, screenY + OriginY);

    /// <summary>Moves the origin directly. Returns true when it changed.</summary>
    public bool MoveTo(int originX, int originY)
    {
        if (originX == OriginX && originY == OriginY)
        {
            return false;
        }

        OriginX = originX;
        OriginY = originY;
        return true;
    }

    /// <summary>
    /// Keeps the view inside <paramref name="worldBounds"/>. When the world is
    /// smaller than the viewport on an axis, the origin aligns to that axis's
    /// bounds origin. Returns true when the origin changed.
    /// </summary>
    public bool ClampTo(Rect worldBounds)
    {
        if (worldBounds.Width <= 0 || worldBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(worldBounds), "World bounds must have positive width and height.");
        }

        var x = ClampAxis(OriginX, worldBounds.X, worldBounds.Width, ViewportWidth);
        var y = ClampAxis(OriginY, worldBounds.Y, worldBounds.Height, ViewportHeight);
        return MoveTo(x, y);
    }

    /// <summary>
    /// Scrolls just enough to bring a world point back inside a deadzone
    /// centered on the viewport. A deadzone of zero centers the point. Returns
    /// true when the origin changed. Does not clamp to world bounds; call
    /// <see cref="ClampTo"/> afterwards when the world is finite.
    /// </summary>
    public bool Follow(int worldX, int worldY, int deadzoneX, int deadzoneY)
    {
        if (deadzoneX < 0 || deadzoneX > ViewportWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(deadzoneX));
        }
        if (deadzoneY < 0 || deadzoneY > ViewportHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(deadzoneY));
        }

        var x = FollowAxis(worldX, OriginX, ViewportWidth, deadzoneX);
        var y = FollowAxis(worldY, OriginY, ViewportHeight, deadzoneY);
        return MoveTo(x, y);
    }

    private static int ClampAxis(int origin, int worldStart, int worldSize, int viewportSize)
    {
        if (worldSize <= viewportSize)
        {
            return worldStart;
        }

        var max = (long)worldStart + worldSize - viewportSize;
        if (origin < worldStart)
        {
            return worldStart;
        }
        if (origin > max)
        {
            return ToInt(max);
        }
        return origin;
    }

    private static int FollowAxis(long target, long origin, int viewportSize, int deadzone)
    {
        long result;
        if (deadzone <= 0)
        {
            result = target - viewportSize / 2;
        }
        else
        {
            var left = (viewportSize - deadzone) / 2;
            var right = left + deadzone;
            var screen = target - origin;
            if (screen < left)
            {
                result = target - left;
            }
            else if (screen >= right)
            {
                result = target - (right - 1);
            }
            else
            {
                result = origin;
            }
        }

        return ToInt(result);
    }

    private static int ToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
