using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// First visible generic composition (epic #1195, part of #1225): every
/// neutral widget painted onto one screen with no chat state, no chat
/// layout and no plugin axis. Paints directly into a caller-owned
/// <see cref="ScreenBuffer"/> — deliberately not through the shipped chat
/// layout factory, which keeps the widget-axis closure intact.
/// </summary>
public static class GenericWidgetsDemo
{
    /// <summary>Demo title line.</summary>
    public const string Title = "Generic widgets — neutral set (no chat)";

    /// <summary>Builds the demo screen at <paramref name="cols"/>×<paramref name="rows"/>.</summary>
    public static ScreenBuffer Build(int cols = 80, int rows = 24)
    {
        var buffer = new ScreenBuffer(Math.Max(20, cols), Math.Max(16, rows));
        Paint(buffer, new Rect(0, 0, buffer.Cols, buffer.Rows));
        return buffer;
    }

    /// <summary>Paints the demo into <paramref name="buffer"/> (all widgets, fixed sample state).</summary>
    public static void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var titleStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var sectionStyle = new CellStyle(ChatPalette.Muted);
        buffer.SetText(rect.X, rect.Y, Title.AsSpan(0, Math.Min(Title.Length, rect.Width)), titleStyle);

        int y = rect.Y + 2;
        int w = rect.Width;
        if (w < 20)
        {
            return;
        }

        y = PaintSection(buffer, rect.X, y, w, rect, "TextField", sectionStyle);
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        var field = new GenericTextField { Placeholder = "Type here…" };
        field.SetText("hello cells");
        field.IsFocused = false;
        field.Paint(buffer, new Rect(rect.X + 2, y, w - 2, 1));
        y += 2;

        y = PaintSection(buffer, rect.X, y, w, rect, "SelectionList", sectionStyle);
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        var list = new GenericSelectionList { MaxVisibleRows = 3 };
        list.SetItems(["alpha", "beta", "gamma", "delta"]);
        list.MoveTo(1);
        int listH = Math.Min(3, rect.Y + rect.Height - y);
        if (listH > 0)
        {
            list.Paint(buffer, new Rect(rect.X + 2, y, w - 2, listH));
            y += listH + 1;
        }

        y = PaintSection(buffer, rect.X, y, w, rect, "Progress + Spinner", sectionStyle);
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        GenericProgressBar.Paint(buffer, new Rect(rect.X + 2, y, Math.Max(0, w - 6), 1), new GenericProgressState(0.42));
        new GenericSpinner().Paint(buffer, new Rect(rect.X + w - 3, y, 1, 1));
        y += 2;

        y = PaintSection(buffer, rect.X, y, w, rect, "Table", sectionStyle);
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        var table = new GenericTable();
        table.SetHeaders(["name", "status"], [GenericColumnAlign.Left, GenericColumnAlign.Right]);
        table.AddRow(["alpha", "ok"]);
        table.AddRow(["beta", "run"]);
        int tableH = Math.Min(table.DesiredHeight, rect.Y + rect.Height - y);
        if (tableH > 0)
        {
            table.Paint(buffer, new Rect(rect.X + 2, y, Math.Max(0, w - 2), tableH));
        }
    }

    private static int PaintSection(ScreenBuffer buffer, int x, int y, int w, Rect bounds, string name, CellStyle style)
    {
        if (y >= bounds.Y + bounds.Height)
        {
            return y;
        }

        string caption = "— " + name + " —";
        buffer.SetText(x, y, caption.AsSpan(0, Math.Min(caption.Length, w)), style);
        return y + 1;
    }
}
