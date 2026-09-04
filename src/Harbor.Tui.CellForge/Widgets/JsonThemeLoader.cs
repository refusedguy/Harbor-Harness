using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Loads custom HDS themes from JSON (Claude-Code pattern) — thin CellForge
/// adapter over the canonical <see cref="ThemeJson" /> codec in
/// Harbor.DesignSystem, the single source of truth for the marketplace
/// format. Every color slot is optional — omitted slots merge over the
/// currently active theme, so an override file can tweak two accents without
/// redefining the catalog. Hex colors accept <c>#RGB</c> and <c>#RRGGBB</c>.
/// </summary>
public static class JsonThemeLoader
{
    /// <summary>
    /// Harbor Terminal default (CF-E-016): the HDS v1 terminal token catalog
    /// pinned 1-to-1 from
    /// <c>apps/Harbor.App.Avalonia/Themes/Hds/HarborDesignTokens.axaml</c>
    /// ("HarborDesignTokens" dictionary). Mapping (axaml key → slot):
    /// AccentColor #39BAE6 → Accent; SuccessColor #7FD962 → Success;
    /// WarningColor #FFB454 → Warning; ErrorColor #FF6B6B → Error;
    /// MochaMauve #D2A6FF → Tool; MochaPeach #F29668 → System;
    /// ChatUserBrush #39BAE6 → User; AppBackgroundBrush #0A0E14 → Background;
    /// PanelBackgroundBrush #0D1117 → Panel; CardBackgroundBrush #131820 →
    /// Surface; CardElevatedBackgroundBrush #1A1F2B → Surface2;
    /// BorderBrush #1F2430 → Border; TextMutedBrush #5C6773 → Muted;
    /// TextBrush #B3B9C5 → Text. Derived tones (AccentHover #66C8EC,
    /// AccentPressed #2394C2, Subtext #8D97A3, …) are intentionally NOT pinned —
    /// the axaml marks them "(derived)" and the 14-slot catalog has no slots
    /// for them. Chat roles and CostLow/Mid/High live below as aliases of
    /// these slots (same axaml brush values, no invented hex).
    /// </summary>
    public static HarborTheme Default { get; } = new HarborTheme(
        "harbor-terminal",
        Accent: new RgbColor(0x39, 0xBA, 0xE6),
        Success: new RgbColor(0x7F, 0xD9, 0x62),
        Warning: new RgbColor(0xFF, 0xB4, 0x54),
        Error: new RgbColor(0xFF, 0x6B, 0x6B),
        Tool: new RgbColor(0xD2, 0xA6, 0xFF),
        System: new RgbColor(0xF2, 0x96, 0x68),
        User: new RgbColor(0x39, 0xBA, 0xE6),
        Background: new RgbColor(0x0A, 0x0E, 0x14),
        Panel: new RgbColor(0x0D, 0x11, 0x17),
        Surface: new RgbColor(0x13, 0x18, 0x20),
        Surface2: new RgbColor(0x1A, 0x1F, 0x2B),
        Border: new RgbColor(0x1F, 0x24, 0x30),
        Muted: new RgbColor(0x5C, 0x67, 0x73),
        Text: new RgbColor(0xB3, 0xB9, 0xC5));

    /// <summary>Chat user role — axaml <c>ChatUserBrush #39BAE6</c> (= Accent).</summary>
    public static RgbColor ChatUser => Default.Accent;

    /// <summary>Chat assistant role — axaml <c>ChatAssistantBrush #B3B9C5</c> (= Text).</summary>
    public static RgbColor ChatAssistant => Default.Text;

    /// <summary>Chat thinking role — axaml <c>ChatThinkingBrush #5C6773</c> (= Muted).</summary>
    public static RgbColor ChatThinking => Default.Muted;

    /// <summary>Chat tool-call role — axaml <c>ChatToolBrush #D2A6FF</c> (= Tool).</summary>
    public static RgbColor ChatTool => Default.Tool;

    /// <summary>Chat tool-result role — axaml <c>ChatToolResultBrush #7FD962</c> (= Success).</summary>
    public static RgbColor ChatToolResult => Default.Success;

    /// <summary>Chat system role — axaml <c>ChatSystemBrush #F29668</c> (= System).</summary>
    public static RgbColor ChatSystem => Default.System;

    /// <summary>Chat error role — axaml <c>ChatErrorBrush #FF6B6B</c> (= Error).</summary>
    public static RgbColor ChatError => Default.Error;

    /// <summary>Cost low — axaml <c>CostLowBrush/CostLowColor #7FD962</c> (= Success).</summary>
    public static RgbColor CostLow => Default.Success;

    /// <summary>Cost mid — axaml <c>CostMidBrush/CostMidColor #FFB454</c> (= Warning).</summary>
    public static RgbColor CostMid => Default.Warning;

    /// <summary>Cost high — axaml <c>CostHighBrush/CostHighColor #FF6B6B</c> (= Error).</summary>
    public static RgbColor CostHigh => Default.Error;

    /// <summary>Loads and parses a theme file; failure carries the reason.</summary>
    public static CSharpFunctionalExtensions.Result<HarborTheme> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return CSharpFunctionalExtensions.Result.Failure<HarborTheme>($"theme file not found: {path}");
            }

            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return CSharpFunctionalExtensions.Result.Failure<HarborTheme>($"theme load failed: {ex.Message}");
        }
    }

    /// <summary>Parses theme JSON, merging over the active theme for omitted slots.</summary>
    public static CSharpFunctionalExtensions.Result<HarborTheme> Parse(string json)
    {
        var result = ThemeJson.Parse(json, TerminalColorPalette.Current);
        return result.IsSuccess
            ? CSharpFunctionalExtensions.Result.Success(result.Theme)
            : CSharpFunctionalExtensions.Result.Failure<HarborTheme>(result.Error);
    }

    /// <summary>
    /// Parses theme JSON, merging omitted slots over <paramref name="fallback" />
    /// (typically <see cref="Default" />) instead of the active theme, so an
    /// override file resolves deterministically regardless of global theme
    /// state. Never touches <see cref="TerminalColorPalette" />.
    /// </summary>
    public static CSharpFunctionalExtensions.Result<HarborTheme> Parse(string json, HarborTheme fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var result = ThemeJson.Parse(json, fallback);
        return result.IsSuccess
            ? CSharpFunctionalExtensions.Result.Success(result.Theme)
            : CSharpFunctionalExtensions.Result.Failure<HarborTheme>(result.Error);
    }

    internal static bool TryParseHex(string hex, out RgbColor color) => ThemeJson.TryParseHex(hex, out color);
}
