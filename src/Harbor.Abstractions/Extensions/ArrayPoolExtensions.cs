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
        /// </summary>
        public Span<T> Span => Array;

        /// <summary>
        ///     A <see cref="ReadOnlySpan{T}" /> view of the rented region.
        /// </summary>
        public ReadOnlySpan<T> ReadOnlySpan => Array;

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
        /// </summary>
        public void Dispose() => _pool.Return(Array);
    }
}

/// <summary>
///     Thread-safe StringBuilder pool. Reuses StringBuilder instances across hot paths
///     to eliminate per-call allocations.
/// </summary>
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
