using System.Runtime.CompilerServices;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Registries.Providers;
using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient; // #1769: generated double

namespace Harbor.Core.Tests;
/// <summary>
///     Tests for <see cref="CompactionService" />: the <see cref="CompactionService.ShouldCompact" />
///     threshold check and the cut-point selection logic exercised via
///     <see cref="CompactionService.CompactAsync" /> (FindCutPoint is private but its result
///     is observable as <see cref="CompactionResult.PrunedMessageCount" />).
/// </summary>
public class CompactionServiceTests
{
    private static readonly ModelInfo SmallModel = new(
        "test-model",
        "test",
        "Test Model",
        4_096,
        1_024,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static readonly ModelInfo HugeModel = new(
        "test-model",
        "test",
        "Test Model",
        1_000_000,
        1_024,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static CompactionService CreateService(ILlmClient? client = null, ITokenTracker? tracker = null)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client ?? new SummaryLlmClient("summary"));
        return new CompactionService(
            tracker ?? new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance);
    }

    private static ToolResultMessage ToolResult(string toolName, string output) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new ToolResultEntry("call-1", toolName, output, false) });

    [Test]
    public async Task ShouldCompact_ReturnsFalse_WhenUnderReserve()
    {
        var svc = CreateService();
        var messages = new AgentMessage[] { MessageFixtures.User("hi") };

        bool result = svc.ShouldCompact(messages, HugeModel);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldCompact_DelegatesToTracker_DegenerateWindowRefuses()
    {
        // #1106 (double estimate): the verdict is the tracker's, so the #80
        // companion refusal applies here too — SmallModel.ContextWindow (4096)
        // does not exceed the reserve, and the service no longer trips on the
        // negative threshold its own scan used to produce.
        var svc = CreateService();
        var messages = new AgentMessage[] { MessageFixtures.User("anything at all") };

        bool result = svc.ShouldCompact(messages, SmallModel);

        await Assert.That(result).IsFalse();
        await Assert.That(result).IsEqualTo(new TokenTracker().ShouldCompact(messages, SmallModel));
    }

    [Test]
    public async Task ShouldCompact_EmptyMessages_ReturnsFalse()
    {
        var svc = CreateService();
        var messages = Array.Empty<AgentMessage>();

        // HugeModel: ContextWindow (1M) - ReserveTokens (16384) is still positive,
        // so 0 estimated tokens does not trip the compaction threshold.
        bool result = svc.ShouldCompact(messages, HugeModel);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldCompact_MatchesTrackerVerdict()
    {
        // #1106 (double estimate): single source of truth for the decision —
        // the service verdict equals a fresh tracker's on both sides of the
        // threshold, so a turn that already asked the tracker pays no second scan.
        var svc = CreateService();
        var big = new List<AgentMessage>();
        for (int i = 0; i < 20; i++)
            big.Add(MessageFixtures.User(new string('x', 4000)));
        var small = new AgentMessage[] { MessageFixtures.User("hi") };
        var tracker = new TokenTracker();

        int bigEstimate = tracker.EstimateTokens(big);
        var tight = new ModelInfo(
            "test-model",
            "test",
            "Test Model",
            bigEstimate + 16_383,
            1_024,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai");

        await Assert.That(svc.ShouldCompact(big, tight)).IsTrue();
        await Assert.That(svc.ShouldCompact(big, tight)).IsEqualTo(tracker.ShouldCompact(big, tight));
        await Assert.That(svc.ShouldCompact(small, HugeModel)).IsFalse();
        await Assert.That(svc.ShouldCompact(small, HugeModel)).IsEqualTo(tracker.ShouldCompact(small, HugeModel));
    }

    [Test]
    public async Task ShouldCompact_FollowsTrackerReserve_SingleSource()
    {
        // #1106 (divergent reserves): the service keeps no second threshold —
        // the knob that moves the verdict is the tracker's reserve alone.
        // 20 × ~1100 tokens = ~22000: over 30000−16384, under 30000−100.
        var big = new List<AgentMessage>();
        for (int i = 0; i < 20; i++)
            big.Add(MessageFixtures.User(new string('x', 4000)));
        var window = new ModelInfo(
            "test-model",
            "test",
            "Test Model",
            30_000,
            1_024,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai");

        var defaultSvc = CreateService();
        var tightTracker = new TokenTracker { ReserveTokens = 100 };
        var tightSvc = CreateService(tracker: tightTracker);

        await Assert.That(defaultSvc.ShouldCompact(big, window)).IsTrue();
        await Assert.That(tightSvc.ShouldCompact(big, window)).IsFalse();
        await Assert.That(tightSvc.ShouldCompact(big, window)).IsEqualTo(tightTracker.ShouldCompact(big, window));
    }

    [Test]
    public async Task CompactAsync_PrunesHead_KeepsTail()
    {
        var svc = CreateService();
        // 6 messages, each ~110 tokens (40 chars / 4 + 100 overhead). With
        // KeepRecentTokens=500 we keep ~4 messages in the tail and prune the
        // first 2 into the summary.
        svc.KeepRecentTokens = 500;
        svc.TailTurns = 0; // disable the tail_turns floor for a deterministic cut

        var messages = new List<AgentMessage>
        {
            MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), // 0
            MessageFixtures.Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), // 1
            MessageFixtures.User("cccccccccccccccccccccccccccccccccccccccc"), // 2
            MessageFixtures.Assistant("dddddddddddddddddddddddddddddddddddd"), // 3
            MessageFixtures.User("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), // 4
            MessageFixtures.Assistant("ffffffffffffffffffffffffffffffffffff") // 5
        };

        var result = await svc.CompactAsync("session-1", messages, HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.PrunedMessageCount).IsGreaterThan(0);
        await Assert.That(result.Value.PrunedMessageCount).IsLessThan(messages.Count);
        await Assert.That(string.IsNullOrEmpty(result.Value.Summary)).IsFalse();
        await Assert.That(result.Value.SummaryMessage).IsNotNull();
        var summaryAssistant = (AssistantMessage)result.Value.SummaryMessage;
        await Assert.That(summaryAssistant.IsSummary).IsTrue();
    }

    [Test]
    public async Task CompactAsync_EmptyMessages_ReturnsFailure()
    {
        var svc = CreateService();
        var result = await svc.CompactAsync("session-1", Array.Empty<AgentMessage>(), HugeModel);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("No messages");
    }

    [Test]
    public async Task CompactAsync_DoesNotCutInsideToolCallResultPair()
    {
        // When the candidate cut lands on a ToolResultMessage, FindCutPoint must
        // skip past it (continue) so the head keeps the matching assistant tool call.
        var svc = CreateService();
        svc.KeepRecentTokens = 200;
        svc.TailTurns = 0;

        var messages = new List<AgentMessage>
        {
            MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            MessageFixtures.Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            // Tool call + result pair — must not be split.
            AssistantWithToolCall("call-1", "read"),
            ToolResult("read", "cccccccccccccccccccccccccccccccccccc"),
            MessageFixtures.User("dddddddddddddddddddddddddddddddddddd"),
            MessageFixtures.Assistant("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")
        };

        var result = await svc.CompactAsync("session-1", messages, HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        // PrunedMessageCount == FindCutPoint(messages) — should not equal the index
        // of the ToolResultMessage (3).
        int pruned = result.Value.PrunedMessageCount;
        await Assert.That(pruned).IsGreaterThan(0);
        await Assert.That(pruned).IsNotEqualTo(3);
    }

    [Test]
    public async Task CompactAsync_TailTurnsFloor_Enforced()
    {
        var svc = CreateService();
        svc.KeepRecentTokens = 1; // keep almost nothing
        svc.TailTurns = 2; // but enforce at least 2*4 = 8 tail messages

        var messages = new List<AgentMessage>();
        for (int i = 0; i < 12; i++)
            messages.Add(MessageFixtures.User($"msg-{i}-xxxxxxxxxxxxxxxx"));

        var result = await svc.CompactAsync("session-1", messages, HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        // minTailStart = 12 - 2*4 = 4 → at most 4 messages pruned.
        await Assert.That(result.Value.PrunedMessageCount).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task CompactAsync_SummaryMessage_ContainsSummaryText()
    {
        var svc = CreateService(new SummaryLlmClient("my custom summary"));
        svc.KeepRecentTokens = 60;
        svc.TailTurns = 0;

        var messages = new List<AgentMessage>
        {
            MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            MessageFixtures.Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            MessageFixtures.User("cccccccccccccccccccccccccccccccccccccccc"),
            MessageFixtures.Assistant("dddddddddddddddddddddddddddddddddddd")
        };

        var result = await svc.CompactAsync("session-1", messages, HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Summary).IsEqualTo("my custom summary");

        var summaryPart = ((AssistantMessage)result.Value.SummaryMessage).Parts.OfType<TextPart>().Single();
        await Assert.That(summaryPart.Text).IsEqualTo("my custom summary");
    }

    [Test]
    public async Task CompactAsync_CapsSummaryPromptToSummaryBudget()
    {
        // #1106 row 6: the summary prompt pasted the whole head uncapped, so a
        // head larger than the summary model's window re-overfilled the very
        // window compaction is freeing. The prompt now keeps the newest head
        // messages inside ContextWindow − output − reserve.
        var capturing = new CapturingSummaryClient("capped summary");
        var tracker = new TokenTracker { ReserveTokens = 100 };
        var svc = CreateService(capturing, tracker);
        svc.KeepRecentTokens = 1; // each message is ~200 tokens: tailStart == Count
        svc.TailTurns = 0;

        var messages = new List<AgentMessage>();
        for (int i = 0; i < 10; i++)
            messages.Add(MessageFixtures.User($"captest-{i:D2}-" + new string('x', 400)));

        int headEstimate = new TokenTracker().EstimateTokens(messages);
        // Budget lands mid-head: the full head does not fit, one message does.
        var tight = new ModelInfo(
            "test-model",
            "test",
            "Test Model",
            4096 + tracker.ReserveTokens + headEstimate / 2,
            1_024,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai");

        await Assert.That(headEstimate).IsGreaterThan(tight.ContextWindow - 4096 - tracker.ReserveTokens);

        var result = await svc.CompactAsync("session-1", messages, tight);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.PrunedMessageCount).IsEqualTo(messages.Count);
        string prompt = PromptText(capturing);
        await Assert.That(prompt.Contains("captest-09-")).IsTrue();
        await Assert.That(prompt.Contains("captest-00-")).IsFalse();
        await Assert.That(prompt.Contains("omitted to fit context")).IsTrue();
    }

    [Test]
    public async Task CompactAsync_SummaryPromptKeepsWholeHeadWhenItFits()
    {
        // Companion: a head inside the budget is pasted whole, with no marker.
        var capturing = new CapturingSummaryClient("full summary");
        var svc = CreateService(capturing);
        svc.KeepRecentTokens = 1;
        svc.TailTurns = 0;

        var messages = new List<AgentMessage> { MessageFixtures.User("fit-aa"), MessageFixtures.User("fit-bb") };

        var result = await svc.CompactAsync("session-1", messages, HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        string prompt = PromptText(capturing);
        await Assert.That(prompt.Contains("fit-aa")).IsTrue();
        await Assert.That(prompt.Contains("fit-bb")).IsTrue();
        await Assert.That(prompt.Contains("omitted to fit context")).IsFalse();
    }

    [Test]
    public async Task Materialize_NoSummary_ReturnsInputUnchanged()
    {
        var messages = new AgentMessage[] { MessageFixtures.User("hi"), MessageFixtures.Assistant("ho") };

        var view = CompactionPolicy.MaterializeCompactedView(messages);

        await Assert.That(ReferenceEquals(view, messages)).IsTrue();
    }

    [Test]
    public async Task Materialize_AnchorResolved_SummaryFirstThenTailFromAnchor()
    {
        // Mirrors the post-append layout: [head…, tail…, summary] — the tail
        // sits BEFORE the appended summary, and the view reorders it behind.
        var u0 = MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = MessageFixtures.User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var u2 = MessageFixtures.Assistant("cccccccccccccccccccccccccccccccccccc");
        var summary = MessageFixtures.Summary("folded head", u1.Id);
        var raw = new AgentMessage[] { u0, u1, u2, summary };

        var view = CompactionPolicy.MaterializeCompactedView(raw);

        await Assert.That(view.Count).IsEqualTo(3);
        await Assert.That(view[0].Id).IsEqualTo(summary.Id);
        await Assert.That(view[1].Id).IsEqualTo(u1.Id);
        await Assert.That(view[2].Id).IsEqualTo(u2.Id);
    }

    [Test]
    public async Task Materialize_NullAnchor_KeepsOnlySummaryAndLaterMessages()
    {
        var u0 = MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = MessageFixtures.User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var summary = MessageFixtures.Summary("folded everything", null);
        var after = MessageFixtures.Assistant("appended after compaction");
        var raw = new AgentMessage[] { u0, u1, summary, after };

        var view = CompactionPolicy.MaterializeCompactedView(raw);

        await Assert.That(view.Count).IsEqualTo(2);
        await Assert.That(view[0].Id).IsEqualTo(summary.Id);
        await Assert.That(view[1].Id).IsEqualTo(after.Id);
    }

    [Test]
    public async Task Materialize_UnresolvedAnchor_ReturnsFullHistory()
    {
        var u0 = MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = MessageFixtures.User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var summary = MessageFixtures.Summary("dangling anchor", "does-not-exist");
        var raw = new AgentMessage[] { u0, u1, summary };

        var view = CompactionPolicy.MaterializeCompactedView(raw);

        await Assert.That(view.Count).IsEqualTo(raw.Length);
        await Assert.That(ReferenceEquals(view, raw)).IsTrue();
    }

    [Test]
    public async Task Materialize_MessagesAppendedAfterSummary_ArePreservedInOrder()
    {
        var u0 = MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = MessageFixtures.User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var summary = MessageFixtures.Summary("folded", u1.Id);
        var a2 = MessageFixtures.Assistant("turn answer");
        var tr3 = ToolResult("read", "file contents here");
        var raw = new AgentMessage[] { u0, u1, summary, a2, tr3 };

        var view = CompactionPolicy.MaterializeCompactedView(raw);

        await Assert.That(view.Count).IsEqualTo(4);
        await Assert.That(view[0].Id).IsEqualTo(summary.Id);
        await Assert.That(view[1].Id).IsEqualTo(u1.Id);
        await Assert.That(view[2].Id).IsEqualTo(a2.Id);
        await Assert.That(view[3].Id).IsEqualTo(tr3.Id);
    }

    [Test]
    public async Task Materialize_TruncatedSummarizedHistory_SurvivesStoreSaveReload()
    {
        var store = new MemorySessionStore();
        var created = await store.CreateAsync(Harbor.TestKit.TestTempDirs.NewDirectory("harbor-compaction"), "code", "test", "test-model");
        await Assert.That(created.IsSuccess).IsTrue();
        string sessionId = created.Value.Id;

        var u0 = MessageFixtures.User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = MessageFixtures.User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var u2 = MessageFixtures.Assistant("cccccccccccccccccccccccccccccccccccc");
        var summary = MessageFixtures.Summary("folded head", u1.Id);
        // Raw layout is append-only: pruned head first, then kept tail,
        // then the summary the loop appends on top.
        var raw = new List<AgentMessage> { u0, u1, u2, summary };
        var beforeView = CompactionPolicy.MaterializeCompactedView(raw);

        foreach (var message in raw)
        {
            var appended = await store.AppendMessageAsync(sessionId, message);
            await Assert.That(appended.IsSuccess).IsTrue();
        }

        var reloaded = await store.GetMessagesAsync(sessionId);
        await Assert.That(reloaded.IsSuccess).IsTrue();
        var afterView = CompactionPolicy.MaterializeCompactedView(reloaded.Value);

        await Assert.That(afterView.Count).IsEqualTo(beforeView.Count);
        await Assert.That(afterView.Count).IsEqualTo(3);
        for (int i = 0; i < beforeView.Count; i++)
        {
            await Assert.That(afterView[i].Id).IsEqualTo(beforeView[i].Id);
        }
    }

    /// <summary>#1101: service thresholds refuse non-positive values; TailTurns=0 disables the floor.</summary>
    [Test]
    public async Task Thresholds_DefaultAndValidate()
    {
        var svc = CreateService();
        await Assert.That(svc.KeepRecentTokens).IsEqualTo(20000);
        await Assert.That(svc.TailTurns).IsEqualTo(2);

        svc.TailTurns = 0;
        await Assert.That(svc.TailTurns).IsEqualTo(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => { svc.KeepRecentTokens = 0; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { svc.KeepRecentTokens = -5; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { svc.TailTurns = -1; });
    }

    private static AssistantMessage AssistantWithToolCall(string id, string toolName)
    {
        var args = JsonDocument.Parse("{}").RootElement.Clone();
        return new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            "session-1",
            DateTimeOffset.UtcNow,
            new[] { new ToolCallPart(id, toolName, args) },
            StopReason.ToolUse,
            new Usage(0, 0),
            "test-model");
    }

    private static string PromptText(CapturingSummaryClient client)
    {
        var userMessage = (LlmUserMessage)client.LastRequest!.Messages[0];
        return userMessage.Content.OfType<LlmTextBlock>().Single().Text;
    }

    /// <summary>
    ///     Mock client that records the summarization request so prompt-cap
    ///     tests can assert on the pasted head slice.
    /// </summary>
    private sealed class CapturingSummaryClient : FakeLlmClientBase
    {
        private readonly string _summary;

        public CapturingSummaryClient(string summary)
        {
            _summary = summary;
            ProviderId = ProviderId.Create("test");
            GetModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>([SmallModel]);
        }

        public LlmRequest? LastRequest { get; private set; }

        public override async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            yield return new TextDeltaEvent("0", _summary);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return new StepFinishEvent(0, "stop", new Usage(0, _summary.Length / 4));
        }
    }

    /// <summary>
    ///     Mock client that returns the supplied summary text as a single
    ///     <see cref="TextDeltaEvent" /> followed by a <see cref="StepFinishEvent" />.
    /// </summary>
    private sealed class SummaryLlmClient : FakeLlmClientBase
    {
        private readonly string _summary;

        public SummaryLlmClient(string summary)
        {
            _summary = summary;
            ProviderId = ProviderId.Create("test");
            GetModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>([SmallModel]);
        }

        public override async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new TextDeltaEvent("0", _summary);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return new StepFinishEvent(0, "stop", new Usage(0, _summary.Length / 4));
        }
    }
}
