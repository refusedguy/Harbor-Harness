using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.ViewModels;
/// <summary>
///     Token-usage chart view-model. Records a snapshot per turn (input/output tokens
///     + cost) and exposes them as bar-chart rows for the <c>TokenUsageView</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>vm-dedup canon (audit 27-G):</b> canonical TEA-projection token VM for
///         Avalonia (<c>RecordUsage</c> over UiState; <c>Clear</c>/<c>Reset</c> shared
///         with the session-switch path). Not the same as the Desktop
///         <c>TokenUsageViewModelBase</c> selector base or the shell-local WPF/Blazor
///         chart VMs — do not merge without a logic refactor.
///     </para>
///     <para>
///         <b>Task R1 — Sparkline:</b> in addition to the existing
///         per-turn bar chart, this view-model now exposes
///         <see cref="RecentOutputTokens" /> — a list of the last 30
///         turns' output-token counts, suitable for binding to the
///         <c>Sparkline</c> control in the status bar. Updates live as
///         <see cref="RecordUsage" /> adds new bars.
///     </para>
/// </remarks>
public sealed partial class TokenUsageViewModel : ObservableObject
{
    private readonly ILogger<TokenUsageViewModel> _logger;
    private long _lastTokensIn;
    private long _lastTokensOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCostText))]
    private decimal _totalCostUsd;

    /// <summary>
    ///     Whether the core could NOT price this session. Rides along with
    ///     <see cref="TotalCostUsd" /> because a zero there is a floor, not a
    ///     bill, and the view must not print it as an amount (#942).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCostText))]
    private bool _isCostUnpriced;

    /// <summary>
    ///     The cumulative-cost cell the overlay paints: the priced total, or
    ///     <see cref="StatusBarText.UnknownCostCell" /> when the core said it
    ///     could not price this model.
    /// </summary>
    /// <remarks>
    ///     #942: this used to be the bare decimal with the glyph and the decimals
    ///     supplied by the XAML <c>StringFormat='{}${0:F4}'</c>, so an unpriced
    ///     session printed "$0.0000" — and a <c>StringFormat</c> is invisible to
    ///     <c>MoneyCellSingleHomeRules</c>, which scans <c>*.cs</c> only. The cell
    ///     is spelled here instead, next to the number it describes, which also
    ///     drops the last hand-rolled money format out of the Avalonia tree.
    /// </remarks>
    public string TotalCostText => IsCostUnpriced
        ? StatusBarText.UnknownCostCell
        : StatusBarText.CostToUsd(TotalCostUsd);

    [ObservableProperty]
    private long _totalTokensIn;

    [ObservableProperty]
    private long _totalTokensOut;
    private int _turnIndex;

    /// <summary>Construct the chart view-model.</summary>
    public TokenUsageViewModel(ILogger<TokenUsageViewModel> logger)
    {
        _logger = logger;
    }

    /// <summary>One bar per turn.</summary>
    public ObservableCollection<TokenUsageBarViewModel> Bars { get; } = new();

    /// <summary>
    ///     Recent output-token counts (last 30 turns), exposed for the
    ///     status-bar sparkline. Capped to keep the sparkline compact.
    /// </summary>
    public ObservableCollection<double> RecentOutputTokens { get; } = new();

    /// <summary>Sample the current UiState and append a new bar when tokens changed.</summary>
    /// <param name="state">Current UI snapshot.</param>
    /// <remarks>
    ///     This method is called from <see cref="MainViewModel.OnStoreChanged" />,
    ///     which itself runs inside a <c>Dispatcher.UIThread.Post</c> — so we
    ///     are already on the UI thread when this is invoked. No inner
    ///     marshaling needed (and trying to Post again would break headless
    ///     tests where no dispatcher is pumping). Callers from a background
    ///     thread are responsible for marshaling.
    /// </remarks>
    public void RecordUsage(UiState state)
    {
        // #942: the money cell is synced BEFORE the token guard below, not after
        // it. That guard exists to add one bar per turn, so it correctly skips a
        // frame where no tokens moved — but the core's "could not price this"
        // bit is not a per-turn quantity. It arrives on the same event as the
        // tokens and flips when it flips, and a session whose first stats frame
        // is already unpriced would otherwise keep the default (priced) bit and
        // paint "$0.0000" for a model that has no rate table at all.
        TotalCostUsd = state.Chat.Cost.CostUsd;
        IsCostUnpriced = state.Chat.Cost.IsCostUnpriced;

        if (state.Chat.Cost.TokensIn == _lastTokensIn && state.Chat.Cost.TokensOut == _lastTokensOut) return;
        long deltaIn = state.Chat.Cost.TokensIn - _lastTokensIn;
        long deltaOut = state.Chat.Cost.TokensOut - _lastTokensOut;
        if (deltaIn < 0 || deltaOut < 0)
        {
            // Session reset — start fresh.
            _lastTokensIn = state.Chat.Cost.TokensIn;
            _lastTokensOut = state.Chat.Cost.TokensOut;
            return;
        }
        _turnIndex++;

        Bars.Add(new TokenUsageBarViewModel(_turnIndex, deltaIn, deltaOut, state.Chat.Cost.CostUsd));
        // Cap the chart to last 50 turns.
        while (Bars.Count > 50)
        {
            Bars.RemoveAt(0);
        }

        // Sparkline series — cap at 30 for compact status-bar display.
        RecentOutputTokens.Add(deltaOut);
        while (RecentOutputTokens.Count > 30)
        {
            RecentOutputTokens.RemoveAt(0);
        }

        TotalTokensIn = state.Chat.Cost.TokensIn;
        TotalTokensOut = state.Chat.Cost.TokensOut;

        _lastTokensIn = state.Chat.Cost.TokensIn;
        _lastTokensOut = state.Chat.Cost.TokensOut;
    }

    /// <summary>
    ///     Clear all bars + sparkline + baseline. Called when the user
    ///     switches sessions so the chart reflects only the active
    ///     session's token usage (not the cumulative total across all
    ///     sessions — a <see cref="AppMsg.Reset" /> zeroes Cost, but the previous
    ///     session's bars would otherwise linger).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Task S3 — per-session reset:</b> this is the canonical
    ///         reset entry point. <see cref="Clear" /> (the
    ///         <c>[RelayCommand]</c> wrapper that backs the
    ///         <c>ClearCommand</c> binding in <c>TokenUsageView.axaml</c>)
    ///         delegates here so the UI button and the
    ///         <see cref="SessionManager" /> switch-path share a single
    ///         source of truth.
    ///     </para>
    /// </remarks>
    public void Reset()
    {
        Bars.Clear();
        RecentOutputTokens.Clear();
        _turnIndex = 0;
        _lastTokensIn = 0;
        _lastTokensOut = 0;
        TotalTokensIn = 0;
        TotalTokensOut = 0;
        TotalCostUsd = 0;
        IsCostUnpriced = false;
    }

    /// <summary>
    ///     <c>[RelayCommand]</c> wrapper around <see cref="Reset" /> so the
    ///     <c>Clear</c> button in <c>TokenUsageView.axaml</c> can bind to
    ///     <c>ClearCommand</c>. Delegates to <see cref="Reset" /> — the
    ///     canonical entry point used by both the UI and the
    ///     <see cref="SessionManager" /> session-switch path.
    /// </summary>
    [RelayCommand]
    public void Clear() => Reset();
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
