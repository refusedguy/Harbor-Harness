using Harbor.Abstractions.Events;
using TUnit.Assertions;

namespace Harbor.Providers.Tests;

/// <summary>
///     ROP-A ПР.5 — the shared transport-error classifier every ILlmClient
///     uses to stamp <see cref="ErrorEvent.Kind" />. These tests pin the
///     wire-condition → kind mapping that the retry policy depends on.
/// </summary>
public class ProviderErrorClassificationTests
{
    [Test]
    public async Task FromStatus_MapsRateLimitAuthAndServerErrors()
    {
        await Assert.That(ProviderErrors.FromStatus(System.Net.HttpStatusCode.TooManyRequests))
            .IsEqualTo(ProviderErrorKind.RateLimit);
        await Assert.That(ProviderErrors.FromStatus(System.Net.HttpStatusCode.Unauthorized))
            .IsEqualTo(ProviderErrorKind.Auth);
        await Assert.That(ProviderErrors.FromStatus(System.Net.HttpStatusCode.Forbidden))
            .IsEqualTo(ProviderErrorKind.Auth);
        await Assert.That(ProviderErrors.FromStatus(System.Net.HttpStatusCode.InternalServerError))
            .IsEqualTo(ProviderErrorKind.ServerError);
        await Assert.That(ProviderErrors.FromStatus(System.Net.HttpStatusCode.BadGateway))
            .IsEqualTo(ProviderErrorKind.ServerError);
        await Assert.That(ProviderErrors.FromStatus(System.Net.HttpStatusCode.BadRequest))
            .IsEqualTo(ProviderErrorKind.Unknown);
    }

    [Test]
    public async Task FromException_TimeoutWithoutCallerCancellation()
    {
        var timeout = new TaskCanceledException(); // no caller token cancelled

        await Assert.That(ProviderErrors.FromException(timeout, CancellationToken.None))
            .IsEqualTo(ProviderErrorKind.Timeout);
    }

    [Test]
    public async Task FromException_CallerCancellationIsCancelledNotTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var oce = new OperationCanceledException(cts.Token);

        await Assert.That(ProviderErrors.FromException(oce, cts.Token))
            .IsEqualTo(ProviderErrorKind.Cancelled);
    }

    [Test]
    public async Task FromException_HttpAndIoFailuresAreNetwork()
    {
        await Assert.That(ProviderErrors.FromException(new HttpRequestException("reset"), CancellationToken.None))
            .IsEqualTo(ProviderErrorKind.Network);
        await Assert.That(ProviderErrors.FromException(new IOException("stream broken"), CancellationToken.None))
            .IsEqualTo(ProviderErrorKind.Network);
        await Assert.That(ProviderErrors.FromException(new InvalidOperationException(), CancellationToken.None))
            .IsEqualTo(ProviderErrorKind.Unknown);
    }

    [Test]
    public async Task IsTransient_VerdictMatchesRetryPolicyExpectations()
    {
        await Assert.That(ProviderErrors.IsTransient(ProviderErrorKind.RateLimit)).IsTrue();
        await Assert.That(ProviderErrors.IsTransient(ProviderErrorKind.ServerError)).IsTrue();
        await Assert.That(ProviderErrors.IsTransient(ProviderErrorKind.Auth)).IsFalse();
        await Assert.That(ProviderErrors.IsTransient(ProviderErrorKind.Malformed)).IsFalse();
    }

    /// <summary>
    ///     #3819: the extracted fallback must be byte-identical to the inline
    ///     ErrorEvent it replaced — same message, same full exception on the
    ///     diagnostics slot, same FromException classification, and no status
    ///     or retry metadata. Every send-failure site now funnels through this
    ///     one primitive (SsePump default + Ollama + OpenAI-compatible hooks).
    /// </summary>
    [Test]
    public async Task HttpSendFailure_ReproducesTheCanonicalFallbackShape()
    {
        var ex = new IOException("stream broken");
        using var cts = new CancellationTokenSource();

        ErrorEvent error = ProviderErrors.HttpSendFailure(ex, cts.Token);

        await Assert.That(error.Message).IsEqualTo($"HTTP request failed: {ex.Message}");
        await Assert.That(error.Exception).IsEqualTo(ex.ToString());
        await Assert.That(error.Kind).IsEqualTo(ProviderErrors.FromException(ex, cts.Token));
        await Assert.That(error.Kind).IsEqualTo(ProviderErrorKind.Network);
        await Assert.That(error.StatusCode).IsNull();
        await Assert.That(error.RetryAfter).IsNull();
    }
}
