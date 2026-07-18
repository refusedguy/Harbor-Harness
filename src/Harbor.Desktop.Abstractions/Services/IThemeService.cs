using Harbor.Desktop.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.Services;

/// <summary>
///     Switches the active theme and persists the user's preference. Each
///     platform app implements this by delegating to
///     <c>Harbor.Desktop.DesignSystem.ThemeManager</c> for state + persistence,
///     then applying the resolved colors to its own resource dictionary.
/// </summary>
public interface IThemeService
{
    /// <summary>The currently active theme kind (Dark or Light — never System).</summary>
    ThemeKind Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes. Payload is the new kind.</summary>
    event EventHandler<ThemeKind>? ThemeChanged;

    /// <summary>Apply the dark (Catppuccin-Mocha) theme.</summary>
    void ApplyDark();

    /// <summary>Apply the light (Catppuccin-Latte) theme.</summary>
    void ApplyLight();

    /// <summary>Apply the OS-preferred theme, resolved to Dark or Light.</summary>
    void ApplySystem();

    /// <summary>Toggle between Dark and Light.</summary>
    void Toggle();
}
