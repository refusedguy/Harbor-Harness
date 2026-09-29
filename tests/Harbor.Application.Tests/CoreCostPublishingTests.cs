using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
// Two stores answer to this name (this namespace has its own single-session
// fake); the TestKit one records what the core persisted, which is the half
// under test here.
using FakeSessionStore = Harbor.TestKit.FakeSessionStore;

namespace Harbor.Application.Tests;

/// <summary>
///     #653: the headless core is where a session gets priced. A model is
///     resolved here, called here, and its own rate table rides along with the
///     usage it returned — the total leaves as a <see cref="SessionStatsEvent" />
///     and the presentation layer has nothing left to compute.
/// </summary>
/// <remarks>
///     <para>
///         End-to-end on purpose: a real <see cref="AgentLoop" /> over a scripted
///         client, a real <see cref="DefaultAgent" />, and the real session
///         context that folds and publishes. A unit test on the fold alone would
///         pass even if the resolved model's rates never reached it — which is
///         precisely the wiring this issue was about.
///     </para>
///     <para>
///         The <see cref="ScriptedLlmClient" />'s <see cref="ModelInfo.Pricing" />
///         is the ONLY rate table in the process here. If the core ever fell back
///         to a constant, these numbers change.
///     </para>
/// </remarks>
public class CoreCostPublishingTests
{
    /// <summary>Claude-Opus-like rates, WITH the cache rates the constants threw away.</summary>
    private static readonly Pricing Priced =
        new(3m, 15m, CacheReadPerMillion: 0.30m, CacheWritePerMillion: 3.75m);

    private static ModelInfo Model(Pricing pricing) => new(
        "test-model", "test", "Test Model", 200_000, 4096, false, false, true, pricing, "openai");

    private static Session NewSession() =>
        Session.Create(TestTempDirs.NewDirectory("harbor-653-core"), "code", "test", "test-model");

    /// <summary>
    ///     Runs one prompt through a real agent and returns what it published,
    ///     plus the store it persisted into.
    /// </summary>
    private static async Task<(FakeEventBus Bus, FakeSessionStore Store, Result Run)> RunOneTurnAsync(
        Pricing pricing,
        Usage usage)
    {
        var client = new ScriptedLlmClient(
            Model(pricing),
            [[new TextDeltaEvent("t", "answer"), new StepFinishEvent(0, "stop", usage)]]);

        var bus = new FakeEventBus();
        var loop = TestLoops.Create(
            client, new FakeToolRegistry(), new FakeTokenTracker(), new FakeCompactionService(), bus);
        var session = NewSession();
        var store = new FakeSessionStore(session);
        using var agent = new DefaultAgent(store, loop, bus, NullLogger<DefaultAgent>.Instance);
        agent.Initialize(session, TestAgents.AllowAll());

        Result run = await agent.PromptAsync("hello").ConfigureAwait(false);
        return (bus, store, run);
    }

    /// <summary>The last session-stats snapshot the core published, or null.</summary>
    private static SessionMetadata? Published(FakeEventBus bus)
    {
        SessionMetadata? found = null;
        foreach (AgentEvent evt in bus.Events)
        {
            if (evt is SessionStatsEvent stats)
            {
                found = stats.Metadata;
            }
        }

        return found;
    }

