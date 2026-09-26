using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
namespace Harbor.Abstractions.Extensions;
/// <summary>
///     Pooling helpers for reducing allocations in hot paths.
///     All methods are safe (no unsafe code).
/// </summary>
public static class ArrayPoolExtensions
{
    /// <summary>
    ///     Rent an array from the shared ArrayPool of T and return a disposable scope.
    ///     Usage: using var rented = ArrayPool.Shared.RentScoped(size);
    /// </summary>
    /// <typeparam name="T">The array element type.</typeparam>
    /// <param name="pool">The pool to rent from (typically <see cref="ArrayPool{T}.Shared" />).</param>
    /// <param name="minimumLength">The minimum required array length.</param>
    /// <returns>A <see cref="RentedArray{T}" /> that returns the underlying array on dispose.</returns>
    public static RentedArray<T> RentScoped<T>(this ArrayPool<T> pool, int minimumLength) => new(pool, minimumLength);

    /// <summary>
    ///     A rented array that returns to the pool when disposed.
    /// </summary>
    /// <typeparam name="T">The array element type.</typeparam>
    public readonly ref struct RentedArray<T>
    {
        private readonly ArrayPool<T> _pool;

        /// <summary>
        ///     The rented array. Its actual length may be greater than or equal to <see cref="Length" />.
        /// </summary>
        public T[] Array { get; }

        /// <summary>
        ///     A <see cref="Span{T}" /> view of the rented region (length = <see cref="Length" />).
        ///     Sliced to the requested length — never the full oversized
        ///     <see cref="Array" /> (#90: callers must not read/write past the rented region).
        /// </summary>
        public Span<T> Span => Array.AsSpan(0, Length);

        /// <summary>
        ///     A <see cref="ReadOnlySpan{T}" /> view of the rented region (length = <see cref="Length" />).
        ///     Sliced to the requested length — never the full oversized
        ///     <see cref="Array" /> (#90).
        /// </summary>
        public ReadOnlySpan<T> ReadOnlySpan => Array.AsSpan(0, Length);

        /// <summary>
        ///     The requested length. The underlying <see cref="Array" /> may be larger.
        /// </summary>
        public int Length { get; }

        /// <summary>
        ///     Rent an array from the supplied pool.
        /// </summary>
        /// <param name="pool">The pool to rent from.</param>
        /// <param name="minimumLength">The minimum required length.</param>
        public RentedArray(ArrayPool<T> pool, int minimumLength)
        {
            _pool = pool;
            Array = pool.Rent(minimumLength);
            Length = minimumLength;
        }

        /// <summary>
        ///     Return the underlying array to the pool.
        ///     Returned with <c>clearArray: true</c> so the previous caller's
        ///     bytes never linger in a recycled buffer (#90: unlike the
        ///     <c>StringBuilder</c> path below — where <c>Clear()</c> only
        ///     resets <c>Length</c> — arrays can be truly wiped, so we do).
        /// </summary>
        public void Dispose() => _pool.Return(Array, clearArray: true);
    }
}

/// <summary>
///     Thread-safe StringBuilder pool. Reuses StringBuilder instances across hot paths
///     to eliminate per-call allocations.
/// </summary>
/// <remarks>
///     <para>
///         <b>#53 retention policy (accepted-retention):</b> the text data path
///         is treated as potentially sensitive (secrets can arrive via
///         files, tool stdout, or user input — not only via auth config).
///         <c>StringBuilder.Clear()</c> resets <c>Length</c> but is NOT a
///         secure wipe of the underlying char buffer, and this pool makes no
///         secure-erase claims. Reuse of cleared buffers across calls is
///         accepted for this personal-tool harness; every <c>Rent</c> returns
///         a zero-length builder so no stale content is ever observable, but
///         sensitive chars may physically linger in pooled buffers. Callers
///         with wipe requirements must not route material through this pool.
///     </para>
///     <para>
///         <b>Ownership:</b> the rented wrapper must be disposed exactly once
///         by its owner, on the same logical flow that rented it. Do not copy
///         the wrapper (it is a struct — copies share one builder and a
///         double-<c>Dispose</c> would return the same builder twice, letting
///         two renters share one instance) and do not retain
///         <c>Builder</c> past <c>Dispose</c> (use-after-return appends into a
///         builder that may already serve another renter).
///     </para>
/// </remarks>
public static class StringBuilderPool
{
    // Max capacity we are willing to retain; larger builders are dropped to avoid
    // holding large LOH buffers in the pool indefinitely.
    private const int MaxRetainCapacity = 16 * 1024;

