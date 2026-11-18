namespace Harbor.Tui.CellForge.Rendering;

/// <summary>Per-tile visibility state reported by <see cref="VisibilityMap"/>.</summary>
public enum TileVisibility : byte
{
    /// <summary>Never seen by the player.</summary>
    Unexplored = 0,

    /// <summary>Seen before, currently outside the field of view.</summary>
    Explored = 1,

    /// <summary>Inside the field of view right now.</summary>
    Visible = 2,

    /// <summary>World coordinate outside the map bounds.</summary>
    Outside = 3,
}

/// <summary>
/// World-space fog-of-war buffer: a visible flag and an explored flag per tile,
/// addressed by world coordinates. The field-of-view algorithm stays with the
/// game; this type only stores its result so lighting, minimaps and save/restore
/// can read the same state. Two flat byte arrays, no allocation on read and no
/// reflection or dynamic code (BCL-only, AOT-safe).
/// </summary>
public sealed class VisibilityMap
{
    private readonly byte[] _visible;
    private readonly byte[] _explored;
    private int _visibleCount;
    private int _exploredCount;

    /// <summary>Creates a map covering <paramref name="bounds"/> in world coordinates.</summary>
    public VisibilityMap(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Visibility map bounds must have positive width and height.");
        }
        if ((long)bounds.X + bounds.Width > int.MaxValue || (long)bounds.Y + bounds.Height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Visibility map bounds overflow the integer coordinate space.");
        }

        var area = (long)bounds.Width * bounds.Height;
        if (area > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Visibility map is too large.");
        }

        Bounds = bounds;
        _visible = new byte[(int)area];
        _explored = new byte[(int)area];
    }

    /// <summary>World-space rectangle this map covers.</summary>
    public Rect Bounds { get; }

    /// <summary>Number of tiles currently visible.</summary>
    public int VisibleCount => _visibleCount;

    /// <summary>Number of tiles explored at least once (including visible ones).</summary>
    public int ExploredCount => _exploredCount;

    /// <summary>True when the world coordinate falls inside <see cref="Bounds"/>.</summary>
    public bool Contains(int worldX, int worldY) =>
        (long)worldX >= Bounds.X && worldX < (long)Bounds.X + Bounds.Width &&
        (long)worldY >= Bounds.Y && worldY < (long)Bounds.Y + Bounds.Height;

    /// <summary>Returns the combined state of a world tile.</summary>
    public TileVisibility StateAt(int worldX, int worldY)
    {
        if (!Contains(worldX, worldY))
        {
            return TileVisibility.Outside;
        }

        var index = Index(worldX, worldY);
        if (_visible[index] != 0)
        {
            return TileVisibility.Visible;
        }

        return _explored[index] != 0 ? TileVisibility.Explored : TileVisibility.Unexplored;
    }

    /// <summary>True when the tile is in the current field of view.</summary>
    public bool IsVisible(int worldX, int worldY) =>
        Contains(worldX, worldY) && _visible[Index(worldX, worldY)] != 0;

    /// <summary>True when the tile has ever been in the field of view.</summary>
    public bool IsExplored(int worldX, int worldY) =>
        Contains(worldX, worldY) && _explored[Index(worldX, worldY)] != 0;

    /// <summary>
    /// Puts a tile in the field of view. Visible tiles are explored by definition.
    /// Returns false when the coordinate is outside the map or already visible.
    /// </summary>
    public bool Reveal(int worldX, int worldY)
    {
        if (!Contains(worldX, worldY))
        {
            return false;
        }

        var index = Index(worldX, worldY);
        if (_visible[index] != 0)
        {
            return false;
        }

        _visible[index] = 1;
        _visibleCount++;
        if (_explored[index] == 0)
        {
            _explored[index] = 1;
            _exploredCount++;
        }

        return true;
    }

    /// <summary>
    /// Takes a tile out of the field of view without forgetting it.
    /// Returns false when the coordinate is outside the map or not visible.
    /// </summary>
    public bool Hide(int worldX, int worldY)
    {
        if (!Contains(worldX, worldY))
        {
            return false;
        }

        var index = Index(worldX, worldY);
        if (_visible[index] == 0)
        {
            return false;
        }

        _visible[index] = 0;
        _visibleCount--;
        return true;
    }

    /// <summary>Drops every visible flag but keeps the explored history.</summary>
    public void ClearVisible()
    {
        Array.Clear(_visible);
        _visibleCount = 0;
    }

    /// <summary>Drops both visible and explored flags.</summary>
    public void Reset()
    {
        ClearVisible();
        Array.Clear(_explored);
        _exploredCount = 0;
    }

    private int Index(int worldX, int worldY) =>
        (worldY - Bounds.Y) * Bounds.Width + (worldX - Bounds.X);
}
