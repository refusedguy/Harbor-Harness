using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

/// <summary>
///     A token tracker that estimates nothing and compacts only when the test
///     says so.
/// </summary>
/// <remarks>
///     Two hand-written token trackers existed across two projects, and both
///     counted nothing: the estimates are zero, the running stats are zero, and
///     the only question with an interesting answer is whether compaction
///     should run. That one is a constructor flag, because a test that wants
///     the compaction branch needs to be able to ask for it.
/// </remarks>
public sealed class FakeTokenTracker(bool shouldCompact = false) : ITokenTracker
{
    /// <inheritdoc />
    public void RecordTurnUsage(Usage usage)
    {
    }

    /// <inheritdoc />
    public int Estimate(string text) => 0;

    /// <inheritdoc />
    public int EstimateMessage(AgentMessage message) => 0;

    /// <inheritdoc />
    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => shouldCompact;

    /// <inheritdoc />
    public TokenStats GetStats() => new(0, 0, null, null, null);
}
