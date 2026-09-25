using System.Net.Http;

namespace Harbor.Application.Resilience;

/// <summary>
///     Conservative default tool-retry policy (#43): retry transport-class
///     failures once, never retry anything else.
/// </summary>
/// <remarks>
///     <para>
///         Retryable: <see cref="IOException" />, <see cref="TimeoutException" />,
///         and <see cref="HttpRequestException" /> classified transient by the
///         shared <see cref="RetryPolicy.IsTransient" /> (network-level, 408,
///         429, 5xx). Everything else — validation, auth, logic errors — is
///         fatal: retrying cannot fix it.
///     </para>
///     <para>
///         Known limitation: a timeout MAY have committed side effects inside
///         the tool (e.g. a killed shell left files behind). There is no
///         idempotency signal on <c>ITool</c> yet (full read-only/retryable
///         taxonomy is reviewer-pending, spec §10) — one bounded retry of
///         transport-class failures is the accepted trade-off, and the
///         decider is replaceable per-registry for stricter policies.
///     </para>
/// </remarks>
public sealed class DefaultToolRetryDecider : IToolRetryDecider
{
    /// <inheritdoc />
    public RetryOptions Options { get; } = new(MaxAttempts: 2, BaseDelay: TimeSpan.FromMilliseconds(200), UseJitter: true);

    /// <inheritdoc />
    public bool ShouldRetry(string toolName, Exception error, int failedAttempt)
    {
        if (string.IsNullOrEmpty(toolName) || failedAttempt < 1 || failedAttempt >= Options.MaxAttempts)
        {
            return false;
        }

        return error switch
        {
            IOException => true,
            TimeoutException => true,
            HttpRequestException => RetryPolicy.IsTransient(error, out _),
            _ => false,
        };
    }
}
