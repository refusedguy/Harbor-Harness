using Harbor.Ui.Framework.State;
using SpectreColor = Spectre.Console.Color;

namespace Harbor.Tui.RazorConsole.Rendering;
/// <summary>
///     Maps a <see cref="ChatRole" /> to a Spectre.Console <see cref="SpectreColor" />
///     + label, matching the SpectreTui ChatMessageFormatter palette so all four
///     renderers paint the same role with the same hue. RazorConsole layers on
///     top of Spectre for its markup pipeline.
/// </summary>
public static class RazorColorMapper
{
    /// <summary>Spectre color used for the role's body text.</summary>
    public static SpectreColor ToColor(ChatRole role) => role switch
    {
        ChatRole.User => SpectreColor.Green,
        ChatRole.Assistant => SpectreColor.White,
        ChatRole.Thinking => SpectreColor.Grey,
        ChatRole.Tool => SpectreColor.Blue,
        ChatRole.ToolResult => SpectreColor.Grey,
        ChatRole.System => SpectreColor.Grey,
        ChatRole.Error => SpectreColor.Red,
        _ => SpectreColor.White
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

    /// <summary>Spectre markup string for the role's body color, e.g. <c>[green]…[/]</c>.</summary>
    public static string ToMarkup(ChatRole role) => role switch
    {
        ChatRole.User => "green",
        ChatRole.Assistant => "white",
        ChatRole.Thinking => "grey italic",
        ChatRole.Tool => "blue",
        ChatRole.ToolResult => "grey",
        ChatRole.System => "grey",
        ChatRole.Error => "red",
        _ => "white"
    };

    /// <summary>True if the role's body should be rendered with markdown spans.</summary>
    public static bool SupportsMarkdown(ChatRole role) =>
        role is ChatRole.Assistant or ChatRole.User or ChatRole.System;
}
