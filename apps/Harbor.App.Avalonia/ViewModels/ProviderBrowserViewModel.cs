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
    /// <summary>
    ///     Hard cap on how long a single model-list fetch may take. The
    ///     default 100s HttpClient timeout made the UI feel frozen when a
    ///     provider (typically Ollama) wasn't running. Five seconds is long
    ///     enough for a healthy local provider to respond, short enough that
    ///     the user doesn't think the app hung.
    /// </summary>
    public static readonly TimeSpan ModelFetchTimeout = TimeSpan.FromSeconds(5);

    private readonly IProviderRegistry _providers;
    private readonly ILogger<ProviderBrowserViewModel> _logger;
    private CancellationTokenSource? _loadCts;

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

    /// <summary>
    ///     Error message shown in the UI when a model fetch fails (e.g. "Ollama
    ///     not running on http://localhost:11434 — start it with `ollama serve`.").
    /// </summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>Load providers from the registry.</summary>
    [RelayCommand]
    private async Task LoadProvidersAsync()
    {
        // Cancel any in-flight load (e.g. the user re-opened the browser
        // while a previous fetch was still running).
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();

        IsLoading = true;
        ErrorMessage = string.Empty;
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

    /// <summary>Load models for the selected provider, with a 5-second timeout.</summary>
    /// <param name="row">The provider row to load models for. No-op when null.</param>
    [RelayCommand]
    private async Task LoadModelsAsync(ProviderRowViewModel? row)
    {
        if (row is null) return;

        // Cancel any previous load. The shared CTS means opening the browser
        // and then immediately picking a different provider cancels the first
        // fetch — no orphaned Task.Runs piling up behind the UI.
        _loadCts?.Cancel();
        _loadCts?.Dispose();

        var cts = new CancellationTokenSource(ModelFetchTimeout);
        _loadCts = cts;

        IsLoading = true;
        ErrorMessage = string.Empty;
        Models.Clear();
        try
        {
            var result = await _providers.GetAllModelsAsync(cts.Token).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                foreach (var m in result.Value.Where(m => m.ProviderId == row.Id))
                {
                    Models.Add(new ModelRowViewModel(
                        m.Id, m.DisplayName, m.ContextWindow, m.MaxOutputTokens,
                        m.SupportsReasoning, m.SupportsVision, m.SupportsToolUse,
                        m.Pricing.InputPerMillion, m.Pricing.OutputPerMillion));
                }

                if (Models.Count == 0)
                {
                    ErrorMessage = $"No models returned by provider '{row.Id}'. " +
                                   "If this is a local provider (e.g. Ollama), make sure it's running.";
                }
            }
            else
            {
                _logger.LogWarning("GetAllModelsAsync failed: {Error}", result.Error);
                ErrorMessage = result.Error;
            }
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Model fetch for {Provider} cancelled/timed out after {Timeout}s",
                row.Id, ModelFetchTimeout.TotalSeconds);
            ErrorMessage = $"Timed out after {ModelFetchTimeout.TotalSeconds:F0}s — is '{row.Id}' running?";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadModelsAsync exception");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            cts.Dispose();
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
