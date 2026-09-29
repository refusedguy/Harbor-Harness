using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Termina.Terminal;

namespace Harbor.Tui.Termina.Rendering;

/// <summary>
///     Maps a <see cref="ChatRole" /> onto Termina's 24-bit RGB palette.
/// </summary>
/// <remarks>
///     Label, markdown rule and colour slot all come from the single shared
///     <see cref="ChatRolePresentation" /> table (#556); this class only decides
///     what a <see cref="ChatColorSlot" /> looks like in Termina. The four chat
///     backends share the slot, not the colour — their palettes genuinely
///     differ, so no "same hue" claim is made here (see the note in
///     <see cref="ChatRolePresentation" />).
/// </remarks>
public static class TerminaColorMapper
{
    /// <summary>Termina color used for the role's body text.</summary>
    public static Color ToColor(ChatRole role) => ToColor(ChatRolePresentation.Slot(role));

    /// <summary>Termina color for a slot — the Termina palette table.</summary>
    public static Color ToColor(ChatColorSlot slot) => slot switch
    {
        ChatColorSlot.User => Color.Green,
        ChatColorSlot.Assistant => Color.White,

        // One grey for the whole muted family (thinking, tool result, system).
        // This was Color.DarkGray for thinking alone — exactly the drift the
        // removed "same hue" comment used to promise did not exist (#556).
        ChatColorSlot.Muted => Color.Gray,
        ChatColorSlot.Tool => Color.Blue,
        ChatColorSlot.Danger => Color.Red
    };

    /// <summary>Header label shown in the <c>─ role ─</c> band.</summary>
    public static string ToLabel(ChatRole role) => ChatRolePresentation.Label(role);

    /// <summary>True if the role's body should be rendered with markdown spans.</summary>
    public static bool SupportsMarkdown(ChatRole role) => ChatRolePresentation.UsesMarkdown(role);
}
