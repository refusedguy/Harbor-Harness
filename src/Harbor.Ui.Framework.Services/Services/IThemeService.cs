using CSharpFunctionalExtensions;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Abstraction for theme switching. Each desktop app implements this
///     to manipulate its own theme resource system.
/// </summary>
public interface IThemeService
{
    /// <summary>Current theme: "dark", "light", or "system".</summary>
    public string Current { get; }

    /// <summary>Whether dark theme is currently active.</summary>
    public bool IsDark { get; }

    /// <summary>Apply a theme by name.</summary>
    public void Apply(string theme);

    /// <summary>Apply dark theme.</summary>
    public void ApplyDark();

    /// <summary>Apply light theme.</summary>
    public void ApplyLight();

    /// <summary>Toggle between dark and light.</summary>
    public void Toggle();

    /// <summary>Apply an HDS palette by name (e.g. "CatppuccinMocha", "Vapor").</summary>
    public void ApplyHds(string theme);

    /// <summary>Set the base chrome variant (dark / light) without changing palette.</summary>
    public void SetThemeVariant(bool isDark);

    /// <summary>Load a theme JSON file from disk.</summary>
    public Result<string> LoadJson(string path);

    /// <summary>Parse and apply a theme from a JSON string.</summary>
    public Result ApplyJson(string json);

    /// <summary>Raised after a JSON theme is applied successfully.</summary>
    public event EventHandler<string>? ThemeJsonApplied;

    /// <summary>Watch a theme JSON file for live reload.</summary>
    public IDisposable Watch(string path);
}