    private static readonly ConcurrentBag<StringBuilder> Bag = new();

    /// <summary>
    ///     Rent a StringBuilder from the pool. The returned builder is cleared and ready to use.
    /// </summary>
    /// <param name="capacity">Suggested initial capacity. Defaults to 256 characters.</param>
    /// <returns>A <see cref="PooledStringBuilder" /> that returns the underlying builder to the pool on dispose.</returns>
    public static PooledStringBuilder Rent(int capacity = 256)
    {
        if (Bag.TryTake(out var sb))
        {
            if (sb.Capacity < capacity)
            {
                sb.Capacity = capacity;
            }
            sb.Clear();
            return new PooledStringBuilder(sb);
        }

        return new PooledStringBuilder(new StringBuilder(capacity));
    }

    /// <summary>
    ///     A pooled <see cref="StringBuilder" /> wrapper. Dispose to return the builder to the pool.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#90: deliberately NOT a ref struct.</b> A ref struct would
    ///         make do-not-copy/dispose-once compiler-enforced, but every
    ///         real call site fundamentally needs heap/async-compatible
    ///         storage: <c>StreamingCoalescer</c> keeps rented builders in
    ///         instance fields and a <c>Dictionary</c> tuple (ref structs
    ///         cannot be fields or generic arguments); <c>BashTool</c> holds
    ///         them across <c>await process.WaitForExitAsync</c> /
    ///         <c>DrainAsync</c> and touches them from process output
    ///         callbacks on other threads; <c>CompactionService</c> holds one
    ///         across <c>await foreach</c>; <c>SystemPromptBuilder</c> rents
    ///         inside an async method. Forcing ref struct would mean
    ///         rewriting all of these to raw <c>StringBuilder</c> handling
    ///         with no pooling discipline gain. The ownership contract below
    ///         therefore stays a documented convention (single owner,
    ///         dispose exactly once, never copy, never touch after dispose),
    ///         enforced by review — see <c>BufferContractsTests</c>.
    ///     </para>
    /// </remarks>
    public readonly struct PooledStringBuilder : IDisposable
    {
        /// <summary>
        ///     The underlying <see cref="StringBuilder" />. Mutate this in place.
        /// </summary>
        public StringBuilder Builder { get; }

        /// <summary>
        ///     Construct a wrapper around a pre-existing <see cref="StringBuilder" />.
        /// </summary>
        /// <param name="builder">The builder to wrap.</param>
        public PooledStringBuilder(StringBuilder builder)
        {
            Builder = builder;
        }

        /// <summary>
        ///     Returns the builder's current contents as a string. Does not dispose.
        /// </summary>
        /// <returns>The accumulated string.</returns>
        public override string ToString() => Builder.ToString();

        /// <summary>
        ///     Return the underlying builder to the pool. Builders that grew past
        ///     <c>MaxRetainCapacity</c> are dropped to avoid holding LOH buffers.
        ///     Dispose exactly once; do not touch <see cref="Builder" /> afterwards.
        /// </summary>
        public void Dispose()
        {
            // Defensive: don't return builders that grew too large (would consume LOH).
            if (Builder.Capacity <= MaxRetainCapacity)
            {
                Builder.Clear();
                Bag.Add(Builder);
            }
        }
    }
}
