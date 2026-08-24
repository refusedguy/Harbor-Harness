using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Results;
using TUnit.Assertions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     P.1 (arch2-application F16): the canonical try/catch→Result conversion —
///     one shared vocabulary for new pipelines. Cancellation must rethrow.
/// </summary>
public class ResultExtensionsTests
{
    [Test]
    public async Task TryAsync_Success_WrapsValue()
    {
        Result<int> result = await ResultGuard.TryAsync(_ => Task.FromResult(42));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(42);
    }

    [Test]
    public async Task TryAsync_Exception_BecomesFailureWithMessage()
    {
        Result<int> result = await ResultGuard.TryAsync<int>(_ =>
            throw new InvalidOperationException("boom"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("boom");
    }

    [Test]
    public async Task TryAsync_OperationCancelled_RethrowsInsteadOfFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.That(async () =>
            await ResultGuard.TryAsync(async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }, cts.Token)
        ).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Try_Sync_ExceptionBecomesFailure()
    {
        Result<int> result = ResultGuard.Try<int>(() => throw new ArgumentException("bad input"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("bad input");
    }
}
