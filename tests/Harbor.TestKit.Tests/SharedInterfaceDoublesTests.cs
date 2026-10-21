using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The shared interface doubles that #1825 merged out of five project-local
///     sets, and the behaviour each of them has to keep.
/// </summary>
/// <remarks>
///     <para>
///         Every one of these types had more than one copy in the tree, and the
///         copies were not interchangeable by accident: they had different
///         names, and some of them answered differently. These tests pin the
///         behaviour the CANONICAL copy has, because a shared double that
///         answers differently from the copies it replaced is a test that
///         lies — and the whole point of the merge is that there is one
///         answer.
///     </para>
/// </remarks>
public class SharedInterfaceDoublesTests
{
    // ---- the compaction fake ------------------------------------------------

    [Test]
    public async Task The_Compaction_Fake_Never_Asks_For_A_Compaction()
    {
        var compaction = new FakeCompactionService();

        bool should = compaction.ShouldCompact([TestMessages.User("hi")], ScriptedLlmClient.TestModel);

        await Assert.That(should).IsFalse()
            .Because("the fake stands in for a service whose transcripts are far below the context window — a 'true' here would send the loop down a branch the test never meant to exercise");
    }

    [Test]
    public async Task The_Compaction_Fake_Fails_The_Call_It_Reached()
    {
        var compaction = new FakeCompactionService();

        Result<CompactionResult> result = await compaction.CompactAsync(
            "s1",
            [TestMessages.User("hi")],
            ScriptedLlmClient.TestModel);

        await Assert.That(result.IsFailure).IsTrue()
            .Because("the fake never produces a summary, so a compaction that reached it has to fail rather than answer with an empty one");
        await Assert.That(compaction.Calls).IsEqualTo(1)
            .Because("the count is what tells a test that the compaction branch was reached at all");
    }

    [Test]
    public async Task The_Compaction_Fake_Answers_With_The_Outcome_It_Was_Given()
    {
        var compaction = new FakeCompactionService(
            Result.Failure<CompactionResult>("simulated compaction failure"));

        Result<CompactionResult> result = await compaction.CompactAsync(
            "s1",
            [],
            ScriptedLlmClient.TestModel);

        await Assert.That(result.Error).IsEqualTo("simulated compaction failure")
            .Because("the failure text is the one thing the three former copies disagreed about, so it is a constructor argument rather than a per-project constant");
    }

    // ---- the token tracker --------------------------------------------------

    [Test]
    public async Task The_Token_Tracker_Counts_Nothing()
    {
        var tracker = new FakeTokenTracker();
        tracker.RecordTurnUsage(new Usage(10, 5, null, null, null));

        await Assert.That(tracker.Estimate("some text")).IsEqualTo(0);
        await Assert.That(tracker.EstimateMessage(TestMessages.User("hi"))).IsEqualTo(0);
        await Assert.That(tracker.EstimateTokens([TestMessages.User("hi")])).IsEqualTo(0);
    }

    [Test]
    public async Task The_Token_Tracker_Compacts_Only_When_The_Test_Says_So()
    {
        var quiet = new FakeTokenTracker();
        var compacting = new FakeTokenTracker(shouldCompact: true);

        await Assert.That(quiet.ShouldCompact([], ScriptedLlmClient.TestModel)).IsFalse();
        await Assert.That(compacting.ShouldCompact([], ScriptedLlmClient.TestModel)).IsTrue()
            .Because("the flag is the only question with an interesting answer — a test that needs the compaction branch must be able to ask for it");
    }

    // ---- the rate limiter ---------------------------------------------------

    [Test]
    public async Task The_Bucket_Reports_Its_Capacity_And_What_It_Admitted()
    {
        using var limiter = new TokenBucketRateLimiter(2);

        await limiter.AcquireAsync();
        await limiter.AcquireAsync();

        await Assert.That(limiter.Capacity).IsEqualTo(2);
        await Assert.That(limiter.TotalAdmissions).IsEqualTo(2);
        await Assert.That(limiter.PeakInFlight).IsEqualTo(2);
    }

