using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

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
