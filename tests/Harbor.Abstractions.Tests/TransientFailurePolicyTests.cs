using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Harbor.Abstractions.Resilience;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     #572: the retry verdict and the retry budget the remote-MCP transports and
///     <c>RetryPolicy</c> now share. These are behaviour tests for the shared
///     owner itself; the rule that nothing may fork it again lives in
///     <c>tests/Harbor.Architecture.Tests/TransportRetryOwnershipRules.cs</c>.
/// </summary>
public class TransientFailurePolicyTests
{
    /// <summary>
    ///     The set the two transports used to state privately and the policy used
    ///     to deny. Pinned by exception TYPE, so a widening or a narrowing has to
    ///     be a deliberate edit to the test rather than a silent drift.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShouldRetry_TransportClassFailures_Retry(bool isTimeout)
    {
        Exception failure = isTimeout ? new TimeoutException("slow") : new IOException("reset by peer");

        await Assert.That(TransientFailurePolicy.ShouldRetry(failure)).IsTrue();
    }

    /// <summary>
    ///     Everything the owner does NOT claim. A dropped socket is worth another
    ///     attempt; a bad argument is not, and neither is a caller cancellation
    ///     — the transports rely on that to keep user cancellation from being
    ///     retried.
    /// </summary>
    [Test]
    public async Task ShouldRetry_ProgrammingAndCancellationFailures_DoNotRetry()
    {
        Exception[] fatal =
        [
            new InvalidOperationException("bad state"),
            new System.Text.Json.JsonException("malformed"),
            new ArgumentException("bad argument"),
            new OperationCanceledException(),
            new TaskCanceledException(),
        ];

        foreach (Exception failure in fatal)
        {
            await Assert.That(TransientFailurePolicy.ShouldRetry(failure)).IsFalse().Because(
                $"{failure.GetType().Name} is not a transport blip — retrying cannot fix it");
        }
    }

    /// <summary>
    ///     #831: the arm #572 dropped when it hoisted the set. The private copies
    ///     the transports carried read
    ///     <c>ex is HttpRequestException or IOException or TimeoutException</c>;
    ///     the owner they were hoisted into matched only the last two. Because
    ///     <see cref="HttpRequestException" /> derives from
    ///     <see cref="Exception" /> and not from <see cref="IOException" />, the
    ///     missing arm was not near-missed — it was a type the pattern could not
    ///     match at all, so a connection that never reached the HTTP layer was
    ///     reported to the user after one attempt instead of three.
    /// </summary>
    [Test]
    public async Task ShouldRetry_StatusLessHttpRequestException_Retries()
    {
        // DNS failure, connection refused, TLS handshake — the shape HttpClient
        // throws with StatusCode left null. No status code means no answer ever
        // came back, so there is nothing to be refused by.
        var failure = new HttpRequestException("connection refused", new SocketException(111));

        await Assert.That(failure.StatusCode).IsNull();
        await Assert.That(TransientFailurePolicy.ShouldRetry(failure)).IsTrue().Because(
            "no response means no verdict — this is the same physical event as the IOException arm, and "
            + "RetryPolicy.IsTransient has always retried it, so declining here is what gave one app two answers");
    }

    /// <summary>
    ///     #925: the arm that was still missing after #831, measured rather than
    ///     guessed. #831 widened the set to the status-less
    ///     <see cref="HttpRequestException" /> because that is the shape a
    ///     <see cref="System.Net.Http.SocketsHttpHandler" /> reports when it wraps
    ///     the socket error. CI then showed the handler does NOT always wrap it:
    ///     driving 40 dropped connections straight at <c>HttpClient.SendAsync</c>
    ///     produced <b>17 bare
    ///     <see cref="System.Net.Sockets.SocketException" />s</b> and 23 wrapped
    ///     ones — and the bare shape is invisible to the set, because
    ///     <see cref="System.Net.Sockets.SocketException" /> derives from
    ///     <see cref="System.Runtime.InteropServices.Win32Exception" />, NOT from
    ///     <see cref="IOException" />.
    ///     <para>
    ///         That is the same defect #831 was filed for, one layer out: the very
    ///         same physical event got two answers depending on which type the
    ///         BCL happened to hand back, and on the runs where it handed back the
    ///         bare one the MCP transport reported the drop after a single attempt.
    ///         Measured on the SSE transport, 7 runs of 10 ended on attempt 1.
    ///     </para>
    ///     <para>
    ///         Pinned by type because that is what was broken: a fix that taught
    ///         the set to match the wrapper alone would pass
    ///         <see cref="ShouldRetry_StatusLessHttpRequestException_Retries" />
    ///         and leave the socket shape unretryable.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(SocketError.ConnectionReset)]
    [Arguments(SocketError.ConnectionAborted)]
    [Arguments(SocketError.ConnectionRefused)]
    [Arguments(SocketError.HostUnreachable)]
    [Arguments(SocketError.NetworkUnreachable)]
    [Arguments(SocketError.TimedOut)]
    [Arguments(SocketError.HostNotFound)]
    public async Task ShouldRetry_BareSocketException_Retries(SocketError socketError)
    {
        var failure = new SocketException((int)socketError);

        await Assert.That(TransientFailurePolicy.ShouldRetry(failure)).IsTrue().Because(
            $"a {socketError} is the same physical event as the IOException arm, arriving unwrapped. "
            + "SocketException derives from Win32Exception, not IOException, so the `is IOException` arm cannot "
            + "match it and a dropped connection went unretryable on exactly the runs where the BCL chose this type");
    }

