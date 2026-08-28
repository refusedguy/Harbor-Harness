using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Copy-on-select (killer features §P6.4): a left press anchors a rectangular
/// selection, drag extends it, release extracts the covered text — the host
/// hands it to <see cref="Osc52Clipboard"/>. Extraction skips wide-tail cells
/// (<see cref="Cell.WSkip"/>), trims per-row trailing whitespace and joins
/// rows with <c>'\n'</c>; a press that never became a drag selects nothing.
/// <see cref="Paint" /> marks the live region with <see cref="StyleAttr.Reverse" />
/// each frame — the next full repaint clears it (transient by design).
/// </summary>
public sealed class SelectionEngine
{
    private (int X, int Y)? _anchor;
    private (int X, int Y)? _extent;

    /// <summary>True while a selection is being tracked (press seen, release pending).</summary>
    public bool IsActive => _anchor is not null;

    /// <summary>Left press starts a selection — true when claimed; other buttons pass through.</summary>
    public bool OnPress(int x, int y, MouseButton button)
    {
        if (button != MouseButton.Left)
        {
            return false;
        }

        _anchor = (x, y);
        _extent = (x, y);
        return true;
    }

    /// <summary>Drag extends the active selection; no-op otherwise.</summary>
    public void OnDrag(int x, int y)
    {
        if (_anchor is not null)
        {
            _extent = (x, y);
        }
    }

    /// <summary>
    /// Release finalizes: returns the selected text, or null when the press
    /// never became a drag or the region holds no visible text. Resets state.
    /// Coordinates and the cell getter are clamped to the live buffer by the caller.
    /// </summary>
    public string? OnRelease(int x, int y, int cols, int rows, Func<int, int, Cell> cellAt)
    {
        ArgumentNullException.ThrowIfNull(cellAt);
        if (_anchor is not { } anchor)
        {
            return null;
        }

        _anchor = null;
        _extent = null;

        int ax = Math.Clamp(anchor.X, 0, cols - 1);
        int ay = Math.Clamp(anchor.Y, 0, rows - 1);
        int ex = Math.Clamp(x, 0, cols - 1);
        int ey = Math.Clamp(y, 0, rows - 1);
        if (ax == ex && ay == ey)
        {
            return null; // plain click — never a drag, nothing to copy
        }

        int x0 = Math.Min(ax, ex);
        int x1 = Math.Max(ax, ex);
        int y0 = Math.Min(ay, ey);
        int y1 = Math.Max(ay, ey);

        var sb = new StringBuilder((x1 - x0 + 1) * (y1 - y0 + 1));
        for (int row = y0; row <= y1; row++)
        {
            int rowStart = sb.Length;
            for (int col = x0; col <= x1; col++)
            {
                var cell = cellAt(col, row);
                if (cell.Width == Cell.WSkip)
                {
                    continue;
                }

                sb.Append(char.ConvertFromUtf32(cell.Rune));
            }

            int end = sb.Length;
            while (end > rowStart && char.IsWhiteSpace(sb[end - 1]))
            {
                end--;
            }

            sb.Length = end;
            if (row < y1 && sb.Length > rowStart)
            {
                sb.Append('\n');
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Normalized selection rectangle in [0..cols)×[0..rows), or null while inactive.</summary>
    public Rect? NormalizedRect(int cols, int rows)
    {
        if (_anchor is not { } anchor || _extent is not { } extent)
        {
            return null;
        }

        int ax = Math.Clamp(anchor.X, 0, cols - 1);
        int ay = Math.Clamp(anchor.Y, 0, rows - 1);
        int ex = Math.Clamp(extent.X, 0, cols - 1);
        int ey = Math.Clamp(extent.Y, 0, rows - 1);
        return new Rect(
            Math.Min(ax, ex),
            Math.Min(ay, ey),
            Math.Abs(ax - ex) + 1,
            Math.Abs(ay - ey) + 1);
    }

    /// <summary>Paints the Reverse attribute over the live selection region.</summary>
    public void Paint(ScreenBuffer buffer)
    {
        if (NormalizedRect(buffer.Cols, buffer.Rows) is not { } rect)
        {
            return;
        }

        for (int row = rect.Y; row < rect.Bottom; row++)
        {
            for (int col = rect.X; col < rect.Right; col++)
            {
                var cell = buffer.Get(col, row);
                var style = new CellStyle(cell.Style.Fg, cell.Style.Bg, cell.Style.Attrs | StyleAttr.Reverse);
                buffer.SetStyleAt(col, row, in style);
            }
        }
    }
}
