using CommunityToolkit.Mvvm.Messaging;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
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
    [Test]
    [Arguments(0m, 0m, "pricing unknown")]
    [Arguments(3m, 15m, "$3.00 in / $15.00 out per 1M")]
    [Arguments(0m, 15m, "$0.00 in / $15.00 out per 1M")]
    [Arguments(3m, 0m, "$3.00 in / $0.00 out per 1M")]
    [Arguments(0.075m, 0.25m, "$0.08 in / $0.25 out per 1M")]
    public async Task For_FormatsTheRateCard(
        decimal inputPerMillion, decimal outputPerMillion, string expected)
    {
        var pricing = new Pricing(inputPerMillion, outputPerMillion);

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
        var catalogue = new FakeRegistry
        {
            Models = Result.Success<IReadOnlyList<ModelInfo>>(
            [
                Model("priced-model", new Pricing(3m, 15m)),
                Model("unpriced-model", Pricing.Unknown),
            ])
        };

        var picker = new ProviderModelPickerViewModel(
            catalogue,
            new StubConfigStore(),
            new StubSessionManager(),
            new StubToastService(),
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

    /// <summary>Registry with one provider and a caller-controlled model catalog.</summary>
    private sealed class FakeRegistry : IProviderRegistry
    {
        public Result<IReadOnlyList<ModelInfo>> Models { get; set; } =
            Result.Success<IReadOnlyList<ModelInfo>>([]);

        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [ProviderId.Create("ollama")];

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            Result.Failure<ILlmClient>("not registered");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Models);

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
            GetAllModelsAsync(cancellationToken);

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

        public Result Unregister(ProviderId providerId) => Result.Failure("not supported");
    }

    private sealed class StubConfigStore : ICommonConfigStore
    {
        public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new CommonConfig()));

        public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class StubToastService : IToastService
    {
#pragma warning disable CS0067
        public event EventHandler<ToastNotification>? ToastAdded;
#pragma warning restore CS0067

        public void Show(string message, ToastKind kind = ToastKind.Info) { }
    }

    private sealed class StubSessionManager : ISessionManager
    {
        public Session? Active => null;
        public SessionContext? ActiveContext => null;
        public SessionContext? GetContext(string sessionId) => null;
        public GitSessionInfo GetGitInfo(string sessionId) => new(null, false, 0, null);
        public void RefreshGitInfo(string sessionId, string directory) { }
        public Task EnsureDefaultSessionAsync() => Task.CompletedTask;
        public Task RebindFromCommonConfigAsync() => Task.CompletedTask;

        public Task<Result<Session>> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null) =>
            Task.FromResult(Result.Failure<Session>("not configured"));

        public Task<bool> OpenSessionAsync(string sessionId) => Task.FromResult(true);

        public Task<Result<Session>> BranchActiveAsync() => Task.FromResult(Result.Failure<Session>("not configured"));
        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(true);
        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(true);
        public SessionStatus GetStatus(string sessionId) => SessionStatus.Idle;
        public void SetStatus(string sessionId, SessionStatus status) { }
        public void NotifyMessageCount(string sessionId, int count) { }

#pragma warning disable CS0067
        public event Action<string, SessionStatus>? StatusChanged;
        public event Action<string, int>? MessageCountChanged;
#pragma warning restore CS0067

        public string? GetDirectory(string sessionId) => null;
        public string? GetStatusText(string sessionId) => null;
        public string? GetBranch(string sessionId) => null;
        public bool GetIsDirty(string sessionId) => false;
        public bool? GetIsSubagent(string sessionId) => null;
    }
}
