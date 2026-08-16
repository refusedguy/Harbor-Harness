using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Providers;
using Harbor.Desktop.Abstractions.Messages;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Core.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Messaging;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     View-model behind the <c>ProviderModelPicker</c> control. Lists every
///     registered provider (from <see cref="IProviderRegistry" />) with its
///     authentication status (✓ key resolved, ✗ no key) and lazily fetches the
///     models each provider exposes — grouped by provider in expandable rows.
///     A search box filters by provider display name OR model id / display
///     name. Selecting a model persists it as the session's
///     <c>DefaultProvider</c>/<c>DefaultModel</c> and rebinds the active
///     session to it via <see cref="SessionManager" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Async state management</b> uses <see cref="AsyncFeed{T}" /> instead
///         of manual <c>CTS + IsLoading + ErrorMessage</c>. The feed owns the
///         cancellation token, timeout, and state transitions.
///     </para>
/// </remarks>
public partial class ProviderModelPickerViewModel : ObservableObject
{
    public static readonly TimeSpan ModelFetchTimeout = TimeSpan.FromSeconds(5);

    private readonly AsyncFeed<IReadOnlyList<ProviderGroupViewModel>> _modelsFeed;
    private readonly ICommonConfigStore _configStore;
    private readonly ILogger<ProviderModelPickerViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly IProviderRegistry _providers;
    private readonly ISessionManager _sessions;
    private readonly IToastService _toasts;
    [ObservableProperty]
    private string _currentModelLabel = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Construct the picker view-model.</summary>
    public ProviderModelPickerViewModel(
        IProviderRegistry providers,
        ICommonConfigStore configStore,
        ISessionManager sessions,
        IToastService toasts,
        ILogger<ProviderModelPickerViewModel> logger,
        IMessenger messenger)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _messenger = messenger;

        _modelsFeed = new AsyncFeed<IReadOnlyList<ProviderGroupViewModel>>(
            LoadAllAsync, ModelFetchTimeout, _logger);

        _modelsFeed.Changed += OnModelsChanged;
    }

    /// <summary>All providers + their models, before search filtering.</summary>
    public ObservableCollection<ProviderGroupViewModel> AllProviders { get; } = new();

    /// <summary>Providers + models filtered by the current <see cref="SearchText" />.</summary>
    public ObservableCollection<ProviderGroupViewModel> FilteredProviders { get; } = new();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await _modelsFeed.RefreshAsync().ConfigureAwait(true);
    }

    private void OnModelsChanged(AsyncData<IReadOnlyList<ProviderGroupViewModel>> data)
    {
        switch (data.Status)
        {
            case AsyncStatus.Loading:
            case AsyncStatus.Refreshing:
                AllProviders.Clear();
                FilteredProviders.Clear();
                break;
            case AsyncStatus.Success:
                if (data.HasValue)
                {
                    AllProviders.Clear();
                    foreach (var group in data.Value!)
                        AllProviders.Add(group);
                    ApplyFilter();
                }
                break;
            case AsyncStatus.Error:
                _logger.LogWarning("Picker load error: {Error}", data.Error);
                break;
        }
    }

    private async Task<Result<IReadOnlyList<ProviderGroupViewModel>>> LoadAllAsync(CancellationToken ct)
    {
        var groups = new List<ProviderGroupViewModel>();
        var ids = _providers.GetRegisteredProviderIds();

        foreach (var pid in ids)
        {
            var group = await BuildProviderGroupAsync(pid.Value, ct).ConfigureAwait(true);
            groups.Add(group);
        }

        var modelsResult = await _providers.GetAllModelsAsync(ct).ConfigureAwait(true);
        if (modelsResult.IsFailure)
            return Result.Failure<IReadOnlyList<ProviderGroupViewModel>>(modelsResult.Error);

        foreach (var group in groups)
        {
            foreach (var m in modelsResult.Value.Where(m => m.ProviderId == group.Id))
            {
                group.Models.Add(new PickerModelViewModel(
                    group.Id, m.Id, m.DisplayName,
                    FormatPricing(m.Pricing.InputPerMillion, m.Pricing.OutputPerMillion),
                    FormatFeatures(m.SupportsToolUse, m.SupportsVision, m.SupportsReasoning)));
            }
            group.HasModels = group.Models.Count > 0;
        }

        await RefreshCurrentModelLabelAsync().ConfigureAwait(true);

        return Result.Success<IReadOnlyList<ProviderGroupViewModel>>(groups);
    }

    [RelayCommand]
    private async Task SelectModelAsync(PickerModelViewModel? model)
    {
        if (model is null) return;

        var group = AllProviders.FirstOrDefault(g => g.Id == model.ProviderId);
        if (group is not null && group.RequiresApiKey && !group.IsAuthenticated)
        {
            _toasts.Show($"Provider '{model.ProviderId}' has no API key configured. Set one in Settings first.", ToastKind.Error);
            return;
        }

        try
        {
            var updateResult = await _configStore.UpdateAsync(cfg => cfg with
            {
                DefaultProvider = model.ProviderId,
                DefaultModel = model.Id
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
            _messenger.Send(new ModelPickedMessage());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Picker SelectModel exception");
            _toasts.Show($"Could not switch model: {ex.Message}", ToastKind.Error);
        }
    }

    private async Task RefreshCurrentModelLabelAsync()
    {
        if (_sessions.Active is { } active)
        {
            CurrentModelLabel = $"{active.ProviderId}/{active.Model}";
            return;
        }

        var result = await _configStore.LoadAsync().ConfigureAwait(true);
        if (result.IsSuccess)
        {
            CurrentModelLabel = $"{result.Value.DefaultProvider}/{result.Value.DefaultModel}";
        }
    }

    private async Task<ProviderGroupViewModel> BuildProviderGroupAsync(string providerId, CancellationToken ct)
    {
        var preset = ProviderPresets.Find(providerId);
        string displayName = preset?.DisplayName ?? providerId;

        bool authenticated = false;
        try
        {
            var cfgResult = await _configStore.LoadAsync(ct).ConfigureAwait(true);
            authenticated = cfgResult.IsSuccess && !string.IsNullOrWhiteSpace(cfgResult.Value.ApiKeys.GetValueOrDefault(providerId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Config load threw for {Provider}", providerId);
        }

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
            SetupHint = preset?.SetupHint
        };
    }

    private void ApplyFilter()
    {
        FilteredProviders.Clear();
        string needle = (SearchText ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            foreach (var p in AllProviders) FilteredProviders.Add(p);
            return;
        }

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
                    IsExpanded = true
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
            return "pricing unknown";
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