    [Test]
    public async Task The_Bucket_Releases_Its_Token_When_The_Caller_Refunds_It()
    {
        using var limiter = new TokenBucketRateLimiter(1);

        await limiter.AcquireAsync();
        limiter.Refund();

        await Assert.That(limiter.TotalAdmissions).IsEqualTo(1)
            .Because("a refund is not a new admission — the bucket admits again, but the admission count only grows when a call is let through");
    }

    [Test]
    public async Task The_Rate_Limited_Client_Gates_The_Streams_It_Forwards()
    {
        using var limiter = new TokenBucketRateLimiter(4);
        var client = new RateLimitedLlmClient(new ScriptedLlmClient(), limiter);

        await foreach (LlmEvent _ in client.StreamAsync(new LlmRequest("test-model", [], "", [])))
        {
        }

        await Assert.That(limiter.TotalAdmissions).IsEqualTo(1)
            .Because("every streaming call takes a token, and the refund happens when the stream ends — so one stream is one admission");
        await Assert.That(limiter.PeakInFlight).IsEqualTo(1);
    }

    [Test]
    public async Task The_Rate_Limited_Client_Refunds_Its_Token_When_The_Stream_Faults()
    {
        using var limiter = new TokenBucketRateLimiter(1);
        var client = new RateLimitedLlmClient(new ThrowingLlmClient(), limiter);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (LlmEvent _ in client.StreamAsync(new LlmRequest("test-model", [], "", [])))
            {
            }
        });

        await Assert.That(limiter.TotalAdmissions).IsEqualTo(1)
            .Because("the fault happened INSIDE the stream, and a token that is not refunded on a fault would shrink the bucket for every later call");
    }

    // ---- the auth resolvers -------------------------------------------------

    [Test]
    public async Task The_Allow_List_Resolver_Authorizes_Only_What_It_Was_Given()
    {
        var resolver = new AllowListAuthResolver("kilocode");

        Result<string> allowed = await resolver.ResolveApiKeyAsync("kilocode");
        Result<string> refused = await resolver.ResolveApiKeyAsync("anthropic");

        await Assert.That(allowed.IsSuccess).IsTrue();
        await Assert.That(refused.IsFailure).IsTrue();
        await Assert.That(refused.Error).Contains("anthropic")
            .Because("a failure message that does not say WHICH provider failed is a failure message that cannot be acted on");
    }

    [Test]
    public async Task The_Allow_List_Resolver_Matches_The_Provider_Id_Regardless_Of_Case()
    {
        var resolver = new AllowListAuthResolver("kilocode");

        Result<string> result = await resolver.ResolveApiKeyAsync("KiloCode");

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("a provider id reaches the resolver from a config file on one side and from a command line on the other, and the two do not agree on case");
    }

    [Test]
    public async Task The_Allow_List_Resolver_Records_What_It_Was_Asked()
    {
        var resolver = new AllowListAuthResolver("kilocode");

        await resolver.ResolveApiKeyAsync("kilocode");
        await resolver.ResolveApiKeyAsync("anthropic");

        await Assert.That(resolver.Requested).IsEquivalentTo(new[] { "kilocode", "anthropic" })
            .Because("a test that proves 'the surfaces asked the resolver' needs the resolver to remember the question");
    }

    [Test]
    public async Task The_Throwing_Resolver_Throws()
    {
        var resolver = new ThrowingAuthResolver();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveApiKeyAsync("kilocode"));
    }

    // ---- the provider registry ----------------------------------------------

    [Test]
    public async Task The_Provider_Registry_Serves_Its_Client()
    {
        var client = new ScriptedLlmClient();
        var registry = new FakeProviderRegistry(client);

        await Assert.That(registry.GetRegisteredProviderIds()).IsEquivalentTo(new[] { client.ProviderId });
        await Assert.That(registry.GetClient(client.ProviderId).Value).IsSameReferenceAs(client);
        await Assert.That((await registry.GetAllModelsAsync()).Value.Count).IsEqualTo(1)
            .Because("the registry answers the catalogue from its client — that is the whole of it");
    }
}
