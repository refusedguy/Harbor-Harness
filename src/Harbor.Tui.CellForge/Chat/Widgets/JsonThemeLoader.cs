using CSharpFunctionalExtensions;
using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     Static parser for terminal JSON theme documents. Pure: parses a theme
///     document held in memory and returns the merged <see cref="HarborTheme" />
///     — it holds no state, applies nothing, and touches no disk.
/// </summary>
/// <remarks>
///     <para>
///         This type deliberately does <em>not</em> implement
///         <see cref="Harbor.Ui.Framework.Services.IThemeService" /> (or its
///         read / apply / watch role interfaces): the terminal renderer drives
///         <c>TerminalColorPalette</c> directly, so there is no apply / watch role
///         for it to honour. Previously it declared the fat interface anyway and
///         threw <see cref="NotImplementedException" /> from every apply member —
///         which crashed the moment anything registered it (see #469).
///     </para>
///     <para>
///         It also no longer reads the disk (#668). It used to carry a public
///         static <c>LoadFile(string)</c> that read a file and parsed it — the
///         second implementation of what <see cref="IThemeStore" /> already did,
///         reachable from Presentation without naming anything. Reading a theme
///         file is now <see cref="IThemeStore.LoadFile" />, and
///         <c>ThemeFileWatcher</c> takes the store as a constructor argument.
///         What is left here is the part that is genuinely this type's: the
///         terminal palette, and a pure parse over a string.
///     </para>
/// </remarks>
public static class JsonThemeLoader
{
    /// <summary>Built-in Harbor terminal palette, used as the merge fallback.</summary>
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

    public static RgbColor ChatUser => Default.Accent;
    public static RgbColor ChatAssistant => Default.Text;
    public static RgbColor ChatThinking => Default.Muted;
    public static RgbColor ChatTool => Default.Tool;
    public static RgbColor ChatToolResult => Default.Success;
    public static RgbColor ChatSystem => Default.System;
    public static RgbColor ChatError => Default.Error;
    public static RgbColor CostLow => Default.Success;
    public static RgbColor CostMid => Default.Warning;
    public static RgbColor CostHigh => Default.Error;

    /// <summary>Parse a theme JSON string, falling back to the active palette.</summary>
    public static Result<HarborTheme> Parse(string json)
    {
        var result = ThemeJson.Parse(json, TerminalColorPalette.Current);
        return result.IsSuccess
            ? Result.Success(result.Theme)
            : Result.Failure<HarborTheme>(result.Error);
    }

    /// <summary>Parse a theme JSON string, merging missing keys from <paramref name="fallback" />.</summary>
    public static Result<HarborTheme> Parse(string json, HarborTheme fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var result = ThemeJson.Parse(json, fallback);
        return result.IsSuccess
            ? Result.Success(result.Theme)
            : Result.Failure<HarborTheme>(result.Error);
    }

    internal static bool TryParseHex(string hex, out RgbColor color) => ThemeJson.TryParseHex(hex, out color);
}
