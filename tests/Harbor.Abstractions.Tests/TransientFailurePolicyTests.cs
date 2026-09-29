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
