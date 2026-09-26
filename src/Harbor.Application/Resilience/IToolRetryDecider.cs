namespace Harbor.Application.Resilience;

/// <summary>
///     Decides whether a failed tool execution may be retried (#43).
///     Decision only — backoff mechanics (delays, sleep loop) stay in
///     <see cref="RetryPolicy" /> and the dispatcher's retry loop, so
///     retryability policy and backoff computation never mix.
/// </summary>
public interface IToolRetryDecider
{
    /// <summary>Retry/backoff configuration owned by the policy (not the mechanics).</summary>
    RetryOptions Options { get; }

    /// <summary>
    ///     Whether the tool call may be attempted again after
    ///     <paramref name="failedAttempt" /> (1-based) failed with
    ///     <paramref name="error" />. Pure decision: no sleeping, no I/O.
    ///     Cancellation and <see cref="OperationCanceledException" /> never
    ///     reach here — the dispatcher filters those before consulting.
    /// </summary>
    bool ShouldRetry(string toolName, Exception error, int failedAttempt);
}
