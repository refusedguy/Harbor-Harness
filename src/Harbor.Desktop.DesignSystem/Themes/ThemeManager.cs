using System.Text.Json;
using Harbor.Desktop.Abstractions.Models;
using Harbor.Desktop.Abstractions.Services;

namespace Harbor.Desktop.DesignSystem.Themes;

/// <summary>
///     Theme manager — holds the current <see cref="ThemeKind"/>, persists it
///     to <c>~/.harbor/theme.json</c>, and exposes the active token dictionary
///     (<see cref="DarkTheme"/> or <see cref="LightTheme"/>). Each platform
///     app's <c>IThemeService</c> implementation wraps this class to add the
///     actual color-application step (e.g. swapping Avalonia resource
///     dictionaries).
/// </summary>
public sealed class ThemeManager
{
    private readonly ILogger<ThemeManager> _logger;
    private readonly string _filePath;
    private ThemeKind _current;

    /// <summary>Construct a <see cref="ThemeManager"/> and load persisted state.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="filePath">
    ///     Optional override for the persistence path. Defaults to
    ///     <c>~/.harbor/theme.json</c>. Test code can pass a temp path.
    /// </param>
    public ThemeManager(ILogger<ThemeManager> logger, string? filePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = filePath ?? DefaultPath();
        _current = Load();
    }

    /// <summary>Default persistence path: <c>~/.harbor/theme.json</c>.</summary>
    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".harbor", "theme.json");
    }

    /// <summary>The currently active theme kind. Always Dark or Light (never System).</summary>
    public ThemeKind Current => _current;

    /// <summary>Raised when <see cref="Current"/> changes. Payload is the new kind.</summary>
    public event EventHandler<ThemeKind>? ThemeChanged;

    /// <summary>The active token map (Mocha or Latte).</summary>
    public IReadOnlyDictionary<string, string> CurrentTokens =>
        _current == ThemeKind.Light ? LightTheme.Tokens : DarkTheme.Tokens;

    /// <summary>Apply the dark (Mocha) theme and persist.</summary>
    public void ApplyDark()
    {
        if (_current == ThemeKind.Dark) return;
        _current = ThemeKind.Dark;
        Persist();
        RaiseChanged();
    }

    /// <summary>Apply the light (Latte) theme and persist.</summary>
    public void ApplyLight()
    {
        if (_current == ThemeKind.Light) return;
        _current = ThemeKind.Light;
        Persist();
        RaiseChanged();
    }

    /// <summary>Apply the OS-preferred theme. Resolves System to Dark or Light.</summary>
    /// <remarks>
    ///     The platform app should call this with the actual OS preference;
    ///     this method just resolves "System" → the given kind and applies it.
    /// </remarks>
    public void ApplySystem(bool osPrefersDark)
    {
        if (osPrefersDark) ApplyDark(); else ApplyLight();
    }

    /// <summary>Toggle between Dark and Light.</summary>
    public void Toggle()
    {
        if (_current == ThemeKind.Dark) ApplyLight(); else ApplyDark();
    }

    private ThemeKind Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return ThemeKind.Dark;
            var json = File.ReadAllText(_filePath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("theme", out var el)
                && el.ValueKind == JsonValueKind.String
                && Enum.TryParse<ThemeKind>(el.GetString(), ignoreCase: true, out var kind)
                && kind != ThemeKind.System)
                return kind;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load theme from {Path}; defaulting to Dark", _filePath);
        }
        return ThemeKind.Dark;
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var payload = new { theme = _current.ToString().ToLowerInvariant() };
            var json = JsonSerializer.Serialize(payload);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist theme to {Path}", _filePath);
        }
    }

    private void RaiseChanged()
    {
        ThemeChanged?.Invoke(this, _current);
    }
}
