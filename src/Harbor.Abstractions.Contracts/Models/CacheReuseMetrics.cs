namespace Harbor.Abstractions.Models;

/// <summary>
///     Prefix-cache reuse figures for one request or an accumulated session
///     (issue #2671, D04).
/// </summary>
/// <remarks>
///     <para>
///         A cached prompt is only a hit when the bytes before the first
///         variable token are identical to the bytes the provider already
///         cached. Both the hit count and the identity of that stable prefix
///         are part of the measurement: a changing prefix explains a falling
///         hit rate better than the rate alone.
///     </para>
///     <para>
///         The value is immutable. Two snapshots combine with
///         <see cref="Combine" />, so per-request readings can be folded into a
///         session total without shared mutable state. If the snapshots
///         disagree about the prefix identity, the combined value reports no
///         stable prefix instead of guessing which one won.
///     </para>
///     <para>
///         Pure BCL value, no serialization or I/O. Publishing it on a stats
///         event is a separate slice.
///     </para>
/// </remarks>
public sealed record CacheReuseMetrics
{
    /// <summary>
    ///     Create a snapshot. All counters default to zero. Negative counters
    ///     are rejected because a cache counter cannot run backwards. A blank
    ///     prefix id is stored as null, since blank text is not an identity.
    /// </summary>
    /// <param name="cachedTokens">Tokens served from the prefix cache.</param>
    /// <param name="missedTokens">Tokens the provider had to compute.</param>
    /// <param name="checkpoints">Cache checkpoints created.</param>
    /// <param name="promptPrefixId">Identity of the stable prompt prefix, if known.</param>
    public CacheReuseMetrics(
        long cachedTokens = 0,
        long missedTokens = 0,
        int checkpoints = 0,
        string? promptPrefixId = null)
    {
        CachedTokens = RequireNonNegative(cachedTokens, nameof(cachedTokens));
        MissedTokens = RequireNonNegative(missedTokens, nameof(missedTokens));
        Checkpoints = RequireNonNegative(checkpoints, nameof(checkpoints));
        PromptPrefixId = string.IsNullOrWhiteSpace(promptPrefixId) ? null : promptPrefixId;
    }

    /// <summary>Tokens served from the prefix cache.</summary>
    public long CachedTokens { get; }

    /// <summary>Tokens the provider had to compute.</summary>
    public long MissedTokens { get; }

    /// <summary>Cache checkpoints created.</summary>
    public int Checkpoints { get; }

    /// <summary>Identity of the stable prompt prefix, or null when unknown.</summary>
    public string? PromptPrefixId { get; }

    /// <summary>All prompt tokens accounted for by this snapshot.</summary>
    public long TotalTokens => CachedTokens + MissedTokens;

    /// <summary>
    ///     Share of prompt tokens served from cache, from 0 to 1. Returns 0
    ///     when no tokens were recorded, which is the "no request yet" state
    ///     rather than a measurement.
    /// </summary>
    public double ReuseRatio => TotalTokens == 0 ? 0d : (double)CachedTokens / TotalTokens;

    /// <summary>True when neither tokens nor checkpoints were recorded.</summary>
    public bool IsEmpty => TotalTokens == 0 && Checkpoints == 0;

    /// <summary>True when a non-blank prefix identity was recorded.</summary>
    public bool HasStablePrefix => PromptPrefixId is not null;

    /// <summary>
    ///     Add another snapshot without mutating either operand. Counters sum.
    ///     The prefix identity survives only when both snapshots report the
    ///     same one, or when only one of them reports an identity.
    /// </summary>
    /// <param name="other">The snapshot to add.</param>
    /// <returns>The summed metrics.</returns>
    public CacheReuseMetrics Combine(CacheReuseMetrics other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new CacheReuseMetrics(
            CachedTokens + other.CachedTokens,
            MissedTokens + other.MissedTokens,
            Checkpoints + other.Checkpoints,
            CombinePrefixId(other.PromptPrefixId));
    }

    private string? CombinePrefixId(string? other)
    {
        if (PromptPrefixId is null)
        {
            return other;
        }

        if (other is null)
        {
            return PromptPrefixId;
        }

        return string.Equals(PromptPrefixId, other, StringComparison.Ordinal)
            ? PromptPrefixId
            : null;
    }

    private static int RequireNonNegative(int value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name);

    private static long RequireNonNegative(long value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name);
}
