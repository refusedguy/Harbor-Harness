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
}
