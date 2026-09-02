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

    internal static bool TryParseHex(string hex, out RgbColor color) => ThemeJson.TryParseHex(hex, out color);
}
