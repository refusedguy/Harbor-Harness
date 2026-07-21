using Harbor.Desktop.Abstractions.Models;
namespace Harbor.Desktop.DesignSystem.Themes;
/// <summary>
///     Catppuccin-Mocha (dark) theme as a flat <c>Dictionary&lt;string,string&gt;</c>
///     of hex strings. Each platform app consumes this to populate its own
///     resource dictionary / CSS variables.
/// </summary>
public static class DarkTheme
{

    /// <summary>The flat token map. Keys are semantic names ("AppBackground", "Accent", etc.).</summary>
    public static readonly IReadOnlyDictionary<string, string> Tokens = new Dictionary<string, string>
    {
        ["AppBackground"] = "#1E1E2E",
        ["PanelBackground"] = "#181825",
        ["SidebarBackground"] = "#11111B",
        ["StatusBarBackground"] = "#181825",
        ["CardBackground"] = "#313244",
        ["HoverBackground"] = "#313244",
        ["SelectedBackground"] = "#45475A",
        ["Border"] = "#313244",

        ["Text"] = "#CDD6F4",
        ["TextSubtle"] = "#A6ADC8",

        ["Accent"] = "#89B4FA",
        ["AccentForeground"] = "#11111B",

        ["Success"] = "#A6E3A1",
        ["Warning"] = "#FAB387",
        ["Error"] = "#F38BA8",

        ["ChatUser"] = "#89DCEB",
        ["ChatAssistant"] = "#CDD6F4",
        ["ChatThinking"] = "#9399B2",
        ["ChatTool"] = "#89B4FA",
        ["ChatToolResult"] = "#A6E3A1",
        ["ChatSystem"] = "#F9E2AF",
        ["ChatError"] = "#F38BA8",

        ["StatusIdle"] = "#A6ADC8",
        ["StatusRunning"] = "#89B4FA",
        ["StatusCompact"] = "#F9E2AF",
        ["StatusError"] = "#F38BA8"
    };
    /// <summary>The theme kind — always <see cref="ThemeKind.Dark" />.</summary>
    public static ThemeKind Kind => ThemeKind.Dark;
}
