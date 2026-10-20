namespace Harbor.Application.Tests;

/// <summary>
///     The two ways a suite waits for a condition to become true.
/// </summary>
/// <remarks>
///     <para>
///         <c>WaitForAsync</c> throws when the condition never holds; <c>WaitUntilAsync</c>
///         reports the outcome and lets the caller decide. They are two helpers,
///         not one with a flag, because the callers want different things from a
///         timeout.
///     </para>
/// </remarks>
internal static class AsyncWaits
{
    internal static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(5).ConfigureAwait(false);
        }

        if (!condition())
        {
            throw new TimeoutException($"Timed out waiting for: {what}");
        }
    }

    /// <summary>
    ///     Bounded poll for a condition a background run establishes asynchronously — the same
    ///     idiom <c>CancelProtocolGenerationTests</c> uses, because a bare <c>Task.Yield()</c> is
    ///     not a synchronisation point.
    /// </summary>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        return condition();
    }
}
