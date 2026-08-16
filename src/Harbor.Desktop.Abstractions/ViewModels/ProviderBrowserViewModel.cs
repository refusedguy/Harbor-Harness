using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Providers;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Browse providers + models with metadata. Read-only view; configuring a new
///     provider is done in <see cref="SettingsViewModel" />. Uses
///     <see cref="AsyncFeed{T}" /> to own cancellation + timeout + state.
/// </summary>
public sealed partial class ProviderBrowserViewModel : ObservableObject
{
    public static readonly TimeSpan ModelFetchTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<ProviderBrowserViewModel> _logger;
    private readonly IProviderRegistry _providers;
    private readonly AsyncFeed<IReadOnlyList<ModelRowViewModel>> _modelsFeed;
    private string _currentProviderId = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ProviderRowViewModel? _selectedProvider;

    public ProviderBrowserViewModel(IProviderRegistry providers, ILogger<ProviderBrowserViewModel> logger)
    {
        _providers = providers;
        _logger = logger;
        _modelsFeed = new AsyncFeed<IReadOnlyList<ModelRowViewModel>>(LoadModelsAsync, ModelFetchTimeout, _logger);
        _modelsFeed.Changed += OnModelsChanged;
    }

    public ObservableCollection<ProviderRowViewModel> Providers { get; } = new();
    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    [RelayCommand]
    private async Task LoadProvidersAsync()
    {
        _currentProviderId = string.Empty;
        Providers.Clear();
        Models.Clear();
        ErrorMessage = string.Empty;
        IsLoading = true;

        try
        {
            foreach (string id in _providers.GetRegisteredProviderIds().Select(p => p.Value))
            {
                Providers.Add(new ProviderRowViewModel(id, id, "ready"));
            }
            SelectedProvider = Providers.FirstOrDefault();
            if (SelectedProvider is not null)
            {
                _currentProviderId = SelectedProvider.Id;
                await _modelsFeed.RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadProvidersAsync exception");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadModelsAsync(ProviderRowViewModel? row)
    {
        if (row is null) return;

        SelectedProvider = row;
        _currentProviderId = row.Id;
        Models.Clear();
        ErrorMessage = string.Empty;
        IsLoading = true;

        try
        {
            await _modelsFeed.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadModelsAsync exception");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnModelsChanged(AsyncData<IReadOnlyList<ModelRowViewModel>> data)
    {
        switch (data.Status)
        {
            case AsyncStatus.Loading:
            case AsyncStatus.Refreshing:
                Models.Clear();
                break;
            case AsyncStatus.Success:
                if (data.HasValue && data.Value is not null)
                {
                    Models.Clear();
                    foreach (var m in data.Value)
                        Models.Add(m);
                    if (Models.Count == 0)
                    {
                        ErrorMessage = $"No models returned by provider '{_currentProviderId}'. " +
                                       "If this is a local provider (e.g. Ollama), make sure it's running.";
                    }
                }
                break;
            case AsyncStatus.Error:
                _logger.LogWarning("Model fetch error: {Error}", data.Error);
                ErrorMessage = data.Error ?? "Unknown error";
                break;
        }
    }

    private async Task<Result<IReadOnlyList<ModelRowViewModel>>> LoadModelsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentProviderId))
            return Result.Failure<IReadOnlyList<ModelRowViewModel>>("No provider selected");

        var result = await _providers.GetAllModelsAsync(ct).ConfigureAwait(true);
        if (result.IsFailure)
            return Result.Failure<IReadOnlyList<ModelRowViewModel>>(result.Error);

        var models = result.Value
            .Where(m => m.ProviderId == _currentProviderId)
            .Select(m => new ModelRowViewModel(
                m.Id, m.DisplayName, m.ContextWindow, m.MaxOutputTokens,
                m.SupportsReasoning, m.SupportsVision, m.SupportsToolUse,
                m.Pricing.InputPerMillion, m.Pricing.OutputPerMillion))
            .ToImmutableArray();

        return Result.Success<IReadOnlyList<ModelRowViewModel>>(models);
    }
}

/// <summary>One row in the provider list.</summary>
public sealed record ProviderRowViewModel(string Id, string Name, string Status);

/// <summary>One row in the model list.</summary>
public sealed record ModelRowViewModel(
    string Id,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsReasoning,
    bool SupportsVision,
    bool SupportsToolUse,
    decimal InputPerMillion,
    decimal OutputPerMillion)
{
    public string Features =>
        string.Join(", ", new[]
        {
            SupportsToolUse ? "tools" : null,
            SupportsVision ? "vision" : null,
            SupportsReasoning ? "reasoning" : null
        }.Where(s => s is not null)!) ?? "—";

    public string PricingLabel => $"${InputPerMillion:F2} in / ${OutputPerMillion:F2} out per 1M";
}
