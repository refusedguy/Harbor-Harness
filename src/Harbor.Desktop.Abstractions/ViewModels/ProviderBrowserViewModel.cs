using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Providers;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Browse providers + models with metadata. Read-only view; configuring a new
///     provider is done in <see cref="SettingsViewModel" />. Uses
///     <see cref="AsyncFeed{T}" /> to own cancellation + timeout + state, and
///     <see cref="AsyncDataBinder" /> for the status → surface mapping it shares
///     with the model picker (#484).
/// </summary>
/// <remarks>
///     The browser bounds the same call the picker bounds — the registry's
///     <c>GetAllModelsAsync</c> fan-out — and answers the same question ("what
///     models does this provider list?"), so it spends the same declared budget,
///     <see cref="ProviderModelPickerViewModel.UiFeedbackBudget" />. It used to
///     hold a private copy of that number under a probe-flavoured name, which is
///     what let two views disagree about how long a catalogue may take while
///     both looked self-consistent. It is NOT the provider-probe canon
///     (<c>IProviderHealthCheck.DefaultTimeout</c>): that budget answers "can
///     this provider answer?", and a user watching an empty list needs a
///     different answer, faster (#685).
/// </remarks>
public sealed partial class ProviderBrowserViewModel : ObservableObject, IAsyncDataSink<ModelRowViewModel>
{
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
        // The budget is the picker's, not this view's: same fan-out, same question (#685).
        _modelsFeed = new AsyncFeed<IReadOnlyList<ModelRowViewModel>>(
            LoadModelsAsync, ProviderModelPickerViewModel.UiFeedbackBudget, _logger);
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

    private void OnModelsChanged(AsyncData<IReadOnlyList<ModelRowViewModel>> data) =>
        AsyncDataBinder.Apply(data, this);

    /// <inheritdoc />
    public void OnLoading()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        Models.Clear();
    }

    /// <inheritdoc />
    public void OnLoaded(IReadOnlyList<ModelRowViewModel> models)
    {
        IsLoading = false;
        ErrorMessage = string.Empty;

        Models.Clear();
        for (int i = 0; i < models.Count; i++)
            Models.Add(models[i]);

        if (Models.Count == 0)
            ErrorMessage = ProviderModelLoadMessages.NoModelsForProvider(_currentProviderId);
    }

    /// <inheritdoc />
    public void OnError(string message)
    {
        IsLoading = false;
        _logger.LogWarning("Model fetch error: {Error}", message);
        ErrorMessage = ProviderModelLoadMessages.LoadFailed(message);
    }

    private async Task<Result<IReadOnlyList<ModelRowViewModel>>> LoadModelsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentProviderId))
            return Result.Failure<IReadOnlyList<ModelRowViewModel>>("No provider selected");

        var result = await _providers.GetAllModelsAsync(ct).ConfigureAwait(true);
        if (result.IsFailure)
            return result.ConvertFailure<IReadOnlyList<ModelRowViewModel>>();

        var models = result.Value
            .Where(m => m.ProviderId == _currentProviderId)
            .Select(m => new ModelRowViewModel(
                m.Id, m.DisplayName, m.ContextWindow, m.MaxOutputTokens,
                m.SupportsReasoning, m.SupportsVision, m.SupportsToolUse,
                ModelRateLabel.For(m.Pricing)))
            .ToImmutableArray();

        return Result.Success<IReadOnlyList<ModelRowViewModel>>(models);
    }
}

/// <summary>One row in the provider list.</summary>
public sealed record ProviderRowViewModel(string Id, string Name, string Status);

/// <summary>
///     One row in the model list.
///     <para>
///         #686: the row carries <c>PricingLabel</c>, not the two rates. A row that
///         formats its own rates is a row free to disagree with the other row —
///         which is how the browser came to print "$0.00 in / $0.00 out per 1M"
///         for a model whose catalogue entry has no rates at all. The wording and
///         the unknown-price decision both live in <see cref="ModelRateLabel" />,
///         shared with the picker dropdown.
///     </para>
/// </summary>
/// <param name="PricingLabel">PROBE-701.</param>
public sealed record ModelRowViewModel(
    string Id,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsReasoning,
    bool SupportsVision,
    bool SupportsToolUse,
    string PricingLabel)
{
    public string Features =>
        string.Join(", ", new[]
        {
            SupportsToolUse ? "tools" : null,
            SupportsVision ? "vision" : null,
            SupportsReasoning ? "reasoning" : null
        }.Where(s => s is not null)!) ?? "—";
}
