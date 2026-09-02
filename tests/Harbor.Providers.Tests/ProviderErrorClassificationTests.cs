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
}
