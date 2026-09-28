namespace Harbor.Application.Resilience;

/// <summary>
///     Optional out-of-band side-channel for a server-provided Retry-After
///     hint (#270). <see cref="RetryPolicy" /> honours this before falling
///     back to the computed exponential backoff, clamped to
///     <see cref="RetryOptions.EffectiveMaxRetryAfter" />.
/// </summary>
/// <remarks>
///     No provider implements this yet (none propagates Retry-After today —
///     verified by audit); the policy additionally reads
///     <c>Exception.Data["RetryAfter"]</c> (<c>TimeSpan</c>, seconds as
///     number, or seconds/HTTP-date as string). Either channel needs no
///     contract change when a provider starts surfacing the header.
/// </remarks>
public interface IRetryAfterHint
{
    /// <summary>Server-requested delay before the next attempt (null = no hint).</summary>
    TimeSpan? RetryAfter { get; }
}
