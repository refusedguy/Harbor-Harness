using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;

namespace Harbor.TestKit;

/// <summary>
///     Deterministic token-bucket admission control for LLM calls. The bucket
///     starts full with <paramref name="capacity" /> tokens; each in-flight LLM
///     stream holds one token and refunds it on completion.
/// </summary>
/// <remarks>
///     <para>
///         Refill is driven by ACTUAL COMPLETIONS, not wall-clock timers, so the
///         shape of the load (who streams when) is a pure function of the
///         system's own progress, with zero real-time sleeps in the harness.
///     </para>
///     <para>
///         This started as the load harness's own limiter. It is here because
///         the admission-control SHAPE is not load-specific: any test that
///         needs to prove "only N calls may be in flight at once" needs a
///         bucket, and a second bucket would be a second place for the
///         refund-on-completion rule to drift.
///     </para>
/// </remarks>
public sealed class TokenBucketRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _tokens;
    private int _inFlight;
    private int _peakInFlight;
    private int _totalAdmissions;

    /// <summary>Creates a bucket that admits <paramref name="capacity" /> calls at a time.</summary>
    /// <param name="capacity">How many calls may hold a token at once.</param>
    public TokenBucketRateLimiter(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _tokens = new SemaphoreSlim(capacity, capacity);
    }

    /// <summary>How many calls may hold a token at once.</summary>
    public int Capacity { get; }

    /// <summary>Highest number of simultaneously in-flight streams observed.</summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>Total admissions granted so far.</summary>
    public int TotalAdmissions => Volatile.Read(ref _totalAdmissions);

    /// <summary>Blocks asynchronously until a token is available.</summary>
    public async ValueTask AcquireAsync(CancellationToken ct = default)
    {
        await _tokens.WaitAsync(ct).ConfigureAwait(false);
        int now = Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref _totalAdmissions);

        int peak = Volatile.Read(ref _peakInFlight);
        while (now > peak &&
               Interlocked.CompareExchange(ref _peakInFlight, now, peak) != peak)
        {
            peak = Volatile.Read(ref _peakInFlight);
        }
    }

    /// <summary>Refund the token held by a completed (or faulted) stream.</summary>
    public void Refund()
    {
        Interlocked.Decrement(ref _inFlight);
        _tokens.Release();
    }

    /// <inheritdoc />
    public void Dispose() => _tokens.Dispose();
}

/// <summary>
///     <see cref="ILlmClient" /> decorator that gates every streaming call
///     through a <see cref="TokenBucketRateLimiter" /> and records the
///     concurrency the bucket actually admitted (proof the shaping worked).
/// </summary>
public sealed class RateLimitedLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly TokenBucketRateLimiter _limiter;

    /// <summary>Wraps <paramref name="inner" /> in the given bucket.</summary>
    /// <param name="inner">The client whose calls are gated.</param>
    /// <param name="limiter">The bucket every call must take a token from.</param>
    public RateLimitedLlmClient(ILlmClient inner, TokenBucketRateLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
        ProviderId = inner.ProviderId;
    }

    /// <inheritdoc />
    public ProviderId ProviderId { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (LlmEvent evt in _inner.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            _limiter.Refund();
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => _inner.GetModelsAsync(cancellationToken);
}
