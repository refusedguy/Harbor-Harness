namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Abstraction for theme switching. Each desktop app implements this
///     to manipulate its own theme resource system.
/// </summary>
public interface IThemeService
{
    /// <summary>Current theme: "dark", "light", or "system".</summary>
    string Current { get; }
    
    /// <summary>Apply a theme by name.</summary>
    void Apply(string theme);
    
    /// <summary>Toggle between dark and light.</summary>
    void Toggle();
    
    /// <summary>Whether dark theme is currently active.</summary>
    bool IsDark { get; }
}
