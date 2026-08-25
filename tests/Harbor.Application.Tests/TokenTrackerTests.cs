using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     A5 (sprint 5): direct coverage for the TokenTracker B3 running-estimate
///     cache — the incremental fast path, the desync/resync fallback, the
///     compaction threshold boundary, and usage-stat accumulation.
/// </summary>
public class TokenTrackerTests
{
    private static ModelInfo Model(int contextWindow) => new(
        "test-model", "test", "Test Model", contextWindow, 4_096,
        false, false, true, Pricing.Unknown, "openai");

    /// <summary>A message with a deterministic text payload (estimator is text-driven).</summary>
    private static UserMessage Msg(string content) => new(
        Guid.NewGuid().ToString("N"),
        "s1",
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    [Test]
    public async Task RecordAppendedMessages_FastPath_MatchesFullEstimate()
    {
        var tracker = new TokenTracker();
        var history = new List<AgentMessage> { Msg(new string('a', 100)), Msg(new string('b', 100)) };
        foreach (var m in history)
        {
            tracker.RecordAppendedMessage(m);
        }

        // Fast path must equal a full scan of the same prefix.
        await Assert.That(tracker.EstimateTokens(history)).IsEqualTo(
            history.Sum(tracker.EstimateMessage));
        await Assert.That(tracker.ShouldCompact(history, Model(1_000_000))).IsFalse();
    }

    [Test]
    public async Task ShouldCompact_ResyncsOnce_AfterExternalAppend()
    {
        var tracker = new TokenTracker();
        var history = new List<AgentMessage> { Msg(new string('a', 100)) };
        tracker.RecordAppendedMessage(history[0]);

        // External append (e.g. steering injection done behind the tracker's
        // back): the cache now covers fewer messages than the history holds.
        history.Add(Msg(new string('b', 100)));

        // First call after desync must reflect BOTH messages (full rescan),
        // not just the cached single-message estimate.
        int fullEstimate = tracker.EstimateTokens(history);
        bool compacted = tracker.ShouldCompact(history, Model(fullEstimate + 16384));

        // Threshold: estimated > window - reserve → window == estimate+reserve
        // makes estimated == window - reserve → NOT above → false.
        await Assert.That(compacted).IsFalse();
        await Assert.That(tracker.ShouldCompact(history, Model(fullEstimate + 16383))).IsTrue();

        // Resync happened: subsequent calls keep agreeing without drift.
        await Assert.That(tracker.ShouldCompact(history, Model(fullEstimate + 16384))).IsFalse();
    }

    [Test]
    public async Task ShouldCompact_ResyncsDownward_AfterCompactionPrune()
    {
        var tracker = new TokenTracker();
        var bigHistory = new List<AgentMessage>();
        for (int i = 0; i < 50; i++)
        {
            var m = Msg(new string('x', 200));
            bigHistory.Add(m);
            tracker.RecordAppendedMessage(m);
        }

        // Compaction prunes the head externally.
        var pruned = bigHistory.GetRange(45, bigHistory.Count - 45);

        // Old cache says huge; rescan must shrink to the tail's estimate.
        await Assert.That(tracker.ShouldCompact(pruned, Model(1_000_000))).IsFalse();
        await Assert.That(tracker.ShouldCompact(pruned, Model(1_000))).IsTrue();
    }

    [Test]
    public async Task ShouldCompact_EmptyHistory_NeverCompacts()
    {
        var tracker = new TokenTracker();
        var empty = Array.Empty<AgentMessage>();

        // Realistic window (≫ ReserveTokens): zero estimate never crosses.
        await Assert.That(tracker.ShouldCompact(empty, Model(200_000))).IsFalse();

        // Degenerate window BELOW the reserve flips the inequality negative —
        // compaction is legitimately requested for any content, even empty.
        // Pin this documented edge so a formula change is visible.
        await Assert.That(tracker.ShouldCompact(empty, Model(1_024))).IsTrue();
    }

    [Test]
    public async Task RecordTurnUsage_AccumulatesAllComponents()
    {
        var tracker = new TokenTracker();
        tracker.RecordTurnUsage(new Usage(10, 5, 2, 7, 3));
        tracker.RecordTurnUsage(new Usage(1, 1));

        var stats = tracker.GetStats();
        await Assert.That(stats.TotalInputTokens).IsEqualTo(11);
        await Assert.That(stats.TotalOutputTokens).IsEqualTo(6);
        await Assert.That(stats.TotalReasoningTokens).IsEqualTo(2);
        await Assert.That(stats.TotalCacheReadTokens).IsEqualTo(7);
        await Assert.That(stats.TotalCacheWriteTokens).IsEqualTo(3);
    }
}
