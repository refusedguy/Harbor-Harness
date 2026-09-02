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

    private static CompactionService CreateService(ILlmClient? client = null)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client ?? new SummaryLlmClient("summary"));
        return new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance);
    }

    private static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    private static AssistantMessage Assistant(string text) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    private static AssistantMessage Summary(string text, string? firstKeptId) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model",
        IsSummary: true,
        SummaryFirstKeptId: firstKeptId);

    private static ToolResultMessage ToolResult(string toolName, string output) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new ToolResultEntry("call-1", toolName, output, false) });

    [Test]
    public async Task ShouldCompact_ReturnsFalse_WhenUnderReserve()
    {
        var svc = CreateService();
        var messages = new AgentMessage[] { User("hi") };

        bool result = svc.ShouldCompact(messages, HugeModel);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldCompact_ReturnsTrue_WhenOverReserve()
    {
        var svc = CreateService();
        // Build a long enough message that the chars/4 heuristic blows past
        // SmallModel.ContextWindow - ReserveTokens (4096 - 16384 < 0, so always true
        // for any content). The reserve is larger than the context window, meaning
        // the threshold (context - reserve) is negative and any non-empty message
        // triggers compaction.
        var messages = new AgentMessage[] { User("anything at all") };

        bool result = svc.ShouldCompact(messages, SmallModel);

        await Assert.That(result).IsTrue();
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
    public async Task ShouldCompact_RespectsReserveTokensProperty()
    {
        var svc = CreateService();
        // Push the reserve so high that even a tiny message technically overflows
        // the (negative) threshold for HugeModel.
        svc.ReserveTokens = int.MaxValue;
        var messages = new AgentMessage[] { User("hi") };

        bool result = svc.ShouldCompact(messages, HugeModel);

        await Assert.That(result).IsTrue();
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
            User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), // 0
            Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), // 1
            User("cccccccccccccccccccccccccccccccccccccccc"), // 2
            Assistant("dddddddddddddddddddddddddddddddddddd"), // 3
            User("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), // 4
            Assistant("ffffffffffffffffffffffffffffffffffff") // 5
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
            User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            // Tool call + result pair — must not be split.
            AssistantWithToolCall("call-1", "read"),
            ToolResult("read", "cccccccccccccccccccccccccccccccccccc"),
            User("dddddddddddddddddddddddddddddddddddd"),
            Assistant("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")
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
            messages.Add(User($"msg-{i}-xxxxxxxxxxxxxxxx"));

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
            User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            User("cccccccccccccccccccccccccccccccccccccccc"),
            Assistant("dddddddddddddddddddddddddddddddddddd")
        };

        var result = await svc.CompactAsync("session-1", messages, HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Summary).IsEqualTo("my custom summary");

        var summaryPart = ((AssistantMessage)result.Value.SummaryMessage).Parts.OfType<TextPart>().Single();
        await Assert.That(summaryPart.Text).IsEqualTo("my custom summary");
    }

    [Test]
    public async Task Materialize_NoSummary_ReturnsInputUnchanged()
    {
        var messages = new AgentMessage[] { User("hi"), Assistant("ho") };

        var view = CompactionService.MaterializeCompactedView(messages);

        await Assert.That(ReferenceEquals(view, messages)).IsTrue();
    }

    [Test]
    public async Task Materialize_AnchorResolved_SummaryFirstThenTailFromAnchor()
    {
        // Mirrors the post-append layout: [head…, tail…, summary] — the tail
        // sits BEFORE the appended summary, and the view reorders it behind.
        var u0 = User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var u2 = Assistant("cccccccccccccccccccccccccccccccccccc");
        var summary = Summary("folded head", u1.Id);
        var raw = new AgentMessage[] { u0, u1, u2, summary };

        var view = CompactionService.MaterializeCompactedView(raw);

        await Assert.That(view.Count).IsEqualTo(3);
        await Assert.That(view[0].Id).IsEqualTo(summary.Id);
        await Assert.That(view[1].Id).IsEqualTo(u1.Id);
        await Assert.That(view[2].Id).IsEqualTo(u2.Id);
    }

    [Test]
    public async Task Materialize_NullAnchor_KeepsOnlySummaryAndLaterMessages()
    {
        var u0 = User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var summary = Summary("folded everything", null);
        var after = Assistant("appended after compaction");
        var raw = new AgentMessage[] { u0, u1, summary, after };

        var view = CompactionService.MaterializeCompactedView(raw);

        await Assert.That(view.Count).IsEqualTo(2);
        await Assert.That(view[0].Id).IsEqualTo(summary.Id);
        await Assert.That(view[1].Id).IsEqualTo(after.Id);
    }

    [Test]
    public async Task Materialize_UnresolvedAnchor_ReturnsFullHistory()
    {
        var u0 = User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var summary = Summary("dangling anchor", "does-not-exist");
        var raw = new AgentMessage[] { u0, u1, summary };

        var view = CompactionService.MaterializeCompactedView(raw);

        await Assert.That(view.Count).IsEqualTo(raw.Length);
        await Assert.That(ReferenceEquals(view, raw)).IsTrue();
    }

    [Test]
    public async Task Materialize_MessagesAppendedAfterSummary_ArePreservedInOrder()
    {
        var u0 = User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var summary = Summary("folded", u1.Id);
        var a2 = Assistant("turn answer");
        var tr3 = ToolResult("read", "file contents here");
        var raw = new AgentMessage[] { u0, u1, summary, a2, tr3 };

        var view = CompactionService.MaterializeCompactedView(raw);

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
        var created = await store.CreateAsync("/tmp", "code", "test", "test-model");
        await Assert.That(created.IsSuccess).IsTrue();
        string sessionId = created.Value.Id;

        var u0 = User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var u1 = User("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var u2 = Assistant("cccccccccccccccccccccccccccccccccccc");
        var summary = Summary("folded head", u1.Id);
        // Raw layout is append-only: pruned head first, then kept tail,
        // then the summary the loop appends on top.
        var raw = new List<AgentMessage> { u0, u1, u2, summary };
        var beforeView = CompactionService.MaterializeCompactedView(raw);

        foreach (var message in raw)
        {
            var appended = await store.AppendMessageAsync(sessionId, message);
            await Assert.That(appended.IsSuccess).IsTrue();
        }

        var reloaded = await store.GetMessagesAsync(sessionId);
        await Assert.That(reloaded.IsSuccess).IsTrue();
        var afterView = CompactionService.MaterializeCompactedView(reloaded.Value);

        await Assert.That(afterView.Count).IsEqualTo(beforeView.Count);
        await Assert.That(afterView.Count).IsEqualTo(3);
        for (int i = 0; i < beforeView.Count; i++)
        {
            await Assert.That(afterView[i].Id).IsEqualTo(beforeView[i].Id);
        }
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

    /// <summary>
    ///     Mock client that returns the supplied summary text as a single
    ///     <see cref="TextDeltaEvent" /> followed by a <see cref="StepFinishEvent" />.
    /// </summary>
    private sealed class SummaryLlmClient : ILlmClient
    {
        private readonly string _summary;

        public SummaryLlmClient(string summary)
        {
            _summary = summary;
        }

        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new TextDeltaEvent("0", _summary);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return new StepFinishEvent(0, "stop", new Usage(0, _summary.Length / 4));
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { SmallModel }));
    }
}
