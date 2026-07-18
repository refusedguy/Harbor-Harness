namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the token-usage view-model. Holds the per-session token counts
///     (input, output, cached) and a breakdown by model. Platform VMs render
///     the chart (Avalonia LiveCharts, WPF LiveCharts, Blazor Chart.js).
/// </summary>
public abstract partial class TokenUsageViewModelBase : ViewModelBase
{
    /// <summary>Construct a <see cref="TokenUsageViewModelBase"/>.</summary>
    protected TokenUsageViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Visible per-message token rows, projected for the view layer.</summary>
    public ObservableCollection<TokenUsageRow> Rows { get; } = new();

    /// <summary>Sum of input (prompt) tokens across all rows.</summary>
    [ObservableProperty]
    private int _totalInputTokens;

    /// <summary>Sum of output (completion) tokens across all rows.</summary>
    [ObservableProperty]
    private int _totalOutputTokens;

    /// <summary>Sum of cached prompt tokens across all rows.</summary>
    [ObservableProperty]
    private int _totalCachedTokens;

    /// <summary>Estimated cost in USD, computed from the row rates.</summary>
    [ObservableProperty]
    private decimal _estimatedCostUsd;

    /// <summary>Refresh the rows from the session's events. Implemented by the platform VM.</summary>
    protected abstract Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
///     One token-usage row, projected for the UI.
/// </summary>
/// <param name="ModelId">Model id (e.g. "gpt-4o").</param>
/// <param name="InputTokens">Prompt tokens.</param>
/// <param name="OutputTokens">Completion tokens.</param>
/// <param name="CachedTokens">Cached prompt tokens.</param>
/// <param name="CostUsd">Estimated cost in USD.</param>
public sealed record TokenUsageRow(
    string ModelId,
    int InputTokens,
    int OutputTokens,
    int CachedTokens,
    decimal CostUsd);
