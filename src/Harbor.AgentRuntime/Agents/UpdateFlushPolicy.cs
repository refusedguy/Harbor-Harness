namespace Harbor.Application.Agents;

/// <summary>
///     Flush policy for the per-delta update coalescer (#1527).
/// </summary>
/// <remarks>
///     <para>
///         The turn runner used to publish ONE <see cref="MessageUpdateEvent" />
///         per streamed delta — one allocation plus one bus dispatch per token —
///         on a path whose consumers redraw at frame rate. This policy is the
///         explicit statement of how many deltas may share one published
///         update.
///     </para>
///     <para>
///         <b>The no-loss invariant (the reason this is safe).</b> Whatever the
///         policy says, the LAST delta of every run and every terminal event is
///         published: the coalescer flushes its pending batch before any event
///         that follows it, and the turn runner flushes it once more before
///         <see cref="MessageEndEvent" />. A batch is therefore never DROPPED,
///         only merged — the concatenation of the published deltas is
///         byte-for-byte the concatenation of the received ones, in the same
///         order. Consumers that accumulate deltas (every renderer in the tree)
///         see the same text as before; they see it in fewer, larger pieces.
///     </para>
///     <para>
///         <b>What the policy does NOT change.</b> The kind of the published
///         event, its id and its delta text are the provider's own; only the
///         GROUPING changes. A tool-call fragment is never merged into another
///         tool call's fragment (their ids can interleave, and a merged
///         fragment would have to pick one id), and the step-finish event —
///         which carries the usage — is never merged with anything.
///     </para>
/// </remarks>
public sealed record UpdateFlushPolicy
{
    /// <summary>
    ///     The shipped policy: at most 32 deltas (or 4096 characters) per
    ///     published update, and a batch never waits longer than 100 ms for
    ///     the next delta.
    /// </summary>
    /// <remarks>
    ///     The three numbers answer three different questions. The DELTA cap
    ///     bounds the work per publish for a burst; the CHARACTER cap bounds
    ///     the size of one published string when a provider sends huge
    ///     fragments; the INTERVAL bounds how long a delta can sit in the
    ///     buffer, which is what keeps a slow stream (one token every few
    ///     hundred milliseconds) visible token by token instead of in
    ///     batches of 32.
    /// </remarks>
    public static UpdateFlushPolicy Default { get; } = new(
        maxDeltas: 32,
        maxChars: 4096,
        maxInterval: TimeSpan.FromMilliseconds(100));

    /// <summary>
    ///     Publish every delta on its own — the pre-#1527 behaviour, kept as
    ///     the escape hatch for a host that needs one update per token.
    /// </summary>
    public static UpdateFlushPolicy PerDelta { get; } = new(
        maxDeltas: 1,
        maxChars: int.MaxValue,
        maxInterval: null);

    /// <summary>
    ///     Construct a policy.
    /// </summary>
    /// <param name="maxDeltas">
    ///     How many deltas may share one published update. Must be at least 1;
    ///     1 means "one delta per update" (no coalescing).
    /// </param>
    /// <param name="maxChars">
    ///     How many characters may share one published update. Must be at least
    ///     1; <see cref="int.MaxValue" /> means "no character cap".
    /// </param>
    /// <param name="maxInterval">
    ///     How long a batch may wait for the next delta before it is published.
    ///     Null disables the time bound (the batch is then published only when
    ///     the delta/character cap is reached or a boundary event arrives).
    /// </param>
    public UpdateFlushPolicy(int maxDeltas, int maxChars, TimeSpan? maxInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeltas, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        if (maxInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInterval),
                interval,
                "The publish interval must be positive, or null to disable the time bound.");
        }

        MaxDeltas = maxDeltas;
        MaxChars = maxChars;
        MaxInterval = maxInterval;
    }

    /// <summary>How many deltas may share one published update.</summary>
    public int MaxDeltas { get; }

    /// <summary>How many characters may share one published update.</summary>
    public int MaxChars { get; }

    /// <summary>
    ///     How long a batch may wait for the next delta before it is published;
    ///     null disables the time bound.
    /// </summary>
    public TimeSpan? MaxInterval { get; }
}
