// CostAlertThreshold.cs: #2680 F130. Pure primitive for the cost-alert
// threshold: parse the argument of `/cost-alert <amount|off>` into a validated
// value and compare it with session spend. It reads no config, starts no
// process, touches no TUI and raises no event. It is the value half of the
// feature, the part that can be decided before anything is displayed.
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Harbor.Tools.Builtin;

/// <summary>
///     A parsed cost-alert threshold (#2680 F130). Immutable, stateless and
///     thread-safe. A disabled threshold never fires.
/// </summary>
/// <remarks>
///     <para>
///         The accepted grammar is a positive USD amount with at most two
///         decimal places, with an optional leading <c>$</c>, or one of the
///         words <c>off</c>, <c>none</c> and <c>disabled</c>. All comparisons
///         are ordinal and never culture-sensitive.
///     </para>
///     <para>
///         An instance is created only by <see cref="Parse" />,
///         <see cref="TryParse" /> or <see cref="Disabled" />, so an enabled
///         threshold always carries a positive, two-decimal amount.
///     </para>
/// </remarks>
public sealed record CostAlertThreshold
{
    /// <summary>The largest amount accepted from configuration.</summary>
    public const decimal MaxAmountUsd = 1_000m * 1_000m;

    private static readonly string[] DisabledWords = ["off", "none", "disabled"];

    private CostAlertThreshold(decimal amountUsd) => AmountUsd = amountUsd;

    /// <summary>The threshold that never fires.</summary>
    public static CostAlertThreshold Disabled { get; } = new(0m);

    /// <summary>The amount in USD when enabled, otherwise zero.</summary>
    public decimal AmountUsd { get; }

    /// <summary>Whether this threshold can fire.</summary>
    public bool IsEnabled => !ReferenceEquals(this, Disabled);

    /// <summary>
    ///     True when <paramref name="spentUsd" /> is strictly above an enabled
    ///     threshold. A disabled threshold is never exceeded.
    /// </summary>
    /// <param name="spentUsd">Session spend in USD.</param>
    public bool IsExceeded(decimal spentUsd) => IsEnabled && spentUsd > AmountUsd;

    /// <summary>
    ///     Parses <paramref name="input" /> into a threshold, or returns
    ///     <see langword="null" /> when the text is not a valid threshold.
    /// </summary>
    /// <param name="input">A slash-command argument such as <c>$5.50</c> or <c>off</c>.</param>
    public static CostAlertThreshold? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string text = input.Trim();
        foreach (string word in DisabledWords)
        {
            if (text.Equals(word, StringComparison.OrdinalIgnoreCase)) return Disabled;
        }

        if (text[0] == '$') text = text[1..].Trim();
        if (text.Length == 0 || text[0] == '+' || text[0] == '-') return null;

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount))
            return null;
        if (amount <= 0m || amount > MaxAmountUsd) return null;

        decimal cents = amount * 100m;
        if (decimal.Truncate(cents) != cents) return null;

        return new CostAlertThreshold(amount);
    }

    /// <summary>
    ///     Parses <paramref name="input" /> without throwing.
    ///     <paramref name="result" /> is non-null exactly when the method
    ///     returns <see langword="true" />.
    /// </summary>
    /// <param name="input">A slash-command argument such as <c>$5.50</c> or <c>off</c>.</param>
    /// <param name="result">The parsed threshold, or <see langword="null" /> on failure.</param>
    public static bool TryParse(string? input, [NotNullWhen(true)] out CostAlertThreshold? result)
    {
        result = Parse(input);
        return result is not null;
    }

    /// <summary>Renders the canonical slash-command argument.</summary>
    public override string ToString() =>
        IsEnabled ? "$" + AmountUsd.ToString("0.00", CultureInfo.InvariantCulture) : "off";
}
