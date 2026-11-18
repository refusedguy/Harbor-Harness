namespace Harbor.Application.Resilience;

/// <summary>
///     Optional out-of-band side-channel for a server-provided Retry-After
///     hint (#270). <see cref="RetryPolicy" /> honours this before falling
///     back to the computed exponential backoff, clamped to
///     <see cref="RetryOptions.EffectiveMaxRetryAfter" />.
/// </summary>
/// <remarks>
///     No provider exception implements this interface today: a provider
///     failure reaches the policy as an <see cref="Harbor.Abstractions.Events.LlmStreamErrorException" />
///     raised from a terminal <see cref="Harbor.Abstractions.Events.ErrorEvent" />,
///     whose <c>RetryAfter</c> metadata the retry decorator copies into
///     <c>Exception.Data["RetryAfter"]</c> (#2615). The policy reads both
///     channels, so either shape needs no contract change here.
/// </remarks>
public interface IRetryAfterHint
{
    /// <summary>Server-requested delay before the next attempt (null = no hint).</summary>
    TimeSpan? RetryAfter { get; }
}
