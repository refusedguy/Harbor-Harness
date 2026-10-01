using System.Net.Sockets;

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
///         reset, truncated stream, dropped connection),
///         <see cref="TimeoutException" /> (a client-side deadline passed), and
///         an <see cref="HttpRequestException" /> carrying <b>no</b> status code
///         — a failure below the HTTP layer (DNS, connection refused, reset,
///         TLS handshake). The third is the same physical event as the first two,
///         which is why <see cref="Harbor.Application.Resilience.RetryPolicy" />
///         has always retried it. Splitting them across two owners is what let
///         "is a dropped socket retryable?" have two answers in one app.
///     </para>
///     <para>
///         <b>A bare <see cref="SocketException" /> is the same event too</b>
///         (#925) — the socket layer's own report of a reset, refused or
///         unreachable endpoint, handed back unwrapped by the HTTP handler. It is
///         named here because it is invisible to the two arms above it, not
///         because it is a fifth kind of failure.
///     </para>
///     <para>
///         <b>The status-less arm is what #572 dropped.</b> Each transport used
///         to carry a private copy reading
///         <c>ex is HttpRequestException or IOException or TimeoutException</c>.
///         Hoisting that set here without the first arm left the type predicate
///         as <c>is IOException or TimeoutException</c>, and
///         <see cref="HttpRequestException" /> derives from
///         <see cref="Exception" />, not <see cref="IOException" /> — so the
///         connection failures the copy named were the one shape the owner
///         could not match. The transports then fell through to their terminal
///         arm and reported a dropped connection after one attempt, while the
///         LLM path retried the identical exception. The paragraph above claimed
///         the union; the code below never tested it.
///     </para>
///     <para>
///         <b>#925 found the same gap one layer out, and #831 did not close it.</b>
///         #831 put the wrapped shape back, assuming the handler always wraps. It
///         does not: CI measured 17 bare <see cref="SocketException" />s against 23
///         wrapped <see cref="HttpRequestException" />s over 40 identical dropped
///         connections. <see cref="SocketException" /> is not an
///         <see cref="IOException" />, so the arm sitting beside the new one could
///         not reach it either — and the event stayed unretryable on every run
///         where the BCL returned that type, which is most of them.
///     </para>
///     <para>
///         <b>What is deliberately not here:</b> HTTP status classification
///         (408 / 429 / 5xx) and <c>Retry-After</c> parsing. Those belong to the
///         Application-layer policy, which is the only place that sees a
///         response object rather than a thrown exception. The transports
///         classify their own status codes inline at the call site and hand only
///         the exception-shaped failures here. The
///         <c>StatusCode: null</c> constraint is that boundary stated as a
///         pattern: a status-*bearing* failure is an answer the server gave, and
///         retrying one is the Application layer's call — never this set's.
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
    /// <remarks>
    ///     <para>
    ///         <see cref="HttpRequestException" /> matches only when
    ///         <see cref="HttpRequestException.StatusCode" /> is <see langword="null" />
    ///         — the below-the-HTTP-layer failures that are the same physical event as
    ///         an <see cref="IOException" />, and that
    ///         <c>RetryPolicy.HttpClassifier</c> already answers "transient" for. A
    ///         status-bearing one is a refusal or a server verdict: #714 established
    ///         that retrying a 401 is three chances to get a key flagged, so those
    ///         stay out of this set and are classified where the response is in hand.
    ///     </para>
    ///     <para>
    ///         <b>Why <see cref="SocketException" /> is named separately (#925).</b>
    ///         It does not derive from <see cref="IOException" /> — its base is
    ///         <see cref="System.Runtime.InteropServices.Win32Exception" /> — so the
    ///         <c>is IOException</c> arm cannot match it, and #831's status-less
    ///         <see cref="HttpRequestException" /> arm could not either, because
    ///         that arm requires the exception the <c>HttpClient</c> handler
    ///         <i>chose</i> to hand back. Measured on the CI runner, 40 dropped
    ///         connections driven at <c>HttpClient.SendAsync</c> produced
    ///         <b>17 bare <see cref="SocketException" />s and 23 wrapped ones</b>:
    ///         the same physical event, two types, and the set answered only one of
    ///         them. The MCP transports then reported the drop after a single
    ///         attempt on 7 runs of 10.
    ///         <br />
    ///         Naming it costs nothing on the wrapped path and closes the unwrapped
    ///         one. It cannot smuggle in a server verdict: a status-bearing
    ///         <see cref="HttpRequestException" /> is still excluded by the
    ///         <see langword="null" /> constraint above, and
    ///         <see cref="SocketException" /> carries no status to begin with —
    ///         <c>ProviderErrors.FromException</c> already maps it to
    ///         <c>ProviderErrorKind.Network</c>, the same verdict this set gives
    ///         the <see cref="IOException" /> beside it.
    ///     </para>
    /// </remarks>
    public static bool ShouldRetry(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is IOException or TimeoutException or SocketException
            || error is HttpRequestException { StatusCode: null };
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
