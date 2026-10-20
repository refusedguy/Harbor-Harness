using System.Text;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Types text into the command palette.
/// </summary>
/// <remarks>
///     <para>
///         The command palette is driven by key presses, and this project and the
///         CLI suite each typed into one. The two copies cannot share a home:
///         <c>CommandPaletteView</c> lives in <c>Harbor.Tui.CellForge</c>, and the
///         shared kit that reaches the palette's engine would have to pull that
///         assembly into every consumer of the kit to hold this one helper.
///     </para>
/// </remarks>
internal static class PaletteKeys
{
    internal static void Type(CommandPaletteView palette, string text)
    {
        foreach (char c in text)
        {
            _ = palette.HandleKey(KeyEvent.Char(new Rune(c)));
        }
    }
}
