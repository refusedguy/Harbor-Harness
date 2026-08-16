using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     serves as API for the theme selection + preview view-model
///     (upon_layers_featureFlags): owns the active theme string
///     (dark / light / system) and the apply/preview commands. Extracted so the
///     theme-switch code path is unit-testable in isolation (construct with a
///     fake <see cref="IThemeService" /> and assert <see cref="Apply" />
///     forwards the right theme string).
/// </summary>
/// <remarks>
///     The live theme <em>thumbnails</em> (Surface/Accent/Text brushes) stay
///     in the platform layer — they need framework brush types
///     (Avalonia <c>IBrush</c>, WPF <c>Brush</c>) which must not leak into
///     this abstractions assembly.
/// </remarks>
public abstract partial class ThemeSettingsViewModelBase : StoreSubscriberViewModel
{
    private readonly IThemeService _themeService;

    /// <summary>
    ///     True when the resolved (applied) theme is dark. Updated by
    ///     <see cref="ApplyTheme" /> / <see cref="Apply" />.
    /// </summary>
    [ObservableProperty]
    private bool _isDarkTheme = true;

    /// <summary>
    ///     The selected theme string — <c>"dark"</c>, <c>"light"</c>, or
    ///     <c>"system"</c>. Persisted to <c>CommonConfig.Theme</c> by the
    ///     parent settings VM.
    /// </summary>
    [ObservableProperty]
    private string _theme = "system";

    /// <summary>Construct a <see cref="ThemeSettingsViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller / store binder.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="themeService">The theme service that applies the theme to the running app.</param>
    protected ThemeSettingsViewModelBase(
        IDispatcherAdapter dispatcher,
        ILogger logger,
        IThemeService themeService)
        : base(dispatcher, logger)
    {
        _themeService = themeService;

        Select(state => _themeService.IsDark, v => IsDarkTheme = v);
    }

    /// <summary>
    ///     Apply all declared selectors against the current store snapshot.
    /// </summary>
    /// <param name="state">The new <see cref="UiState" /> snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }

    /// <summary>
    ///     Apply the current theme immediately (without saving). Used by the
    ///     "Preview" button next to the theme picker so the user can preview a
    ///     theme before committing.
    /// </summary>
    [RelayCommand]
    private void ApplyTheme()
    {
        _themeService.Apply(Theme);
        IsDarkTheme = _themeService.IsDark;
    }

    /// <summary>
    ///     Apply a specific theme programmatically (used by the parent settings
    ///     VM's save path when persisting a saved theme).
    /// </summary>
    /// <param name="theme">The theme to apply (<c>"dark"</c> / <c>"light"</c> / <c>"system"</c>).</param>
    public void Apply(string theme)
    {
        _themeService.Apply(theme);
        IsDarkTheme = _themeService.IsDark;
    }
}
