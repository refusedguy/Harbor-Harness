using Harbor.Ui.Framework.State;
using Termina.Terminal;
namespace Harbor.Tui.Termina.Rendering;
/// <summary>
///     Maps a <see cref="ChatRole" /> to its Termina <see cref="Color" /> and
///     human-readable label, matching the SpectreTui ChatMessageFormatter palette
///     so all four renderers paint the same role with the same hue.
/// </summary>
public static class TerminaColorMapper
{
    /// <summary>Termina color used for the role's body text.</summary>
    public static Color ToColor(ChatRole role) => role switch
    {
        ChatRole.User => Color.Green,
        ChatRole.Assistant => Color.White,
        ChatRole.Thinking => Color.DarkGray,
        ChatRole.Tool => Color.Blue,
        ChatRole.ToolResult => Color.Gray,
        ChatRole.System => Color.Gray,
        ChatRole.Error => Color.Red,
        _ => Color.White
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
