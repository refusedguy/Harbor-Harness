// #3087 (S6): the real consumer of the BCL-only widget catalog. This is a
// runnable reference screen, not a test double: `harbor catalog` composes the
// five declarative widgets into one frame and prints it. No TUI host, renderer
// or Harbor state layer is involved — the engine grid is the whole surface,
// which is exactly the "assemble an arbitrary TUI without chat" case the
// catalog exists for.

using System.Text;
using Harbor.Tui.CellForge.Catalog;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.App.Cli.Commands;

internal static class CafCatalogCommands
{
    private const int Cols = 48;
    private const int Rows = 12;

    /// <summary>Paint the catalog reference screen and print it as plain rows.</summary>
    public static int Catalog()
    {
        var buffer = new ScreenBuffer(Cols, Rows);
        var widgets = WidgetCatalog.Widgets;
        var names = new string[widgets.Count];
        for (int i = 0; i < widgets.Count; i++)
        {
            names[i] = widgets[i].Name;
        }

        WidgetCatalog.Paint(new ParagraphSpec("CellForge widget catalog (BCL-only)"),
            buffer, new Rect(0, 0, Cols, 1));
        WidgetCatalog.Paint(RuleSpec.Dashed(), buffer, new Rect(0, 1, Cols, 1));
        WidgetCatalog.Paint(ListSpec.Create(names, 2), buffer, new Rect(0, 2, Cols, widgets.Count));
        WidgetCatalog.Paint(GaugeSpec.Create("widgets", 1.0), buffer, new Rect(0, 8, Cols, 1));
        WidgetCatalog.Paint(new StatusSpec("surface", "BCL-only / AOT-clean"), buffer, new Rect(0, 10, Cols, 1));

        Console.Out.Write(Plain(buffer));
        return 0;
    }

    private static string Plain(ScreenBuffer buffer)
    {
        var text = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            int end = buffer.Cols;
            while (end > 0 && buffer.Get(end - 1, y).Rune == ' ')
            {
                end--;
            }

            for (int x = 0; x < end; x++)
            {
                Cell cell = buffer.Get(x, y);
                text.Append(cell.Width == Cell.WSkip || cell.Rune is < 0x20 or > 0x7E
                    ? '?'
                    : (char)cell.Rune);
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
