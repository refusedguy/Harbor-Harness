using Harbor.Desktop.Abstractions.Models;

namespace Harbor.Desktop.DesignSystem.Themes;

/// <summary>
///     Catppuccin-Latte (light) theme as a flat <c>Dictionary&lt;string,string&gt;</c>
///     of hex strings. Each platform app consumes this to populate its own
///     resource dictionary / CSS variables.
/// </summary>
public static class LightTheme
{
    /// <summary>The theme kind — always <see cref="ThemeKind.Light"/>.</summary>
    public static ThemeKind Kind => ThemeKind.Light;

    /// <summary>The flat token map. Keys are semantic names ("AppBackground", "Accent", etc.).</summary>
    public static readonly IReadOnlyDictionary<string, string> Tokens = new Dictionary<string, string>
    {
        ["AppBackground"] = "#EFF1F5",
        ["PanelBackground"] = "#E6E9EF",
        ["SidebarBackground"] = "#DCE0E8",
        ["StatusBarBackground"] = "#E6E9EF",
        ["CardBackground"] = "#CCD0DA",
        ["HoverBackground"] = "#CCD0DA",
        ["SelectedBackground"] = "#BCC0CC",
        ["Border"] = "#CCD0DA",

        ["Text"] = "#4C4F69",
        ["TextSubtle"] = "#6C6F85",

        ["Accent"] = "#1E66F5",
        ["AccentForeground"] = "#EFF1F5",

        ["Success"] = "#40A02B",
        ["Warning"] = "#FE640B",
        ["Error"] = "#D20F39",

        ["ChatUser"] = "#04A5EC",
        ["ChatAssistant"] = "#4C4F69",
        ["ChatThinking"] = "#7C7F93",
        ["ChatTool"] = "#1E66F5",
        ["ChatToolResult"] = "#40A02B",
        ["ChatSystem"] = "#DF8E1D",
        ["ChatError"] = "#D20F39",

        ["StatusIdle"] = "#6C6F85",
        ["StatusRunning"] = "#1E66F5",
        ["StatusCompact"] = "#DF8E1D",
        ["StatusError"] = "#D20F39",
    };
}
