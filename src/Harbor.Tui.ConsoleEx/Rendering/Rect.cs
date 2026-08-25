namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>Integer rectangle shared by buffer fills, layout and mouse routing.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int x, int y) =>
        x >= X && x < Right && y >= Y && y < Bottom;

    public Rect Intersect(Rect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : default;
    }

    /// <summary>Cell count — the damage-area metric for hint-vs-fullscan choice.</summary>
    public long Area => (long)Width * Height;
}
