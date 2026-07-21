using Harbor.Ui.Framework.State;
using TerminalColor = Terminal.Gui.Drawing.Color;

namespace Harbor.Tui.TerminalGui.Rendering;
/// <summary>
///     Maps a <see cref="ChatRole" /> to a Terminal.Gui v2
///     <see cref="TerminalColor" /> + label, matching the SpectreTui palette
///     so all four renderers paint the same role with the same hue.
/// </summary>
public static class TerminalGuiColorMapper
{
    /// <summary>Terminal.Gui color used for the role's body text.</summary>
    public static TerminalColor ToColor(ChatRole role) => role switch
    {
        ChatRole.User => TerminalColor.BrightGreen,
        ChatRole.Assistant => TerminalColor.White,
        ChatRole.Thinking => TerminalColor.DarkGray,
        ChatRole.Tool => TerminalColor.BrightBlue,
        ChatRole.ToolResult => TerminalColor.Gray,
        ChatRole.System => TerminalColor.Gray,
        ChatRole.Error => TerminalColor.BrightRed,
        _ => TerminalColor.White
    };

    /// <summary>Header label shown in the <c>─ role ─</c> band.</summary>
    public static string ToLabel(ChatRole role) => role switch
    {
        ChatRole.User => "you",
        ChatRole.Assistant => "assistant",
        ChatRole.Thinking => "thinking",
        ChatRole.Tool => "tool",
        ChatRole.ToolResult => "result",
        ChatRole.System => "system",
        ChatRole.Error => "error",
        _ => "msg"
    };

    /// <summary>True if the role's body should be rendered with markdown spans.</summary>
    public static bool SupportsMarkdown(ChatRole role) =>
        role is ChatRole.Assistant or ChatRole.User or ChatRole.System;
}
