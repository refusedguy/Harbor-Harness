using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Token-usage chart view-model. Records a snapshot per turn (input/output tokens
///     + cost) and exposes them as bar-chart rows for the <c>TokenUsageView</c>.
/// </summary>
public sealed partial class TokenUsageViewModel : ObservableObject
{
    private readonly ILogger<TokenUsageViewModel> _logger;
    private long _lastTokensIn;
    private long _lastTokensOut;
    private int _turnIndex;

    /// <summary>Construct the chart view-model.</summary>
    public TokenUsageViewModel(ILogger<TokenUsageViewModel> logger)
    {
        _logger = logger;
    }

    /// <summary>One bar per turn.</summary>
    public ObservableCollection<TokenUsageBarViewModel> Bars { get; } = new();

    [ObservableProperty]
    private long _totalTokensIn;

    [ObservableProperty]
    private long _totalTokensOut;

    [ObservableProperty]
    private decimal _totalCostUsd;

    /// <summary>Sample the current UiState and append a new bar when tokens changed.</summary>
    /// <param name="state">Current UI snapshot.</param>
    public void RecordUsage(UiState state)
    {
        if (state.Cost.TokensIn == _lastTokensIn && state.Cost.TokensOut == _lastTokensOut) return;
        long deltaIn = state.Cost.TokensIn - _lastTokensIn;
        long deltaOut = state.Cost.TokensOut - _lastTokensOut;
        if (deltaIn < 0 || deltaOut < 0)
        {
            // Session reset — start fresh.
            _lastTokensIn = state.Cost.TokensIn;
            _lastTokensOut = state.Cost.TokensOut;
            return;
        }
        _turnIndex++;
        Dispatcher.UIThread.Post(() =>
        {
            Bars.Add(new TokenUsageBarViewModel(_turnIndex, deltaIn, deltaOut, state.Cost.CostUsd));
            // Cap the chart to last 50 turns.
            while (Bars.Count > 50) Bars.RemoveAt(0);
            TotalTokensIn = state.Cost.TokensIn;
            TotalTokensOut = state.Cost.TokensOut;
            TotalCostUsd = state.Cost.CostUsd;
        });
        _lastTokensIn = state.Cost.TokensIn;
        _lastTokensOut = state.Cost.TokensOut;
    }

    /// <summary>Clear all bars.</summary>
    [RelayCommand]
    private void Clear()
    {
        Bars.Clear();
        _turnIndex = 0;
        _lastTokensIn = 0;
        _lastTokensOut = 0;
        TotalTokensIn = 0;
        TotalTokensOut = 0;
        TotalCostUsd = 0;
    }
}

/// <summary>One token-usage bar.</summary>
public sealed record TokenUsageBarViewModel(int Turn, long TokensIn, long TokensOut, decimal CumulativeCostUsd)
{
    /// <summary>Max height (in tokens) for the bar — clamped for rendering.</summary>
    public long Max => Math.Max(Math.Max(TokensIn, TokensOut), 1);

    /// <summary>Input-tokens fraction (0..1) of Max.</summary>
    public double InFraction => Max == 0 ? 0 : (double)TokensIn / Max;

    /// <summary>Output-tokens fraction (0..1) of Max.</summary>
    public double OutFraction => Max == 0 ? 0 : (double)TokensOut / Max;
}
