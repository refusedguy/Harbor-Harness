using System.Net;
using System.Net.Http;
using Harbor.Application.Resilience;

namespace Harbor.Application.Tests;

/// <summary>
///     #43: retryability matrix. Decision only — no sleeping, no I/O.
/// </summary>
public class ToolRetryDeciderTests
{
    private static readonly DefaultToolRetryDecider Decider = new();

    [Test]
    public async Task IOException_IsRetryable()
    {
        await Assert.That(Decider.ShouldRetry("bash", new IOException("reset"), 1)).IsTrue();
    }

    [Test]
    public async Task TimeoutException_IsRetryable()
    {
        await Assert.That(Decider.ShouldRetry("webfetch", new TimeoutException("slow"), 1)).IsTrue();
    }

    [Test]
    public async Task HttpNoStatus_IsRetryable()
    {
        await Assert.That(Decider.ShouldRetry("webfetch", new HttpRequestException("dns"), 1)).IsTrue();
    }

    [Test]
    public async Task HttpServerError_IsRetryable()
    {
        await Assert.That(Decider.ShouldRetry(
            "webfetch",
            new HttpRequestException("boom", null, HttpStatusCode.InternalServerError), 1)).IsTrue();
    }

    [Test]
    public async Task HttpRateLimited_IsRetryable()
    {
        await Assert.That(Decider.ShouldRetry(
            "webfetch",
            new HttpRequestException("slow down", null, (HttpStatusCode)429), 1)).IsTrue();
    }

    [Test]
    public async Task HttpClientError_IsFatal()
    {
        await Assert.That(Decider.ShouldRetry(
            "webfetch",
            new HttpRequestException("bad", null, HttpStatusCode.BadRequest), 1)).IsFalse();
        await Assert.That(Decider.ShouldRetry(
            "webfetch",
            new HttpRequestException("no", null, HttpStatusCode.Unauthorized), 1)).IsFalse();
    }

    [Test]
    public async Task LogicError_IsFatal()
    {
        await Assert.That(Decider.ShouldRetry("edit", new InvalidOperationException("boom"), 1)).IsFalse();
    }

    [Test]
    public async Task AttemptCap_Exhausted_IsFatal()
    {
        await Assert.That(Decider.ShouldRetry("bash", new IOException("reset"), 2)).IsFalse();
        await Assert.That(Decider.Options.MaxAttempts).IsEqualTo(2);
    }

    [Test]
    public async Task EmptyToolName_IsFatal()
    {
        await Assert.That(Decider.ShouldRetry("", new IOException("reset"), 1)).IsFalse();
    }
}
