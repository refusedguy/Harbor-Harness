using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>JSON shape — camelCase, all slots optional.</summary>
internal sealed record ThemeDto(
    string? Name,
    string? Accent,
    string? Success,
    string? Warning,
    string? Error,
    string? Tool,
    string? System,
    string? User,
    string? Background,
    string? Panel,
    string? Surface,
    string? Surface2,
    string? Border,
    string? Muted,
    string? Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ThemeDto))]
internal sealed partial class ThemeJsonContext : JsonSerializerContext;

/// <summary>
/// Loads custom HDS themes from JSON (Claude-Code pattern): every color slot
/// is optional — omitted slots merge over the currently active theme, so an
/// override file can tweak two accents without redefining the catalog.
/// Hex colors accept <c>#RGB</c> and <c>#RRGGBB</c>. AOT-safe via
/// <see cref="ThemeJsonContext" /> source generation.
/// </summary>
public static partial class JsonThemeLoader
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
        ThemeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeDto);
        }
        catch (Exception ex)
        {
            return CSharpFunctionalExtensions.Result.Failure<HarborTheme>($"theme JSON invalid: {ex.Message}");
        }

        if (dto is null)
        {
            return CSharpFunctionalExtensions.Result.Failure<HarborTheme>("theme JSON empty");
        }

        var errors = new List<string>();
        var accent = ParseSlot(dto.Accent, TerminalColorPalette.Current.Accent, "accent", errors);
        var success = ParseSlot(dto.Success, TerminalColorPalette.Current.Success, "success", errors);
        var warning = ParseSlot(dto.Warning, TerminalColorPalette.Current.Warning, "warning", errors);
        var error = ParseSlot(dto.Error, TerminalColorPalette.Current.Error, "error", errors);
        var tool = ParseSlot(dto.Tool, TerminalColorPalette.Current.Tool, "tool", errors);
        var system = ParseSlot(dto.System, TerminalColorPalette.Current.System, "system", errors);
        var user = ParseSlot(dto.User, TerminalColorPalette.Current.User, "user", errors);
        var background = ParseSlot(dto.Background, TerminalColorPalette.Current.Background, "background", errors);
        var panel = ParseSlot(dto.Panel, TerminalColorPalette.Current.Panel, "panel", errors);
        var surface = ParseSlot(dto.Surface, TerminalColorPalette.Current.Surface, "surface", errors);
        var surface2 = ParseSlot(dto.Surface2, TerminalColorPalette.Current.Surface2, "surface2", errors);
        var border = ParseSlot(dto.Border, TerminalColorPalette.Current.Border, "border", errors);
        var muted = ParseSlot(dto.Muted, TerminalColorPalette.Current.Muted, "muted", errors);
        var text = ParseSlot(dto.Text, TerminalColorPalette.Current.Text, "text", errors);

        if (errors.Count > 0)
        {
            return CSharpFunctionalExtensions.Result.Failure<HarborTheme>(string.Join("; ", errors));
        }

        string name = string.IsNullOrWhiteSpace(dto.Name) ? "custom" : dto.Name.Trim();
        return CSharpFunctionalExtensions.Result.Success(new HarborTheme(
            name, accent, success, warning, error, tool, system, user,
            background, panel, surface, surface2, border, muted, text));
    }

    private static RgbColor ParseSlot(string? hex, RgbColor fallback, string slot, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        if (TryParseHex(hex, out var color))
        {
            return color;
        }

        errors.Add($"invalid hex for '{slot}': {hex}");
        return fallback;
    }

    internal static bool TryParseHex(string hex, out RgbColor color)
    {
        color = default;
        string s = hex.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length == 3 && s.All(char.IsAsciiHexDigit))
        {
            color = new RgbColor(
                (byte)(HexVal(s[0]) * 17),
                (byte)(HexVal(s[1]) * 17),
                (byte)(HexVal(s[2]) * 17));
            return true;
        }

        if (s.Length == 6 && s.All(char.IsAsciiHexDigit))
        {
            color = new RgbColor(
                (byte)((HexVal(s[0]) << 4) | HexVal(s[1])),
                (byte)((HexVal(s[2]) << 4) | HexVal(s[3])),
                (byte)((HexVal(s[4]) << 4) | HexVal(s[5])));
            return true;
        }

        return false;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}
