using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// DECSCUSR cursor shapes («CSI Pn q»). Block shapes are used by vim NORMAL
/// mode, bar shapes by insert mode, underline as a third distinct shape.
/// </summary>
public static class DecscusrCursorShape
{
    public enum Shape
    {
        BlinkingBlock = 1,
        SteadyBlock = 2,
        BlinkingUnderline = 3,
        SteadyUnderline = 4,
        BlinkingBar = 5,
        SteadyBar = 6,
    }

    public static byte[] Format(Shape shape) =>
        Encoding.ASCII.GetBytes($"\u001B[{(int)shape}q");
}

/// <summary>
/// Per-session DECSCUSR emitter. Only emits when the shape actually changes
/// and the probe confirmed support, so an unsupported terminal keeps the
/// cursor it had (#2219).
/// </summary>
public sealed class CursorShapeEmitter
{
    private byte[]? _pending;
    private DecscusrCursorShape.Shape? _last;

    public bool Enabled { get; set; }

    public void Set(DecscusrCursorShape.Shape shape)
    {
        if (!Enabled || _last == shape)
        {
            return;
        }

        _last = shape;
        _pending = DecscusrCursorShape.Format(shape);
    }

    public void Emit(AnsiWriter writer)
    {
        if (_pending is { } bytes)
        {
            writer.RawBytes(bytes);
            _pending = null;
        }
    }
}
