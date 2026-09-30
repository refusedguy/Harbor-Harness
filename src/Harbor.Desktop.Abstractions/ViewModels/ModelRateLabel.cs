using Harbor.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Wording of the per-1M rate label, shared by the model picker and the
///     provider browser so the SAME model reads the same in both: two private
///     copies had already drifted, and the copy without a guard printed
///     <c>$0.00 in / $0.00 out per 1M</c> for a model whose catalogue entry
///     carries no rates at all — a price claim the catalogue never made.
/// </summary>
/// <remarks>
///     <para>
///         The unknown-price question is not answered here. It is
///         <see cref="Pricing.IsUnknown" />, the core's own bit,
///         and this type only spells its answer. The two were previously
///         conflated: the picker re-derived the bit from the input and output
///         numbers alone, which not only duplicated a core decision but got the
///         cache-rate case wrong — a model priced only on cache reads was
///         reported as unpriced while <c>IsUnknown</c> correctly calls it priced.
///     </para>
///     <para>
///         #686. The single-surface guard is
///         <c>tests/Harbor.Architecture.Tests/ModelRateLabelRules.cs</c>; this file
///         is the one place it allows a rate to be spelled into a string, so a
///         second sentence for the same job has to be added there as a decision,
///         not written in a second view-model.
///     </para>
/// </remarks>
public static class ModelRateLabel
{
    /// <summary>What a model row says when its catalogue entry publishes no rates.</summary>
    public const string UnknownText = "pricing unknown";

    /// <summary>
    ///     The per-1M rate label for a model, or <see cref="UnknownText" /> when the
    ///     model publishes no price table at all.
    /// </summary>
    /// <param name="pricing">The model's rate table, as the core published it.</param>
    public static string For(Pricing pricing) =>
        pricing.IsUnknown
            ? UnknownText
            : $"${pricing.InputPerMillion:F2} in / ${pricing.OutputPerMillion:F2} out per 1M";
}
