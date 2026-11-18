// Chat-owned post effect on the engine contract, next to GlowEffect. Grid
// types are engine-qualified because assembly-wide aliases pin the short
// names to the shared Rendering vocabulary (the same shape GlowEffect uses).
using EngineCells = Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Drop shadow for dialogs, palettes and toasts (#2696, item 12). The effect
/// darkens a rectangle shifted from the dialog by a fixed offset and leaves
/// the dialog's own rect alone, so the surface reads as floating above the
/// transcript. It implements the same cell-pure, allocation-free contract as
/// <see cref="GlowEffect"/>: a host arms it in a <see cref="PostFxPipeline"/>
/// slot and refreshes the parameters once per frame.
/// </summary>
public sealed class ShadowEffect : IPostEffect
{
    private EngineCells.Rect _dialog;
    private EngineCells.Rect _region;
    private double _strength;

    /// <summary>The shifted rect the pipeline clips to.</summary>
    public EngineCells.Rect Region => _region;

    /// <summary>
    /// Refreshes the effect for one frame. Only three value fields change, so
    /// arming it on a steady frame allocates nothing.
    /// </summary>
    public void Update(EngineCells.Rect dialog, int offsetX, int offsetY, double strength)
    {
        _dialog = dialog;
        _region = new EngineCells.Rect(
            dialog.X + offsetX,
            dialog.Y + offsetY,
            dialog.Width,
            dialog.Height);
        _strength = double.Clamp(strength, 0.0, 1.0);
    }

    /// <inheritdoc />
    public EngineCells.Cell Transform(int x, int y, in EngineCells.Cell cell)
    {
        if (_strength <= 0.0
            || !_region.Contains(x, y)
            || _dialog.Contains(x, y))
        {
            return cell;
        }

        var style = cell.Style;
        return EngineCells.Cell.FromRaw(
            cell.Rune,
            Darken(style.Fg, _strength),
            Darken(style.Bg, _strength),
            (ushort)(style.Attrs | EngineCells.StyleAttr.Dim),
            cell.Width);
    }

    /// <summary>
    /// Moves a packed color toward black. Terminal-default colors stay
    /// default: there is no palette entry to darken, and writing one would
    /// replace the terminal's own theme with ours.
    /// </summary>
    private static uint Darken(EngineCells.PackedColor color, double strength)
    {
        if (color.IsDefault)
        {
            return color.Value;
        }

        double keep = 1.0 - strength;
        if (color.IsRgb)
        {
            var (r, g, b) = color.RgbChannels;
            return EngineCells.PackedColor.Rgb(
                (byte)(r * keep),
                (byte)(g * keep),
                (byte)(b * keep)).Value;
        }

        // Index 0 is black in the standard palette ramp.
        return EngineCells.PackedColor.Indexed((byte)(color.Index * keep)).Value;
    }
}