    /// <summary>
    ///     The headline: a turn with cache traffic is priced with the model's own
    ///     cache rates. $10.50 is what the deleted $3/$15 constants printed for
    ///     the input and output alone, ignoring 1.4M cached tokens.
    /// </summary>
    [Test]
    public async Task Core_PricesTheTurn_WithTheModelsOwnCacheRates_AndPublishesIt()
    {
        var usage = new Usage(
            InputTokens: 1_000_000,
            OutputTokens: 500_000,
            ReasoningTokens: null,
            CacheReadTokens: 1_000_000,
            CacheWriteTokens: 400_000);

        var (bus, store, run) = await RunOneTurnAsync(Priced, usage);

        await Assert.That(run.IsSuccess).IsTrue();

        SessionMetadata? published = Published(bus);
        await Assert.That(published).IsNotNull()
            .Because("the core must publish the totals the UI displays — that event is the "
                     + "UI's only source now that the reducer prices nothing");

        // in $3.00 + out $7.50 + cache-read $0.30 + cache-write $1.50.
        await Assert.That(published!.Cost).IsEqualTo(12.30m);
        await Assert.That(published.IsCostKnown).IsTrue();
        await Assert.That(published.TokensInput).IsEqualTo(1_000_000);
        await Assert.That(published.TokensOutput).IsEqualTo(500_000);
        await Assert.That(published.TokensCacheRead).IsEqualTo(1_000_000);
        await Assert.That(published.TokensCacheWrite).IsEqualTo(400_000);

        // The store record is the same computation, not a second opinion.
        await Assert.That(store.UpdatedStats.Count).IsEqualTo(1);
        await Assert.That(store.UpdatedStats[0].Cost).IsEqualTo(12.30m);
        await Assert.That(store.UpdatedStats[0].IsCostKnown).IsTrue();

        decimal constantsWouldSay = 1_000_000 / 1_000_000m * 3m + 500_000 / 1_000_000m * 15m;
        await Assert.That(constantsWouldSay).IsEqualTo(10.50m);
        await Assert.That(published.Cost).IsNotEqualTo(constantsWouldSay);
    }

    /// <summary>
    ///     No cache traffic: input + output, at the model's own rates.
    /// </summary>
    [Test]
    public async Task Core_PricesTheTurn_WithoutCache_AtTheModelsRates()
    {
        var (bus, _, run) = await RunOneTurnAsync(Priced, new Usage(2_000_000, 1_000_000));

        await Assert.That(run.IsSuccess).IsTrue();
        SessionMetadata? published = Published(bus);
        await Assert.That(published).IsNotNull();
        await Assert.That(published!.Cost).IsEqualTo(21m);
        await Assert.That(published.IsCostKnown).IsTrue();
    }

    /// <summary>
    ///     A model that publishes no price (Ollama — and a paid provider whose
    ///     catalogue entry carries no rates) yields a zero total FLAGGED as
    ///     unknown, so the UI renders "—" instead of claiming "free".
    /// </summary>
    [Test]
    public async Task Core_ReportsAnUnpricedModel_AsUnknownRatherThanFree()
    {
        var (bus, _, run) = await RunOneTurnAsync(
            Pricing.Unknown,
            new Usage(61_600, 196, null, 10_000, 5_000));

        await Assert.That(run.IsSuccess).IsTrue();
        SessionMetadata? published = Published(bus);
        await Assert.That(published).IsNotNull();
        await Assert.That(published!.Cost).IsEqualTo(0m);
        await Assert.That(published.IsCostKnown).IsFalse();

        // Tokens are still exact — only the money is a floor.
        await Assert.That(published.TokensInput).IsEqualTo(61_600);
        await Assert.That(published.TokensOutput).IsEqualTo(196);
    }

    /// <summary>
    ///     The ambiguity, stated rather than papered over: a genuinely free model
    ///     and an unpriced one both publish a zero rate table, so the core reports
    ///     both as "cost unknown" and the status bar shows "—" in both cases. The
    ///     alternative — reading all-zeros as a measured free session — is the
    ///     lie #653 exists to remove, because a PAID provider with an unpriced
    ///     catalogue entry looks identical from the numbers alone.
    /// </summary>
    [Test]
    public async Task Core_CannotTellAFreeModelFromAnUnpricedOne_SoItSaysUnknown()
    {
        var (bus, _, run) = await RunOneTurnAsync(new Pricing(0m, 0m), new Usage(61_600, 196));

        await Assert.That(run.IsSuccess).IsTrue();
        SessionMetadata? published = Published(bus);
        await Assert.That(published).IsNotNull();
        await Assert.That(published!.Cost).IsEqualTo(0m);
        await Assert.That(published.IsCostKnown).IsFalse();
        await Assert.That(Pricing.Unknown.IsUnknown).IsTrue();
        await Assert.That(new Pricing(0m, 0m).IsUnknown).IsTrue();
    }
}
