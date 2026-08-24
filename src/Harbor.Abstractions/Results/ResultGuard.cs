using CSharpFunctionalExtensions;

namespace Harbor.Abstractions.Results;

/// <summary>
///     Canonical try/catch→Result conversion (ROP canon, arch2-application F16):
///     new pipelines signal failures with <see cref="Result{T}" /> instead of
///     hand-rolled catch blocks. Cancellation is NOT swallowed — it propagates
///     so Esc/timeout semantics stay intact.
/// </summary>
public static class ResultGuard
{
    /// <summary>
    ///     Run an async operation, converting any exception other than
    ///     cancellation into <c>Failure(ex.Message)</c>.
    /// </summary>
    public static async Task<Result<T>> TryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await operation(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure<T>(ex.Message);
        }
    }

    /// <summary>Sync variant of <see cref="TryAsync{T}" />.</summary>
    public static Result<T> Try<T>(Func<T> operation)
    {
        try
        {
            return Result.Success(operation());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure<T>(ex.Message);
        }
    }
}
