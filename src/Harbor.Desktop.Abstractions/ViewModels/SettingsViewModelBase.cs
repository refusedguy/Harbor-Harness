using Harbor.Desktop.Abstractions.Models;
namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     Base for the settings view-model. Holds the user-editable settings
///     fields (theme, default provider, font size) and exposes Save / Reset
///     commands. The platform VM wires the actual config-persistence calls.
/// </summary>
public abstract partial class SettingsViewModelBase : ViewModelBase
{

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
    /// <summary>Construct a <see cref="SettingsViewModelBase" />.</summary>
    protected SettingsViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Mark the VM dirty when a property changes.</summary>
    /// <param name="e">Event args.</param>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Skip the IsDirty flag itself to avoid infinite recursion.
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
