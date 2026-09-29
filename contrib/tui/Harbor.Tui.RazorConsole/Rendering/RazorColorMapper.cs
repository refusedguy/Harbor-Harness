using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using SpectreColor = Spectre.Console.Color;

namespace Harbor.Tui.RazorConsole.Rendering;

/// <summary>
///     Maps a <see cref="ChatRole" /> onto RazorConsole's Spectre palette.
/// </summary>
/// <remarks>
///     Label, markdown rule and colour slot all come from the single shared
///     <see cref="ChatRolePresentation" /> table (#556); this class only decides
///     what a <see cref="ChatColorSlot" /> looks like in Spectre markup. The four
///     chat backends share the slot, not the colour — their palettes genuinely
///     differ, so no "same hue" claim is made here (see the note in
///     <see cref="ChatRolePresentation" />).
/// </remarks>
public static class RazorColorMapper
{
    /// <summary>Spectre color used for the role's body text.</summary>
    public static SpectreColor ToColor(ChatRole role) => ToColor(ChatRolePresentation.Slot(role));

    /// <summary>Spectre color for a slot — the RazorConsole palette table.</summary>
    public static SpectreColor ToColor(ChatColorSlot slot)
    {
        // Every arm named, no wildcard (docs/PATTERNS.md §"Type unions"): an
        // unhandled slot falls out of the switch and hits the throw below.
        switch (slot)
        {
            case ChatColorSlot.User: return SpectreColor.Green;
            case ChatColorSlot.Assistant: return SpectreColor.White;
            case ChatColorSlot.Muted: return SpectreColor.Grey;
            case ChatColorSlot.Tool: return SpectreColor.Blue;
            case ChatColorSlot.Danger: return SpectreColor.Red;
        }

        throw ChatRolePresentation.UnhandledSlot(slot);
    }

    /// <summary>Header label shown in the <c>─ role ─</c> band.</summary>
    public static string ToLabel(ChatRole role) => ChatRolePresentation.Label(role);

    /// <summary>Spectre markup string for the role's body color, e.g. <c>[green]…[/]</c>.</summary>
    public static string ToMarkup(ChatRole role)
    {
        string hue = ToMarkup(ChatRolePresentation.Slot(role));

        // Italic is decoration, not hue: RazorConsole is the only backend that
        // italicises thinking output, so it is layered on top of the slot's hue
        // instead of being a second hand-maintained per-role colour table.
        return role is ChatRole.Thinking ? $"{hue} italic" : hue;
    }

    private static string ToMarkup(ChatColorSlot slot)
    {
        switch (slot)
        {
            case ChatColorSlot.User: return "green";
            case ChatColorSlot.Assistant: return "white";
            case ChatColorSlot.Muted: return "grey";
            case ChatColorSlot.Tool: return "blue";
            case ChatColorSlot.Danger: return "red";
        }

        throw ChatRolePresentation.UnhandledSlot(slot);
    }

    /// <summary>True if the role's body should be rendered with markdown spans.</summary>
    public static bool SupportsMarkdown(ChatRole role) => ChatRolePresentation.UsesMarkdown(role);
}
