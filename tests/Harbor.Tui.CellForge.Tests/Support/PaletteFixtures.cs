using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Builds a command palette with a known set of items.
/// </summary>
/// <remarks>
///     <para>
///         The palette suites need a palette with a known number of entries, and
///         each wrote the same item loop.
///     </para>
/// </remarks>
internal static class PaletteFixtures
{
    internal static CommandPaletteView OpenMany(int count)
    {
        var items = new CommandItem[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new CommandItem("cmd" + i, "Command " + i.ToString("00"));
        }

        var palette = new CommandPaletteView();
        palette.Show(items);
        return palette;
    }
}
