namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Mutation side of the theme abstraction: switch the app between
///     palettes / variants. Split out of <see cref="IThemeService" /> (ISP, #469)
///     so callers that only switch themes — e.g. shell chrome's
///     <c>ToggleTheme</c> — do not drag in the JSON read-watch role.
/// </summary>
public interface IThemeApplier
{
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
}
