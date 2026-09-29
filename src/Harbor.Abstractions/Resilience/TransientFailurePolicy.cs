namespace Harbor.Abstractions.Resilience;

/// <summary>
///     The single answer to "is this failure worth retrying, and how long should
///     the next attempt wait?" for every layer that talks to a socket (#572).
/// </summary>
/// <remarks>
///     <para>
///         This type lives in the Domain facade rather than next to
///         <c>Harbor.Application.Resilience.RetryPolicy</c> for a layering
///         reason, not a stylistic one. <c>Harbor.Tools.Builtin</c> is
///         Infrastructure and may reference only <c>Harbor.Abstractions</c> and
///         <c>Harbor.Extensions</c> — an Infrastructure → Application edge is
///         forbidden by <c>docs/ARCHITECTURE_LAYERS.md</c> §2 and enforced by
///         <c>FullLayerMatrixTests</c>. The remote-MCP transports therefore
///         cannot call <see cref="Harbor.Application.Resilience.RetryPolicy" />
///         at all, which is precisely why they each grew a private copy of its
///         classification and why those copies disagreed with it. Infrastructure
///         owns the verdict; Application and Infrastructure both consume it. See
///         the <c>Harbor.Terminal.Pty</c> row in <c>FullLayerMatrixTests</c> for
///         the same argument already made about a contract Infrastructure owns.
///     </para>
///     <para>
///         <b>What is transient here:</b> <see cref="IOException" /> (socket
///         reset, truncated stream, dropped connection) and
///         <see cref="TimeoutException" /> (a client-side deadline passed). Both
///         are the same physical event as an <see cref="System.Net.Http.HttpRequestException" />
///         with no status code — a failure below the HTTP layer — which
///         <see cref="Harbor.Application.Resilience.RetryPolicy" /> already
///         treated as transient. Splitting them across two owners is what let
///         "is a dropped socket retryable?" have two answers in one app.
///     </para>
///     <para>
///         <b>What is deliberately not here:</b> HTTP status classification
///         (408 / 429 / 5xx) and <c>Retry-After</c> parsing. Those belong to the
///         Application-layer policy, which is the only place that sees a
///         response object rather than a thrown exception. The transports
///         classify their own status codes inline at the call site and hand only
///         the exception-shaped failures here.
///     </para>
/// </remarks>
public static class TransientFailurePolicy
{
    /// <summary>
    ///     Total attempts allowed for a transient transport failure, first try
    ///     included. Bounded on purpose: the legacy SSE transport has no
    ///     idempotency guarantee, so a retried request may reach the server
    ///     twice, and three is the smallest budget that survives a single
    ///     connection blip.
    /// </summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>
    ///     Delay before the retry that follows the first failure. Doubles per
    ///     attempt via <see cref="BackoffDelay" />.
    /// </summary>
    public static readonly TimeSpan DefaultFirstRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     Whether a thrown transport failure is worth another attempt. This is
    ///     the one definition of the set; the remote-MCP transports and
    ///     <c>DefaultToolRetryDecider</c> both call it rather than restating it.
    /// </summary>
    /// <param name="error">The failure a caller caught.</param>
    public static bool ShouldRetry(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is IOException or TimeoutException;
    }

    /// <summary>
    ///     Exponential backoff for the retry that follows the failure of attempt
    ///     <paramref name="failedAttempt" />: <c>DefaultFirstRetryDelay ·
    ///     2^(attempt − 1)</c>, so 200 ms, 400 ms, 800 ms.
    /// </summary>
    /// <remarks>
    ///     The shift is clamped to 30 so a caller with a larger attempt budget
    ///     than <see cref="DefaultMaxAttempts" /> gets a long delay rather than a
    ///     wrapped-around short one — <c>1 &lt;&lt; 31</c> is zero in
    ///     <see langword="int" />, which would have turned a deep backoff into an
    ///     immediate retry.
    /// </remarks>
    public static TimeSpan BackoffDelay(int failedAttempt)
    {
        if (failedAttempt < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failedAttempt),
                failedAttempt,
                "A backoff follows a failure, so the attempt it follows is at least 1.");
        }

        int shift = Math.Min(failedAttempt - 1, 30);
        return DefaultFirstRetryDelay * (1 << shift);
    }
}
