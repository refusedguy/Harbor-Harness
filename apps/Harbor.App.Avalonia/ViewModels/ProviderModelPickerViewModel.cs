using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Providers;
using Harbor.App.Avalonia.Services;
using Harbor.Core.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     View-model behind the <c>ProviderModelPicker</c> control. Lists every
///     registered provider (from <see cref="IProviderRegistry"/>) with its
///     authentication status (✓ key resolved, ✗ no key) and lazily fetches the
///     models each provider exposes — grouped by provider in expandable rows.
///     A search box filters by provider display name OR model id / display
///     name. Selecting a model persists it as the session's
///     <c>DefaultProvider</c>/<c>DefaultModel</c> and rebinds the active
///     session to it via <see cref="SessionManager"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Auth resolution</b> uses <see cref="IAuthResolver"/> (the same
///         path the agent uses) so the picker's status matches what the agent
///         actually sees at request time. <see cref="ProviderPresets"/> is also
///         consulted so the control can show a friendly display name even when
///         no preset env var is set.
///     </para>
///     <para>
///         <b>Lazy model fetch.</b> The registry's <c>GetAllModelsAsync</c>
///         fans out to every registered provider; we bound the whole call to a
///         per-load cancellation token of 5 seconds so a missing local
///         provider (typical Ollama-not-running case) doesn't hang the picker.
///     </para>
///     <para>
///         <b>Search.</b> The filter is case-insensitive and matches across
///         provider id / display name AND model id / display name. Providers
///         with zero matching models are hidden while a search is active.
///     </para>
/// </remarks>
public sealed partial class ProviderModelPickerViewModel : ObservableObject
{
    /// <summary>
    ///     Hard cap on how long the aggregated model-list fetch may take. Five
    ///     seconds is long enough for a healthy provider to respond, short
    ///     enough that the user doesn't think the picker hung when a local
    ///     provider (Ollama, vLLM) isn't running.
    /// </summary>
    public static readonly TimeSpan ModelFetchTimeout = TimeSpan.FromSeconds(5);

    private readonly IProviderRegistry _providers;
    private readonly IAuthResolver _authResolver;
    private readonly ICommonConfigStore _configStore;
    private readonly SessionManager _sessions;
    private readonly ToastService _toasts;
    private readonly ILogger<ProviderModelPickerViewModel> _logger;
    private CancellationTokenSource? _loadCts;

    /// <summary>Construct the picker view-model.</summary>
    public ProviderModelPickerViewModel(
        IProviderRegistry providers,
        IAuthResolver authResolver,
        ICommonConfigStore configStore,
        SessionManager sessions,
        ToastService toasts,
        ILogger<ProviderModelPickerViewModel> logger)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _authResolver = authResolver ?? throw new ArgumentNullException(nameof(authResolver));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     All providers + their models, before search filtering. The
    ///     <see cref="FilteredProviders"/> view is what the UI binds to.
    /// </summary>
    public ObservableCollection<ProviderGroupViewModel> AllProviders { get; } = new();

    /// <summary>Providers + models filtered by the current <see cref="SearchText"/>.</summary>
    public ObservableCollection<ProviderGroupViewModel> FilteredProviders { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _currentModelLabel = string.Empty;

    /// <summary>
    ///     Raised (without payload) after a model is selected so hosts can
    ///     close the picker popup. The MainWindow flyout subscribes to this.
    /// </summary>
    public event Action? ModelSelected;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    ///     Load providers from the registry, resolve auth status for each, then
    ///     fan out a single <c>GetAllModelsAsync</c> with a 5s timeout. Safe
    ///     to call repeatedly — cancels any in-flight load first.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource(ModelFetchTimeout);
        _loadCts = cts;

        IsLoading = true;
        ErrorMessage = string.Empty;
        AllProviders.Clear();
        FilteredProviders.Clear();

        try
        {
            // Step 1: list registered providers with auth status (synchronous
            // metadata — no network IO).
            var ids = _providers.GetRegisteredProviderIds();
            foreach (var pid in ids)
            {
                var group = await BuildProviderGroupAsync(pid.Value, cts.Token).ConfigureAwait(true);
                AllProviders.Add(group);
            }

            // Step 2: fetch models for every provider in one aggregated call.
            // The registry implementation forwards to each provider's
            // /models endpoint; the 5s token bounds the whole fan-out.
            var modelsResult = await _providers.GetAllModelsAsync(cts.Token).ConfigureAwait(true);
            if (modelsResult.IsSuccess)
            {
                foreach (var group in AllProviders)
                {
                    foreach (var m in modelsResult.Value.Where(m => m.ProviderId == group.Id))
                    {
                        group.Models.Add(new PickerModelViewModel(
                            group.Id,
                            m.Id,
                            m.DisplayName,
                            FormatPricing(m.Pricing.InputPerMillion, m.Pricing.OutputPerMillion),
                            FormatFeatures(m.SupportsToolUse, m.SupportsVision, m.SupportsReasoning)));
                    }
                    group.HasModels = group.Models.Count > 0;
                }
            }
            else
            {
                // Don't fail the whole picker — the provider list + auth status
                // are still useful. Surface the error and let the user fix the
                // missing provider (start Ollama, set an API key, …).
                _logger.LogWarning("Picker GetAllModelsAsync failed: {Error}", modelsResult.Error);
                ErrorMessage = modelsResult.Error;
            }

            // Step 3: refresh the current-model label so the user can see which
            // model is active right now.
            await RefreshCurrentModelLabelAsync().ConfigureAwait(true);

            ApplyFilter();
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Picker model fetch timed out after {Timeout}s", ModelFetchTimeout.TotalSeconds);
            ErrorMessage = $"Model fetch timed out after {ModelFetchTimeout.TotalSeconds:F0}s — some providers may not be running.";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Picker LoadAsync exception");
            ErrorMessage = ex.Message;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
            cts.Dispose();
        }
    }

