using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     The key events the CellForge dialog suites type with.
/// </summary>
/// <remarks>
///     <para>
///         A dialog is driven by synthetic key presses, and each suite built its
///         own. Two of them are here: the printable key and the special key, and
///         the typing loop that feeds them to a dialog.
///     </para>
/// </remarks>
internal static class Keys
{
    // DialogOverlay approval editing only inspects Key for navigation —
    // any other Key value types KeyChar verbatim into the reason field.
    internal static ConsoleKeyInfo CharKey(char c) => new(c, ConsoleKey.A, false, false, false);

    internal static ConsoleKeyInfo SpecialKey(ConsoleKey key) => new('\0', key, false, false, false);

    internal static void Type(DialogOverlay dialog, string text)
    {
        foreach (char c in text)
        {
            _ = dialog.HandleKey(CharKey(c));
        }
    }
}
