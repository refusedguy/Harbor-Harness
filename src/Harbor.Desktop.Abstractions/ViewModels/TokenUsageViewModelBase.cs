using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Desktop canon base for token-usage VMs: UiState selectors for totals; platform
///     VMs project rows/bars. Distinct from the Framework TEA-projection
///     <c>TokenUsageViewModel</c> (Avalonia) and the shell-local WPF/Blazor chart VMs
///     (vm-dedup audit 27-G — do not merge).
/// </summary>
public abstract partial class TokenUsageViewModelBase : StoreSubscriberViewModel
{

    [ObservableProperty]
    private decimal _estimatedCostUsd;

    [ObservableProperty]
    private int _totalCachedTokens;

    [ObservableProperty]
    private int _totalInputTokens;

    [ObservableProperty]
    private int _totalOutputTokens;

    protected TokenUsageViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => (int)state.Cost.TokensIn, v => TotalInputTokens = v);
        Select(state => (int)state.Cost.TokensOut, v => TotalOutputTokens = v);
        Select(state => state.Cost.CostUsd, v => EstimatedCostUsd = v);
    }

    public ObservableCollection<TokenUsageRow> Rows { get; } = new();

    protected abstract Task RefreshAsync(CancellationToken cancellationToken);

    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }
}

public sealed record TokenUsageRow(
    string ModelId,
    int InputTokens,
    int OutputTokens,
    int CachedTokens,
    decimal CostUsd);