    /// <summary>
    ///     Select a model and apply it to the active session. Persists the
    ///     choice to <see cref="CommonConfig.DefaultProvider"/> /
    ///     <see cref="CommonConfig.DefaultModel"/> (so it survives a restart)
    ///     and immediately rebinds the agent via
    ///     <see cref="SessionManager.RebindFromCommonConfigAsync"/>.
    /// </summary>
    /// <param name="model">The model row the user clicked.</param>
    [RelayCommand]
    private async Task SelectModelAsync(PickerModelViewModel? model)
    {
        if (model is null) return;

        try
        {
            var updateResult = await _configStore.UpdateAsync(cfg => cfg with
            {
                DefaultProvider = model.ProviderId,
                DefaultModel = model.Id,
            }).ConfigureAwait(true);

            if (updateResult.IsFailure)
            {
                _toasts.Show($"Could not save model selection: {updateResult.Error}", ToastKind.Error);
                _logger.LogError("Picker SelectModel: config update failed: {Error}", updateResult.Error);
                return;
            }

            await _sessions.RebindFromCommonConfigAsync().ConfigureAwait(true);
            CurrentModelLabel = $"{model.ProviderId}/{model.Id}";
            _toasts.Show($"Switched to {model.ProviderId}/{model.Id}.", ToastKind.Success);
            ModelSelected?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Picker SelectModel exception");
            _toasts.Show($"Could not switch model: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Refresh <see cref="CurrentModelLabel"/> from the active session.</summary>
    private async Task RefreshCurrentModelLabelAsync()
    {
        if (_sessions.Active is { } active)
        {
            CurrentModelLabel = $"{active.ProviderId}/{active.Model}";
            return;
        }
        // Fall back to CommonConfig defaults when no session is active yet.
        var result = await _configStore.LoadAsync().ConfigureAwait(true);
        if (result.IsSuccess)
        {
            CurrentModelLabel = $"{result.Value.DefaultProvider}/{result.Value.DefaultModel}";
        }
    }

    /// <summary>Build a <see cref="ProviderGroupViewModel"/> with auth status resolved.</summary>
    private async Task<ProviderGroupViewModel> BuildProviderGroupAsync(string providerId, CancellationToken ct)
    {
        var preset = ProviderPresets.Find(providerId);
        string displayName = preset?.DisplayName ?? providerId;

        // Resolve auth: IAuthResolver checks CommonConfig.ApiKeys first, then
        // preset env vars, then conventional {PROVIDERID}_API_KEY.
        bool authenticated = false;
        try
        {
            var resolveResult = await _authResolver.ResolveApiKeyAsync(providerId, ct).ConfigureAwait(true);
            authenticated = resolveResult.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auth resolution threw for {Provider}", providerId);
        }

        // Local providers (Ollama, vLLM) don't need a key — show a "no key
        // required" status instead of the scary ✗ "no key" one.
        bool requiresKey = preset?.RequiresApiKey ?? true;
        string icon;
        string text;
        string brushKey;
        if (!requiresKey)
        {
            icon = "✓";
            text = "No key required";
            brushKey = "MochaGreen";
        }
        else if (authenticated)
        {
            icon = "✓";
            text = "Authenticated";
            brushKey = "MochaGreen";
        }
        else
        {
            icon = "✗";
            text = "No API key — set one in Settings";
            brushKey = "MochaRed";
        }

        return new ProviderGroupViewModel(providerId, displayName, icon, text, brushKey, authenticated, requiresKey)
        {
            SetupHint = preset?.SetupHint,
        };
    }

    /// <summary>Apply <see cref="SearchText"/> to <see cref="AllProviders"/> → <see cref="FilteredProviders"/>.</summary>
    private void ApplyFilter()
    {
        FilteredProviders.Clear();
        var needle = (SearchText ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            foreach (var p in AllProviders) FilteredProviders.Add(p);
            return;
        }

        // Case-insensitive contains on provider id/display name OR any model
        // id/display name. Providers with zero matching models are hidden.
        foreach (var p in AllProviders)
        {
            bool providerMatches =
                p.Id.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                p.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase);

            if (providerMatches)
            {
                FilteredProviders.Add(p);
                continue;
            }

            // Provider name didn't match — check models. Add the provider only
            // if at least one model matches, and surface only the matches.
            var matchingModels = p.Models
                .Where(m => m.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                         || m.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchingModels.Count > 0)
            {
                var filtered = new ProviderGroupViewModel(
                    p.Id, p.DisplayName, p.AuthStatusIcon, p.AuthStatusText,
                    p.AuthStatusBrushKey, p.IsAuthenticated, p.RequiresApiKey)
                {
                    SetupHint = p.SetupHint,
                    IsExpanded = true,
                };
                foreach (var m in matchingModels) filtered.Models.Add(m);
                filtered.HasModels = true;
                FilteredProviders.Add(filtered);
            }
        }
    }

    private static string FormatPricing(decimal inputPerMillion, decimal outputPerMillion)
    {
        if (inputPerMillion == 0m && outputPerMillion == 0m)
        {
            return "pricing unknown";
        }
        return $"${inputPerMillion:F2} in / ${outputPerMillion:F2} out per 1M";
    }

    private static string FormatFeatures(bool tools, bool vision, bool reasoning)
    {
        var parts = new List<string>(3);
        if (tools) parts.Add("tools");
        if (vision) parts.Add("vision");
        if (reasoning) parts.Add("reasoning");
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }
}

/// <summary>
///     One provider row in the picker. Holds the auth status badge and the
///     lazily-populated model list. <see cref="IsExpanded"/> is bound to the
///     Expander so the user can collapse providers they're not interested in.
/// </summary>
public sealed partial class ProviderGroupViewModel : ObservableObject
{
    /// <summary>Construct a provider group.</summary>
    public ProviderGroupViewModel(
        string id,
        string displayName,
        string authStatusIcon,
        string authStatusText,
        string authStatusBrushKey,
        bool isAuthenticated,
        bool requiresApiKey)
    {
        Id = id;
        DisplayName = displayName;
        AuthStatusIcon = authStatusIcon;
        AuthStatusText = authStatusText;
        AuthStatusBrushKey = authStatusBrushKey;
        IsAuthenticated = isAuthenticated;
        RequiresApiKey = requiresApiKey;
    }

    /// <summary>Provider id (e.g. <c>ollama</c>, <c>kilocode</c>).</summary>
    public string Id { get; }

    /// <summary>Human-readable name from <see cref="ProviderPresets"/> (or id fallback).</summary>
    public string DisplayName { get; }

    /// <summary>Auth status icon — <c>✓</c> or <c>✗</c>.</summary>
    public string AuthStatusIcon { get; }

    /// <summary>Auth status text — e.g. "Authenticated", "No API key — set one in Settings".</summary>
    public string AuthStatusText { get; }

    /// <summary>
    ///     App-resource brush key for the auth icon — <c>MochaGreen</c> when
    ///     authenticated / no-key-required, <c>MochaRed</c> when a key is
    ///     missing. Resolved at bind time via <c>BrushKeyConverter</c>.
    /// </summary>
    public string AuthStatusBrushKey { get; }

    /// <summary>Whether an API key was resolved for this provider.</summary>
    public bool IsAuthenticated { get; }

    /// <summary>Whether this provider requires an API key (false for Ollama, vLLM).</summary>
    public bool RequiresApiKey { get; }

    /// <summary>Optional setup hint URL from the provider preset.</summary>
    public string? SetupHint { get; init; }

    /// <summary>Models exposed by this provider.</summary>
    public ObservableCollection<PickerModelViewModel> Models { get; } = new();

    /// <summary>Whether the Expander is open.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Whether <see cref="Models"/> has been populated.</summary>
    [ObservableProperty]
    private bool _hasModels;
}

/// <summary>
///     One model row in the picker. Clicking the row raises
///     <see cref="ProviderModelPickerViewModel.SelectModelCommand"/>.
/// </summary>
public sealed record PickerModelViewModel(
    string ProviderId,
    string Id,
    string DisplayName,
    string PricingText,
    string FeaturesText);
