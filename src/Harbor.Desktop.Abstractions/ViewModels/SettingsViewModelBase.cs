using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Providers;
using Harbor.Desktop.Abstractions.Models;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the settings view-model. Holds the user-editable settings
///     fields (theme, default provider, font size) and exposes Save / Reset
///     commands. The platform VM wires the actual config-persistence calls.
/// </summary>
public abstract partial class SettingsViewModelBase : StoreSubscriberViewModel
{
    private readonly IThemeService _themeService;
    private readonly IProviderRegistry _providers;

    /// <summary>Code font size in px. Default 13.</summary>
    [ObservableProperty]
    private int _codeFontSize = 13;

    /// <summary>Default provider id (e.g. "openai", "anthropic").</summary>
    [ObservableProperty]
    private string _defaultProviderId = string.Empty;

    /// <summary>True if there are unsaved changes.</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>True while saving.</summary>
    [ObservableProperty]
    private bool _isSaving;

    /// <summary>Selected theme kind. Persisted via <see cref="IThemeService" />.</summary>
    [ObservableProperty]
    private ThemeKind _theme;

    /// <summary>UI font size in px. Default 13.</summary>
    [ObservableProperty]
    private int _uiFontSize = 13;

    /// <summary>Whether dark theme is currently active. Projected from <see cref="IThemeService" />.</summary>
    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>Registered provider IDs. Projected from <see cref="IProviderRegistry" />.</summary>
    [ObservableProperty]
    private ImmutableArray<string> _availableProviders = ImmutableArray<string>.Empty;

    /// <summary>Whether the active session is authenticated.</summary>
    [ObservableProperty]
    private bool _isAuthenticated;

    /// <summary>Construct a <see cref="SettingsViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller / store binder.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="themeService">Theme service for projecting <see cref="IsDarkTheme" />.</param>
    /// <param name="providers">Provider registry for projecting <see cref="AvailableProviders" />.</param>
    protected SettingsViewModelBase(
        IDispatcherAdapter dispatcher,
        ILogger logger,
        IThemeService themeService,
        IProviderRegistry providers)
        : base(dispatcher, logger)
    {
        _themeService = themeService;
        _providers = providers;

        Select(state => _themeService.IsDark, v => IsDarkTheme = v);
        Select(state => _providers.GetRegisteredProviderIds()
            .Select(p => p.Value)
            .ToImmutableArray(), v => AvailableProviders = v);
    }

    /// <summary>Apply all declared selectors against the current store snapshot.</summary>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }

    /// <summary>Mark the VM dirty when a property changes.</summary>
    /// <param name="e">Event args.</param>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsDirty) && e.PropertyName != nameof(IsSaving))
        {
            IsDirty = true;
        }
    }

    /// <summary>Persist settings. Implemented by the platform VM (writes to ~/.harbor/).</summary>
    protected abstract Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Reload settings from disk. Implemented by the platform VM.</summary>
    protected abstract Task ReloadAsync(CancellationToken cancellationToken);
}
