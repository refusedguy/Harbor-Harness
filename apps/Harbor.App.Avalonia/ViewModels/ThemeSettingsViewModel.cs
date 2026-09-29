using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Themes;
using Harbor.Ui.Framework.Services;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Theme selection + preview view-model. Owns the active theme string
///     (dark / light / system) and the live preview command. Extracted
///     from <c>SettingsViewModel</c> so the theme-switch code path is
///     unit-testable in isolation (construct a
///     <see cref="ThemeSettingsViewModel" /> with a fake
///     <see cref="IThemeApplier" /> and assert that <c>ApplyTheme</c>
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
///         <see cref="AvailableThemes"/> projects the HDS palettes that
///         <see cref="HdsThemeCatalog" /> reads from
///         <c>Themes/Hds/*.axaml</c>. This view-model holds no colour and no
///         light/dark list: a preview's brushes and its
///         <c>ThemeVariant</c> come from the palette dictionary, so a re-tuned
///         palette cannot leave a stale copy behind here (#673 — the previous
///         hand-copied <c>Color.Parse</c> table and "which palettes are dark"
///         array were exactly that second source of truth).
///     </para>
/// </remarks>
public sealed partial class ThemeSettingsViewModel : ObservableObject
{
    private readonly IThemeReader _themeReader;
    private readonly IThemeApplier _themeApplier;

    /// <summary>
    ///     The HDS palettes available for preview, one entry per palette
    ///     dictionary — brushes and variant read from the dictionary itself.
    /// </summary>
    public ObservableCollection<HdsThemePreview> AvailableThemes { get; }

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
    /// <param name="themeReader">Read-only view of the active theme.</param>
    /// <param name="themeApplier">The theme applier that switches the running app.</param>
    public ThemeSettingsViewModel(IThemeReader themeReader, IThemeApplier themeApplier)
    {
        _themeReader = themeReader;
        _themeApplier = themeApplier;

        // Snapshotted once per dialog opening: the catalog reads the palette
        // dictionaries, so the collection is a snapshot of "what the themes
        // declare right now", not a live view of them.
        AvailableThemes = new ObservableCollection<HdsThemePreview>(HdsThemeCatalog.ReadAll());
    }

    /// <summary>
    ///     Apply the current theme immediately (without saving). Used by
    ///     the "Preview" button next to the theme picker so the user can
    ///     preview a theme before committing.
    /// </summary>
    [RelayCommand]
    private void ApplyTheme()
    {
        _themeApplier.Apply(Theme);
        IsDarkTheme = _themeReader.IsDark;
    }

    /// <summary>
    ///     Apply a specific HDS palette immediately (without saving). Called
    ///     from the Settings UI when the user clicks a theme preview thumbnail.
    /// </summary>
    /// <param name="themeName">HDS theme name, e.g. "CatppuccinMocha".</param>
    [RelayCommand]
    private void ApplyHdsTheme(string themeName)
    {
        _themeApplier.ApplyHds(themeName);

        // The palette answers "am I dark?" — it declares the ThemeVariant it was
        // designed for. An unknown name leaves the variant alone rather than
        // guessing light, which is what the old hand-written array did for every
        // palette it had not been updated for (#673).
        HdsThemePreview? preview = HdsThemeCatalog.Find(themeName);
        if (preview is null)
        {
            return;
        }

        _themeApplier.SetThemeVariant(preview.IsDark);
        IsDarkTheme = preview.IsDark;
    }

    /// <summary>
    ///     Apply a specific theme programmatically (used by the parent
    ///     <c>SettingsViewModel.SaveAsync</c> when persisting a saved
    ///     theme).
    /// </summary>
    /// <param name="theme">The theme to apply (<c>"dark"</c> / <c>"light"</c> / <c>"system"</c>).</param>
    public void Apply(string theme)
    {
        _themeApplier.Apply(theme);
        IsDarkTheme = _themeReader.IsDark;
    }
}
