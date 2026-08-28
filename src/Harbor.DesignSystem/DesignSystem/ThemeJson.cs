using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.DesignSystem;

/// <summary>
/// Outcome of parsing theme JSON — never throws. Carries the parsed theme on
/// success, the (joined) fatal error on failure, and non-fatal lint warnings
/// in both cases (unknown properties, WCAG contrast concerns).
/// </summary>
public sealed record ThemeParseResult
{
    /// <summary>True when <see cref="Theme" /> is usable.</summary>
    public required bool IsSuccess { get; init; }

    /// <summary>Parsed theme (default when <see cref="IsSuccess" /> is false).</summary>
    public HarborTheme Theme { get; init; } = HarborTheme.HarborDark;

    /// <summary>Primary fatal reason (empty on success).</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>All fatal problems (parse errors, invalid hex slots).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Non-fatal lint findings — the theme still loads.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Successful outcome.</summary>
    public static ThemeParseResult Ok(HarborTheme theme, IReadOnlyList<string> warnings) => new()
    {
        IsSuccess = true,
        Theme = theme,
        Warnings = warnings,
    };

    /// <summary>Failed outcome — <paramref name="errors" /> is joined into <see cref="Error" />.</summary>
    public static ThemeParseResult Fail(IReadOnlyList<string> errors, IReadOnlyList<string> warnings) => new()
    {
        IsSuccess = false,
        Error = string.Join("; ", errors),
        Errors = errors,
        Warnings = warnings,
    };
}

/// <summary>JSON shape of a theme file — camelCase, all slots optional.</summary>
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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(ThemeDto))]
internal sealed partial class ThemeJsonContext : JsonSerializerContext;

/// <summary>
/// Canonical theme-JSON codec of the HDS marketplace format: every color slot
/// is optional — omitted slots merge over a fallback theme, so an override
/// file can tweak two accents without redefining the catalog. Hex colors
/// accept <c>#RGB</c> and <c>#RRGGBB</c> (leading <c>#</c> optional). AOT-safe
/// via <see cref="ThemeJsonContext" /> source generation.
///
/// On top of fatal validation (malformed JSON, invalid hex) the parser LINTS:
/// unknown properties and WCAG contrast concerns surface as non-fatal
/// <see cref="ThemeParseResult.Warnings" /> — the theme still loads.
/// </summary>
public static class ThemeJson
{
    private const string FallbackName = "custom";

    /// <summary>Reader options mirror the DTO context: trailing commas + comments tolerated.</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly (string Key, Func<HarborTheme, RgbColor> Get)[] Slots =
    [
        ("accent", t => t.Accent),
        ("success", t => t.Success),
        ("warning", t => t.Warning),
        ("error", t => t.Error),
        ("tool", t => t.Tool),
        ("system", t => t.System),
        ("user", t => t.User),
        ("background", t => t.Background),
        ("panel", t => t.Panel),
        ("surface", t => t.Surface),
        ("surface2", t => t.Surface2),
        ("border", t => t.Border),
        ("muted", t => t.Muted),
        ("text", t => t.Text),
    ];

    private static readonly HashSet<string> KnownKeys =
    [
        "name",
        .. Slots.Select(s => s.Key),
    ];

