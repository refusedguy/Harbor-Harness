using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Ui.Framework.Services;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Theme selection + preview view-model. Owns the active theme string
///     (dark / light / system) and the live preview command. Extracted
///     from <c>SettingsViewModel</c> so the theme-switch code path is
///     unit-testable in isolation (construct a
///     <see cref="ThemeSettingsViewModel" /> with a fake
///     <see cref="ThemeService" /> and assert that <c>ApplyTheme</c>
///     forwards the right theme string).
/// </summary>
/// <remarks>
///     Registered as a transient in <c>AppHost</c> so the Settings dialog
///     gets a fresh instance each time it opens. SettingsViewModel
///     composes this VM and exposes it as <c>ThemeSettings</c> for XAML
///     data binding (<c>{Binding ThemeSettings.Theme}</c>).
/// </remarks>
public sealed partial class ThemeSettingsViewModel : ObservableObject
{
    private readonly IThemeService _themeService;

    /// <summary>
    ///     True when the resolved (applied) theme is dark. Updated by
    ///     <see cref="ApplyTheme" />. Bound to the Settings UI to drive
    ///     theme-aware preview elements.
    /// </summary>
    [ObservableProperty]
    private bool _isDarkTheme = true;

    /// <summary>
    ///     The selected theme string — <c>"dark"</c>, <c>"light"</c>, or
    ///     <c>"system"</c>. Persisted to <c>CommonConfig.Theme</c> +
    ///     <c>AvaloniaConfig.Theme</c> by the parent <c>SettingsViewModel</c>.
    /// </summary>
    [ObservableProperty]
    private string _theme = "system";

    /// <summary>Construct a <see cref="ThemeSettingsViewModel" />.</summary>
    /// <param name="themeService">The theme service that applies the theme to the running app.</param>
    public ThemeSettingsViewModel(IThemeService themeService)
    {
        _themeService = themeService;
    }

    /// <summary>
    ///     Apply the current theme immediately (without saving). Used by
    ///     the "Preview" button next to the theme picker so the user can
    ///     preview a theme before committing.
    /// </summary>
    [RelayCommand]
    private void ApplyTheme()
    {
        _themeService.Apply(Theme);
        IsDarkTheme = _themeService.IsDark;
    }

    /// <summary>
    ///     Apply a specific theme programmatically (used by the parent
    ///     <c>SettingsViewModel.SaveAsync</c> when persisting a saved
    ///     theme).
    /// </summary>
    /// <param name="theme">The theme to apply (<c>"dark"</c> / <c>"light"</c> / <c>"system"</c>).</param>
    public void Apply(string theme)
    {
        _themeService.Apply(theme);
        IsDarkTheme = _themeService.IsDark;
    }
}
