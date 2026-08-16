using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     serves as API for the provider + model picker view-model
///     (upon_layers_featureFlags): holds the search text, the loading flag,
///     the current-model label, and the <see cref="ModelSelected" /> event a
///     host uses to close the picker popup. The grouped provider/model
///     collection is populated by the platform VM (its item types vary by
///     framework).
/// </summary>
/// <remarks>
///     <para>
///         <b>Lazy model fetch.</b> The registry's <c>GetAllModelsAsync</c>
///         fans out to every registered provider; the platform VM bounds the
///         whole call to <see cref="ModelFetchTimeout" /> so a missing local
///         provider (typical Ollama-not-running case) doesn't hang the picker.
///     </para>
///     <para>
///         <b>Search.</b> The filter is case-insensitive and matches across
///         provider id / display name AND model id / display name. Providers
///         with zero matching models are hidden while a search is active.
///     </para>
/// </remarks>
public abstract partial class ProviderModelPickerViewModelBase : StoreSubscriberViewModel
{
    /// <summary>
    ///     Hard cap on how long the aggregated model-list fetch may take. Five
    ///     seconds is long enough for a healthy provider to respond, short
    ///     enough that the user doesn't think the picker hung when a local
    ///     provider (Ollama, vLLM) isn't running.
    /// </summary>
    public static readonly TimeSpan ModelFetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Label for the currently-selected model (shown in the picker's header).</summary>
    [ObservableProperty]
    private string _currentModelLabel = string.Empty;

    /// <summary>Last load/fetch error (empty when healthy).</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>True while the provider/model list is loading.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Search filter applied to the grouped provider list.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Construct the picker base.</summary>
    /// <param name="dispatcher">UI-thread marshaller and store binder.</param>
    /// <param name="logger">Logger.</param>
    protected ProviderModelPickerViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => state.Model, v => CurrentModelLabel = v);
    }

    /// <summary>
    ///     Raised (without payload) after a model is selected so hosts can
    ///     close the picker popup. The shell's flyout subscribes to this.
    /// </summary>
    public event Action? ModelSelected;

    /// <summary>Raise <see cref="ModelSelected" /> (called by the platform VM after persisting the choice).</summary>
    protected void RaiseModelSelected() => ModelSelected?.Invoke();

    /// <summary>
    ///     Called when the global <see cref="UiState" /> changes. Applies all
    ///     declared selectors to project state slices into view-model properties.
    /// </summary>
    /// <param name="state">The current UI state snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }
}
