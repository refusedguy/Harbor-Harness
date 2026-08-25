namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>Terminal viewport size change notification (columns × rows).</summary>
public readonly struct ResizeSignal(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    public override string ToString() => $"Resize({Width}x{Height})";
}
