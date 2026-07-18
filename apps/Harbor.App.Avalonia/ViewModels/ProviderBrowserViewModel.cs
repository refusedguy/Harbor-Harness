using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Browse providers + models with metadata. Read-only view; configuring a new
///     provider is done in <see cref="SettingsViewModel"/>.
/// </summary>
public sealed partial class ProviderBrowserViewModel : ObservableObject
{
    private readonly IProviderRegistry _providers;
    private readonly ILogger<ProviderBrowserViewModel> _logger;

    /// <summary>Construct the provider browser view-model.</summary>
    public ProviderBrowserViewModel(IProviderRegistry providers, ILogger<ProviderBrowserViewModel> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <summary>Provider rows.</summary>
    public ObservableCollection<ProviderRowViewModel> Providers { get; } = new();

    /// <summary>Models for the selected provider.</summary>
    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    [ObservableProperty]
    private ProviderRowViewModel? _selectedProvider;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Load providers from the registry.</summary>
    [RelayCommand]
    private async Task LoadProvidersAsync()
    {
        IsLoading = true;
        Providers.Clear();
        foreach (var id in _providers.GetRegisteredProviderIds().Select(p => p.Value))
        {
            Providers.Add(new ProviderRowViewModel(id, id, "ready"));
        }
        SelectedProvider = Providers.FirstOrDefault();
        if (SelectedProvider is not null)
        {
            await LoadModelsAsync(SelectedProvider);
        }
        IsLoading = false;
    }

    /// <summary>Load models for the selected provider.</summary>
    [RelayCommand]
    private async Task LoadModelsAsync(ProviderRowViewModel? row)
    {
        if (row is null) return;
        IsLoading = true;
        Models.Clear();
        try
        {
            var result = await _providers.GetAllModelsAsync().ConfigureAwait(false);
            if (result.IsSuccess)
            {
                foreach (var m in result.Value.Where(m => m.ProviderId == row.Id))
                {
                    Models.Add(new ModelRowViewModel(
                        m.Id, m.DisplayName, m.ContextWindow, m.MaxOutputTokens,
                        m.SupportsReasoning, m.SupportsVision, m.SupportsToolUse,
                        m.Pricing.InputPerMillion, m.Pricing.OutputPerMillion));
                }
            }
            else
            {
                _logger.LogWarning("GetAllModelsAsync failed: {Error}", result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadModelsAsync exception");
        }
        finally
        {
            IsLoading = false;
        }
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
    /// <summary>Compact feature summary.</summary>
    public string Features =>
        string.Join(", ", new[]
        {
            SupportsToolUse ? "tools" : null,
            SupportsVision ? "vision" : null,
            SupportsReasoning ? "reasoning" : null,
        }.Where(s => s is not null)!) ?? "—";

    /// <summary>Per-million pricing label.</summary>
    public string PricingLabel => $"${InputPerMillion:F2} in / ${OutputPerMillion:F2} out per 1M";
}
