using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Desktop.Abstractions.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #686 (pricing half) — the two model pickers are one user-facing surface
///     split across two view-models, so the same model must read the same in both.
/// </summary>
/// <remarks>
///     The two copies were <c>ProviderModelPickerViewModel.FormatPricing</c> and
///     <c>ModelRowViewModel.PricingLabel</c>, and they had already drifted: for a
///     zero-rate catalogue entry the picker said <c>"pricing unknown"</c> and the
///     browser said <c>"$0.00 in / $0.00 out per 1M"</c>. The second is a price
///     claim the catalogue never made, and the core says so itself in
///     <c>Pricing.IsUnknown</c>'s remark — "a '$0.0000' is a claim, and for the
///     second case it is a false one."
/// </remarks>
public class ModelRateLabelTests
{
    // Rates travel as strings because a `decimal` literal is not a legal C#
    // attribute argument (CS0182) — the values are parsed here, in the test, so
    // the table stays readable and the formatter still sees real decimals.
    [Test]
    [Arguments("0", "0", "pricing unknown")]
    [Arguments("3", "15", "$3.00 in / $15.00 out per 1M")]
    [Arguments("0", "15", "$0.00 in / $15.00 out per 1M")]
    [Arguments("3", "0", "$3.00 in / $0.00 out per 1M")]
    [Arguments("0.075", "0.25", "$0.08 in / $0.25 out per 1M")]
    public async Task For_FormatsTheRateCard(string inputPerMillion, string outputPerMillion, string expected)
    {
        var pricing = new Pricing(
            decimal.Parse(inputPerMillion, CultureInfo.InvariantCulture),
            decimal.Parse(outputPerMillion, CultureInfo.InvariantCulture));

        await Assert.That(ModelRateLabel.For(pricing)).IsEqualTo(expected);
    }

    /// <summary>
    ///     The label is the core's bit, not a re-derivation from two numbers: a
    ///     model priced ONLY on cache reads is priced, even though its input and
    ///     output rates are both zero. The picker's old
    ///     <c>if (inputPerMillion == 0m &amp;&amp; outputPerMillion == 0m)</c> called
    ///     that "pricing unknown" — a false negative, and a second place deciding a
    ///     fact <c>Pricing.IsUnknown</c> already publishes.
    /// </summary>
    [Test]
    public async Task For_CacheOnlyPricedModel_IsNotReportedAsUnpriced()
    {
        var cacheOnly = new Pricing(0m, 0m, CacheReadPerMillion: 0.30m, CacheWritePerMillion: 3.75m);

        await Assert.That(cacheOnly.IsUnknown).IsFalse();
        await Assert.That(ModelRateLabel.For(cacheOnly)).DoesNotContain("unknown");
    }

    /// <summary>
    ///     <c>Pricing.Unknown</c> is the sentinel the core hands out, so it must
    ///     read as unpriced rather than as a free model.
    /// </summary>
    [Test]
    public async Task For_TheCoreUnknownSentinel_SaysSo()
    {
        await Assert.That(ModelRateLabel.For(Pricing.Unknown)).IsEqualTo(ModelRateLabel.UnknownText);
    }

    /// <summary>
    ///     The regression itself, driven through BOTH real view-models' public load
    ///     commands over the same catalogue: the browser row and the picker row
    ///     must carry one label between them. Before the convergence this failed for
    ///     every unpriced model — the only case the two copies disagreed on.
    /// </summary>
    [Test]
    public async Task BothPickers_PriceTheSameModelTheSameWay()
    {
        var catalogue = new PricedModelRegistry(
            ("priced-model", new Pricing(3m, 15m)),
            ("unpriced-model", Pricing.Unknown));

        var picker = new ProviderModelPickerViewModel(
            catalogue,
            new EmptyApiKeyConfigStore(),
            new NoSessionsManager(),
            new SilentToastService(),
            NullLogger<ProviderModelPickerViewModel>.Instance,
            WeakReferenceMessenger.Default,
            new NoKeysAnywhereAuthResolver());
        var browser = new ProviderBrowserViewModel(
            catalogue, NullLogger<ProviderBrowserViewModel>.Instance);

        await picker.LoadCommand.ExecuteAsync(null);
        await browser.LoadProvidersCommand.ExecuteAsync(null);

        Dictionary<string, string> pickerRows = picker.AllProviders
            .SelectMany(g => g.Models)
            .ToDictionary(m => m.Id, m => m.PricingText);
        Dictionary<string, string> browserRows = browser.Models
            .ToDictionary(m => m.Id, m => m.PricingLabel);

        await Assert.That(pickerRows.Keys.Order()).IsEquivalentTo(browserRows.Keys);
        foreach (string id in pickerRows.Keys)
        {
            await Assert.That(browserRows[id]).IsEqualTo(pickerRows[id])
                .Because(
                    "model '" + id + "' is priced \"${pickerRows[id]}\" in the picker dropdown and "
                    + "\"{browserRows[id]}\" in the provider browser — the same model must not read "
                    + "two ways (#686).");
        }

        await Assert.That(browserRows["unpriced-model"]).IsEqualTo(ModelRateLabel.UnknownText)
            .Because("an entry with no rates must not be shown as a $0.00 price claim");
    }

    private static ModelInfo Model(string modelId, Pricing pricing) =>
        new(modelId, "ollama", modelId, 128_000, 8_192,
            false, false, true, pricing, "openai");

    /// <summary>
    ///     One provider whose catalogue the caller names outright, with each model's
    ///     rate table under the test's control — the existing
    ///     <see cref="StaticModelRegistry" /> serves <see cref="Pricing.Unknown" />
    ///     for every model and cannot express a priced one.
    /// </summary>
    private sealed class PricedModelRegistry(params (string ModelId, Pricing Pricing)[] catalogue) : IProviderRegistry
    {
        private const string Provider = "ollama";

        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [ProviderId.Create(Provider)];

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            Result.Failure<ILlmClient>("not registered");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(
                [.. catalogue.Select(m => Model(m.ModelId, m.Pricing))]));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
            GetAllModelsAsync(cancellationToken);

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

        public Result Unregister(ProviderId providerId) => Result.Failure("not supported");
    }
}
