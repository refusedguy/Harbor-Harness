namespace Harbor.Desktop.Abstractions.Models;
/// <summary>
///     Selects which theme a desktop app should render. <see cref="System" />
///     means "follow the OS preference" — each platform app resolves it to
///     Dark or Light at startup.
/// </summary>
public enum ThemeKind
{
    /// <summary>Dark theme (Catppuccin-Mocha).</summary>
    Dark,

    /// <summary>Light theme (Catppuccin-Latte).</summary>
    Light,

    /// <summary>Follow the OS dark-mode preference; resolved at startup.</summary>
    System
}