    /// <summary>
    ///     The constraint that makes the widened arm safe, and it is the whole
    ///     reason this is not simply <c>is HttpRequestException</c>. A status code
    ///     means the server DID answer — 401 is a refusal, 400 is a bad request,
    ///     and #714 established that spending three attempts on either is latency
    ///     in front of a guaranteed error, plus three more chances for a provider
    ///     to flag a key. Only the status-less shape is a blip.
    /// </summary>
    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.BadRequest)]
    public async Task ShouldRetry_StatusBearingHttpRequestException_DoesNotRetry(HttpStatusCode status)
    {
        var failure = new HttpRequestException("refused", null, status);

        await Assert.That(TransientFailurePolicy.ShouldRetry(failure)).IsFalse().Because(
            $"{(int)status} is an answer the server gave. Retrying it cannot change the answer, and this set does not "
            + "own status classification — the transports weigh a status while the response is still in hand (#714), and "
            + "RetryPolicy.HttpClassifier owns the same set for the LLM path");
    }

    /// <summary>
    ///     A null error is a caller bug, not a transient failure. The predicate
    ///     is called from <c>catch</c> filters where the argument can never be
    ///     null, so a null here means a broken seam and must be loud.
    /// </summary>
    [Test]
    public async Task ShouldRetry_Null_Throws()
    {
        await Assert.That(() => TransientFailurePolicy.ShouldRetry(null!))
            .Throws<ArgumentNullException>()
            .Because("a null error is a broken call site, not a retryable failure");
    }

    /// <summary>
    ///     200 ms, 400 ms, 800 ms — the exact sequence both transports slept
    ///     before this was hoisted, and what the existing transport tests observe
    ///     (they assert three handled requests against a three-attempt budget).
    /// </summary>
    [Test]
    [Arguments(1, 200)]
    [Arguments(2, 400)]
    [Arguments(3, 800)]
    [Arguments(4, 1600)]
    public async Task BackoffDelay_Doubles_PerAttempt(int failedAttempt, int expectedMs)
    {
        await Assert.That(TransientFailurePolicy.BackoffDelay(failedAttempt))
            .IsEqualTo(TimeSpan.FromMilliseconds(expectedMs));
    }

    /// <summary>
    ///     The shift is clamped: <c>1 &lt;&lt; 31</c> is zero in <c>int</c>, so an
    ///     unclamped shift would turn a deep backoff into an immediate retry —
    ///     the failure mode a growing attempt budget would introduce.
    /// </summary>
    [Test]
    public async Task BackoffDelay_DeepAttempt_DoesNotWrapAround()
    {
        TimeSpan deep = TransientFailurePolicy.BackoffDelay(40);

        await Assert.That(deep.TotalMilliseconds).IsGreaterThan(TransientFailurePolicy.BackoffDelay(4).TotalMilliseconds).Because(
            "a 40th attempt must wait longer than a 4th, not less — an int shift overflow would make it wait 200 ms");
    }

    /// <summary>Attempt 0 has no failure to follow, so there is no backoff to compute.</summary>
    [Test]
    public async Task BackoffDelay_AttemptBelowOne_Throws()
    {
        await Assert.That(() => TransientFailurePolicy.BackoffDelay(0))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    ///     The first-retry delay is the one the transports used to hard-code.
    ///     Only this half is asserted, and deliberately: it is a
    ///     <c>static readonly</c> field, so the value is read at run time and the
    ///     assertion can fail. <c>DefaultMaxAttempts</c> is a <c>const</c>, so
    ///     asserting it here would be vacuous — a change to the constant would
    ///     recompile this assertion to match (TUnitAssertions0005 says so out
    ///     loud). The attempt cap of three is pinned where it is actually
    ///     observable instead: the transport tests in
    ///     <c>McpRemoteTransportTests</c>, which assert three handled requests
    ///     against the shared budget.
    /// </summary>
    [Test]
    public async Task FirstRetryDelay_IsTwoHundredMilliseconds()
    {
        await Assert.That(TransientFailurePolicy.DefaultFirstRetryDelay.TotalMilliseconds).IsEqualTo(200d);
    }
}
