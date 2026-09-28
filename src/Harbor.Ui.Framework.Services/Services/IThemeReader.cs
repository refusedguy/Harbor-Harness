namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Read-only view of the active theme. Split out of
///     <see cref="IThemeService" /> (ISP, #469) so consumers that only project
///     the current variant — e.g. <c>SettingsViewModelBase</c> — do not take a
///     dependency on the apply / watch roles.
/// </summary>
public interface IThemeReader
{
    /// <summary>Current theme: "dark", "light", or "system".</summary>
    public string Current { get; }

    /// <summary>Whether dark theme is currently active.</summary>
    public bool IsDark { get; }
}