    /// <summary>
    /// Parses theme JSON, merging omitted slots over <paramref name="fallback" />.
    /// Never throws — malformed input returns a failed
    /// <see cref="ThemeParseResult" /> carrying the reason.
    /// </summary>
    public static ThemeParseResult Parse(string json, HarborTheme fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        IReadOnlyList<string> warnings = [];
        ThemeDto? dto;
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);
            warnings = LintUnknownKeys(doc.RootElement);
            dto = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeDto);
        }
        catch (Exception ex)
        {
            return ThemeParseResult.Fail(["theme JSON invalid: " + ex.Message], warnings);
        }

        if (dto is null)
        {
            return ThemeParseResult.Fail(["theme JSON empty"], warnings);
        }

        var errors = new List<string>();
        var values = new Dictionary<string, RgbColor>(StringComparer.Ordinal);
        foreach (var (key, get) in Slots)
        {
            string? hex = GetDtoSlot(dto, key);
            if (string.IsNullOrWhiteSpace(hex))
            {
                values[key] = get(fallback);
            }
            else if (TryParseHex(hex, out var color))
            {
                values[key] = color;
            }
            else
            {
                errors.Add($"invalid hex for '{key}': {hex}");
            }
        }

        if (errors.Count > 0)
        {
            return ThemeParseResult.Fail(errors, warnings);
        }

        string name = string.IsNullOrWhiteSpace(dto.Name) ? FallbackName : dto.Name.Trim();
        var theme = new HarborTheme(
            name,
            values["accent"], values["success"], values["warning"], values["error"],
            values["tool"], values["system"], values["user"],
            values["background"], values["panel"], values["surface"], values["surface2"],
            values["border"], values["muted"], values["text"]);

        warnings = [.. warnings, .. LintContrast(theme)];
        return ThemeParseResult.Ok(theme, warnings);
    }

    /// <summary>Serializes a theme to marketplace-format JSON (indented).</summary>
    public static string Write(HarborTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var dto = new ThemeDto(
            theme.Name,
            Hex(theme.Accent), Hex(theme.Success), Hex(theme.Warning), Hex(theme.Error),
            Hex(theme.Tool), Hex(theme.System), Hex(theme.User),
            Hex(theme.Background), Hex(theme.Panel), Hex(theme.Surface), Hex(theme.Surface2),
            Hex(theme.Border), Hex(theme.Muted), Hex(theme.Text));
        return JsonSerializer.Serialize(dto, ThemeJsonContext.Default.ThemeDto);
    }

    /// <summary>
    /// Parses <c>#RGB</c> / <c>#RRGGBB</c> hex (leading <c>#</c> optional,
    /// case-insensitive). Canonical port shared by every theme loader.
    /// </summary>
    public static bool TryParseHex(string hex, out RgbColor color)
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

    /// <summary>Formats a color as <c>#RRGGBB</c>.</summary>
    public static string Hex(RgbColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string? GetDtoSlot(ThemeDto dto, string key) => key switch
    {
        "accent" => dto.Accent,
        "success" => dto.Success,
        "warning" => dto.Warning,
        "error" => dto.Error,
        "tool" => dto.Tool,
        "system" => dto.System,
        "user" => dto.User,
        "background" => dto.Background,
        "panel" => dto.Panel,
        "surface" => dto.Surface,
        "surface2" => dto.Surface2,
        "border" => dto.Border,
        "muted" => dto.Muted,
        "text" => dto.Text,
        _ => throw new InvalidOperationException($"unknown theme slot: {key}"),
    };

    private static IReadOnlyList<string> LintUnknownKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var unknown = new List<string>();
        foreach (var prop in root.EnumerateObject())
        {
            if (!KnownKeys.Contains(prop.Name.ToLowerInvariant()))
            {
                unknown.Add($"unknown property '{prop.Name}' ignored");
            }
        }

        return unknown;
    }

    /// <summary>
    /// WCAG lint (§Accessibility): body text below 4.5:1 and muted text below
    /// 3:1 against the background are flagged as warnings, never fatal — a
    /// theme author may deliberately trade contrast for style.
    /// </summary>
    private static IReadOnlyList<string> LintContrast(HarborTheme theme)
    {
        var warnings = new List<string>();
        double text = Accessibility.ContrastRatio(theme.Text, theme.Background);
        if (text < 4.5)
        {
            warnings.Add($"contrast: text/background {text:F2}:1 is below WCAG AA 4.5:1");
        }

        double muted = Accessibility.ContrastRatio(theme.Muted, theme.Background);
        if (muted < 3.0)
        {
            warnings.Add($"contrast: muted/background {muted:F2}:1 is below 3:1");
        }

        return warnings;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}
