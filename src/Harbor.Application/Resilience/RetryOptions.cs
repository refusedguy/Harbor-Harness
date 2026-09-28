namespace Harbor.Application.Resilience;

/// <summary>
///     Retry configuration. <see cref="MaxAttempts" /> bounds the general
///     transient budget; rate-limit (HTTP 429) failures draw from a separate,
///     minutes-scale budget (<see cref="EffectiveRateLimitAttempts" />) so a
///     429 storm does not burn the whole run in seconds (#270). Callers that
///     do not opt in get the legacy behaviour: the rate-limit budget defaults
///     to <see cref="MaxAttempts" />.
/// </summary>
/// <param name="MaxAttempts">Total attempts for general transient failures (must be ≥ 1).</param>
/// <param name="BaseDelay">Base backoff delay, scaled exponentially per attempt.</param>
/// <param name="UseJitter">Whether to apply full jitter to computed delays.</param>
/// <param name="MaxRateLimitAttempts">Total attempts for rate-limit failures; defaults to <paramref name="MaxAttempts" /> when null.</param>
/// <param name="RateLimitBaseDelay">Base backoff for rate-limit retries; defaults to <paramref name="BaseDelay" /> when null.</param>
/// <param name="MaxRetryAfter">Clamp for a server-provided Retry-After hint; defaults to 5 minutes when null.</param>
public sealed record RetryOptions(
    int MaxAttempts,
    TimeSpan BaseDelay,
    bool UseJitter,
    int? MaxRateLimitAttempts = null,
    TimeSpan? RateLimitBaseDelay = null,
    TimeSpan? MaxRetryAfter = null)
{
    /// <summary>Total attempts allowed for rate-limit failures (never below 1 in a valid configuration).</summary>
    public int EffectiveRateLimitAttempts => MaxRateLimitAttempts ?? MaxAttempts;

    /// <summary>Base backoff delay for rate-limit retries.</summary>
    public TimeSpan EffectiveRateLimitBaseDelay => RateLimitBaseDelay ?? BaseDelay;

    /// <summary>Upper bound honoured for a server-provided Retry-After hint.</summary>
    public TimeSpan EffectiveMaxRetryAfter => MaxRetryAfter ?? TimeSpan.FromMinutes(5);
}
