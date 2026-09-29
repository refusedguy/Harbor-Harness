using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using TerminalColor = Terminal.Gui.Drawing.Color;

namespace Harbor.Tui.TerminalGui.Rendering;

/// <summary>
///     Maps a <see cref="ChatRole" /> onto a Terminal.Gui v2
///     <see cref="TerminalColor" />.
/// </summary>
/// <remarks>
///     Label, markdown rule and colour slot all come from the single shared
///     <see cref="ChatRolePresentation" /> table (#556); this class only decides
///     what a <see cref="ChatColorSlot" /> looks like in Terminal.Gui's ANSI
///     vocabulary. The four chat backends share the slot, not the colour — their
///     palettes genuinely differ, so no "same hue" claim is made here (see the
///     note in <see cref="ChatRolePresentation" />).
/// </remarks>
public static class TerminalGuiColorMapper
{
    /// <summary>Terminal.Gui color used for the role's body text.</summary>
    public static TerminalColor ToColor(ChatRole role) => ToColor(ChatRolePresentation.Slot(role));

    /// <summary>Terminal.Gui color for a slot — the Terminal.Gui palette table.</summary>
    public static TerminalColor ToColor(ChatColorSlot slot) => slot switch
    {
        ChatColorSlot.User => TerminalColor.BrightGreen,
        ChatColorSlot.Assistant => TerminalColor.White,

        // One grey for the whole muted family (thinking, tool result, system).
        // This was TerminalColor.DarkGray for thinking alone — exactly the drift
        // the removed "same hue" comment used to promise did not exist (#556).
        ChatColorSlot.Muted => TerminalColor.Gray,
        ChatColorSlot.Tool => TerminalColor.BrightBlue,
        ChatColorSlot.Danger => TerminalColor.BrightRed,
        _ => throw ChatRolePresentation.UnhandledSlot(slot)
    };

    /// <summary>Header label shown in the <c>─ role ─</c> band.</summary>
    public static string ToLabel(ChatRole role) => ChatRolePresentation.Label(role);

    /// <summary>True if the role's body should be rendered with markdown spans.</summary>
    public static bool SupportsMarkdown(ChatRole role) => ChatRolePresentation.UsesMarkdown(role);
}
