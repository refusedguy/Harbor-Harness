using System.Collections.ObjectModel;
using Avalonia.Media;
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
///     <see cref="IThemeService" /> and assert that <c>ApplyTheme</c>
///     forwards the right theme string).
/// </summary>
/// <remarks>
///     <para>
///         Registered as a transient in <c>AppHost</c> so the Settings dialog
///         gets a fresh instance each time it opens. SettingsViewModel
///         composes this VM and exposes it as <c>ThemeSettings</c> for XAML
///         data binding (<c>{Binding ThemeSettings.Theme}</c>).
///     </para>
///     <para>
///         <see cref="AvailableThemes"/> exposes the five built-in HDS palettes
///         with hard-coded preview colors so the Settings UI can render
///         live thumbnails without loading XAML dictionaries.
///     </para>
/// </remarks>
public sealed partial class ThemeSettingsViewModel : ObservableObject
{
    private readonly IThemeService _themeService;

    /// <summary>
    ///     Mini preview model for one HDS palette. Exposes static brushes
    ///     so the Settings thumbnail can bind Background / Foreground /
    ///     Accent directly.
    /// </summary>
    public sealed class ThemePreviewModel
    {
        public string Name { get; }
        public string DisplayName { get; }
        public IBrush SurfaceBrush { get; }
        public IBrush AccentBrush { get; }
        public IBrush TextBrush { get; }

        public ThemePreviewModel(string name, string displayName, IBrush surfaceBrush, IBrush accentBrush, IBrush textBrush)
        {
            Name = name;
            DisplayName = displayName;
            SurfaceBrush = surfaceBrush;
            AccentBrush = accentBrush;
            TextBrush = textBrush;
        }
    }

    /// <summary>Five built-in HDS palettes available for preview.</summary>
    public ObservableCollection<ThemePreviewModel> AvailableThemes { get; } = new();

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

        AvailableThemes.Add(new ThemePreviewModel("CatppuccinMocha", "Catppuccin Mocha",
            new SolidColorBrush(Color.Parse("#1E1E2E")),
            new SolidColorBrush(Color.Parse("#89B4FA")),
            new SolidColorBrush(Color.Parse("#CDD6F4"))));

        AvailableThemes.Add(new ThemePreviewModel("Lumen", "Lumen",
            new SolidColorBrush(Color.Parse("#FFFDF7")),
            new SolidColorBrush(Color.Parse("#D4A373")),
            new SolidColorBrush(Color.Parse("#2B2B2B"))));

        AvailableThemes.Add(new ThemePreviewModel("Mono", "Mono",
            new SolidColorBrush(Color.Parse("#0A0A0A")),
            new SolidColorBrush(Color.Parse("#888888")),
            new SolidColorBrush(Color.Parse("#E5E5E5"))));

        AvailableThemes.Add(new ThemePreviewModel("Paper", "Paper",
            new SolidColorBrush(Color.Parse("#FCFCFA")),
            new SolidColorBrush(Color.Parse("#4A6CF7")),
            new SolidColorBrush(Color.Parse("#2B2B2B"))));

        AvailableThemes.Add(new ThemePreviewModel("Vapor", "Vapor",
            new SolidColorBrush(Color.Parse("#0D002B")),
            new SolidColorBrush(Color.Parse("#8B5CF7")),
            new SolidColorBrush(Color.Parse("#E8E8FF"))));
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
    ///     Apply a specific HDS palette immediately (without saving). Called
    ///     from the Settings UI when the user clicks a theme preview thumbnail.
    /// </summary>
    /// <param name="themeName">HDS theme name, e.g. "CatppuccinMocha".</param>
    [RelayCommand]
    private void ApplyHdsTheme(string themeName)
    {
        _themeService.ApplyHds(themeName);
        bool isDark = themeName is "CatppuccinMocha" or "Mono" or "Vapor";
        _themeService.SetThemeVariant(isDark);
        IsDarkTheme = isDark;
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
