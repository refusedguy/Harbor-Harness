using CSharpFunctionalExtensions;
using Harbor.Abstractions.Results;
using TUnit.Assertions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Bible §4.5: <see cref="ResultErrors.Message" /> + built-in
///     <c>Result.Try</c> are THE canonical try/catch→Result pair
///     (the duplicate <c>ResultGuard</c> class was deleted). Cancellation
///     must rethrow so Esc/timeout semantics stay intact.
/// </summary>
public class ResultErrorsTests
{
    [Test]
    public async Task Message_RegularException_ReturnsMessage()
    {
        var error = ResultErrors.Message(new InvalidOperationException("boom"));

        await Assert.That(error).IsEqualTo("boom");
    }

    [Test]
    public async Task Message_OperationCancelled_RethrowsInsteadOfMessage()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Task.Run(() => ResultErrors.Message(new OperationCanceledException())));
    }

    [Test]
    public async Task Try_Success_WrapsValue()
    {
        Result<int> result = Result.Try(() => 42, ResultErrors.Message);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(42);
    }

    [Test]
    public async Task Try_Exception_BecomesFailureWithMessage()
    {
        static int Boom() => throw new ArgumentException("bad input");

        Result<int> result = Result.Try(Boom, ResultErrors.Message);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("bad input");
    }

    [Test]
    public async Task Try_OperationCancelled_RethrowsInsteadOfFailure()
    {
        static int Cancelled() => throw new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Task.Run(() => Result.Try(Cancelled, ResultErrors.Message)));
    }

    [Test]
    public async Task TryAsync_Success_WrapsValue()
    {
        Result<int> result = await Result.Try(async () => await Task.FromResult(42), ResultErrors.Message);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(42);
    }

    [Test]
    public async Task TryAsync_Exception_BecomesFailureWithMessage()
    {
        static Task<int> Boom() => throw new InvalidOperationException("boom");

        Result<int> result = await Result.Try(Boom, ResultErrors.Message);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("boom");
    }

    [Test]
    public async Task TryAsync_OperationCancelled_RethrowsInsteadOfFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Result.Try<int>(async () =>
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
                return 0;
            }, ResultErrors.Message));
    }
}
