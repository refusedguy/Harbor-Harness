using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

/// <summary>
///     A compaction service that never compacts.
/// </summary>
/// <remarks>
///     <para>
///         Three hand-written compaction doubles existed across three projects
///         (the Application suite, the CLI suite and the load harness), and all
///         three said the same thing: "there is nothing to compact here". The
///         agent loop asks <see cref="ShouldCompact" /> first, and every one of
///         them answered <see langword="false" />, so the compaction call itself
///         was never reached.
///     </para>
///     <para>
///         The one difference between them was the failure text the compaction
///         call would have returned had it been reached, so that text is a
///         constructor argument here rather than a per-project constant.
///     </para>
/// </remarks>
public sealed class FakeCompactionService : ICompactionService
{
    private readonly Result<CompactionResult> _outcome;
    private int _calls;

    /// <summary>
    ///     Creates a compaction service that never compacts and fails the
    ///     compaction call if it is reached anyway.
    /// </summary>
    public FakeCompactionService()
        : this(Result.Failure<CompactionResult>("FakeCompactionService does not compact."))
    {
    }

    /// <summary>
    ///     Creates a compaction service that never compacts and answers the
    ///     compaction call with the given outcome.
    /// </summary>
    /// <param name="outcome">What <c>CompactAsync</c> answers when it is called.</param>
    public FakeCompactionService(Result<CompactionResult> outcome) => _outcome = outcome;

    /// <summary>How many times the compaction call was reached.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

    /// <inheritdoc />
    public Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_outcome);
    }
}
