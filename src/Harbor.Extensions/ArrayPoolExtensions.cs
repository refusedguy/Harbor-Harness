using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
///         <b>#1239 v2 — size-bucketed.</b> v1 kept a single global
///         <c>ConcurrentBag</c>, so one renter that left a 16 KiB builder in it made
///         every later renter — including a caller that asked for 256 characters —
///         walk away with 16 KiB of <c>char[]</c>. That was the memory-efficiency bug
///         recorded under "Known issues" in <c>PLAN.md</c>, and it is what made
///         "pool vs new" a question that had to be measured rather than assumed.
///         The bag is now an array of bags indexed by <c>ceil(log2(capacity))</c>: a
///         rent is served from the smallest bucket that can satisfy it, and a
///         returned builder is filed under the capacity it actually grew to. Each
///         bucket retains at most <c>MaxRetainedPerBucket</c> builders, so the pool's
///         footprint is bounded by the bucket count rather than by caller behaviour.
///     </para>
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

    // Smallest bucket. Rents below it are served from bucket 0, so the pool never
    // stores a builder smaller than the smallest real renter.
    private const int MinBucketCapacity = 16;

    // One bucket per power of two: 16, 32, 64 ... 16384 characters.
    private const int BucketCount = 11;

    // Per-bucket ceiling. ConcurrentBag.Count is read without a lock, so this is a
    // soft bound — under contention a bucket can briefly hold a few more — but it
    // makes the pool's footprint a function of the bucket count instead of whatever
    // callers happened to leave behind.
    private const int MaxRetainedPerBucket = 16;

    private static readonly ConcurrentBag<StringBuilder>[] Buckets = CreateBuckets();

    /// <summary>
    ///     Rent a StringBuilder from the pool. The returned builder is cleared, ready to use,
    ///     and its <see cref="StringBuilder.Capacity" /> is at least
    ///     <paramref name="capacity" /> characters.
    /// </summary>
    /// <param name="capacity">Requested initial capacity. Defaults to 256 characters.</param>
    /// <returns>A <see cref="PooledStringBuilder" /> that returns the underlying builder to the pool on dispose.</returns>
    /// <remarks>
    ///     The rent is served from the smallest bucket that can satisfy it, so a caller
    ///     that asks for 256 characters never receives the 16 KiB builder a neighbour
    ///     left behind. A rent above <see cref="MaxRetainCapacity" /> is never pooled:
    ///     it allocates a right-sized builder and <see cref="PooledStringBuilder.Dispose" />
    ///     drops it instead of retaining it.
    /// </remarks>
    public static PooledStringBuilder Rent(int capacity = 256)
    {
        int requested = capacity < MinBucketCapacity ? MinBucketCapacity : capacity;

        if (requested > MaxRetainCapacity)
        {
            return new PooledStringBuilder(new StringBuilder(requested));
        }

        int bucket = BucketIndexFor(requested);
        if (Buckets[bucket].TryTake(out var reused))
        {
            // Defensive: the return path files a builder by the capacity it actually
            // has, so this cannot fire. It is here so the "capacity >= requested"
            // contract never depends on that invariant holding.
            if (reused.Capacity < requested)
            {
                reused.Capacity = requested;
            }

            reused.Clear();
            return new PooledStringBuilder(reused);
        }

        return new PooledStringBuilder(new StringBuilder(BucketCapacity(bucket)));
    }

    /// <summary>
    ///     Return a builder to the bucket that matches the capacity it actually grew to.
    ///     Called by <see cref="PooledStringBuilder.Dispose" />.
    /// </summary>
    /// <param name="builder">The builder being returned. Cleared before it is stored.</param>
    internal static void Return(StringBuilder builder)
    {
        // Defensive: don't retain builders that grew too large (would consume LOH).
        if (builder.Capacity > MaxRetainCapacity)
        {
            return;
        }

        ConcurrentBag<StringBuilder> bucket = Buckets[BucketIndexFor(builder.Capacity)];
        if (bucket.Count >= MaxRetainedPerBucket)
        {
            return;
        }

        builder.Clear();
        bucket.Add(builder);
    }

    private static ConcurrentBag<StringBuilder>[] CreateBuckets()
    {
        var buckets = new ConcurrentBag<StringBuilder>[BucketCount];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new ConcurrentBag<StringBuilder>();
        }

        return buckets;
    }

    /// <summary>
    ///     Index of the smallest bucket whose capacity is at least
    ///     <paramref name="capacity" />, clamped to the last (16 KiB) one. Callers that
    ///     ask for more than that are routed to an unpooled builder by <see cref="Rent" />
    ///     and never reach here.
    /// </summary>
    /// <param name="capacity">The capacity to file under.</param>
    /// <returns>The bucket index.</returns>
    private static int BucketIndexFor(int capacity)
    {
        int bucket = 0;
        int size = MinBucketCapacity;
        while (size < capacity && bucket < BucketCount - 1)
        {
            size <<= 1;
            bucket++;
        }

        return bucket;
    }

    private static int BucketCapacity(int bucket) => MinBucketCapacity << bucket;

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
        public void Dispose() => StringBuilderPool.Return(Builder);
    }
}

/// <summary>
///     Thread-safe <see cref="Dictionary{TKey, TValue}" /> pool — the counterpart to
///     <see cref="StringBuilderPool" /> for the per-call dictionaries hot paths build
///     (argument maps, per-turn indexes) and throw away.
/// </summary>
/// <remarks>
///     <para>
///         <b>#1239.</b> Sized the same way <see cref="StringBuilderPool" /> is: one
///         <c>ConcurrentBag</c> per power-of-two entry band (8, 16 … 1024 entries), at
///         most <c>MaxRetainedPerBucket</c> dictionaries per band, and anything that
///         outgrew the largest band dropped on return instead of retained. The band a
///         rent is filed under is the entry count it held when it was returned, so a
///         recycled instance is never smaller than it was when last used.
///     </para>
///     <para>
///         <b>Comparers are not pooled.</b> A dictionary's comparer is fixed at
///         construction, so an instance can only be recycled into a caller that wants
///         the same one. The pool therefore only ever hands back instances built with
///         <see cref="EqualityComparer{T}.Default" />: a rent that names a custom
///         comparer gets a fresh dictionary, and a return whose
///         <see cref="Dictionary{TKey, TValue}.Comparer" /> is not the default is
///         dropped rather than stored — otherwise the next comparer-agnostic renter
///         would be handed a dictionary that silently compares by the wrong rule.
///     </para>
///     <para>
///         <b>Ownership:</b> the same contract as <see cref="PooledStringBuilder" /> —
///         dispose exactly once, do not copy the wrapper (copies share one dictionary
///         and a double <c>Dispose</c> returns it twice), and do not touch
///         <see cref="PooledDictionary{TKey, TValue}.Dictionary" /> after <c>Dispose</c>.
///     </para>
/// </remarks>
public static class DictionaryPool<TKey, TValue>
{
    /// <summary>Entry band a rent below it is served from.</summary>
    private const int MinRetainEntries = 8;

    /// <summary>Highest band retained: 8, 16, 32 … 1024 entries.</summary>
    private const int MaxRetainEntries = 1024;

    /// <summary>Number of entry bands, one per power of two.</summary>
    private const int BucketCount = 8;

    /// <summary>Soft per-band ceiling, read without a lock (same shape as the builder pool's).</summary>
    private const int MaxRetainedPerBucket = 16;

    // One bucket array per closed generic type: the static state belongs to
    // DictionaryPool<TKey, TValue>, so pools for different key/value pairs never mix.
    private static readonly ConcurrentBag<Dictionary<TKey, TValue>>[] Buckets = CreateBuckets();

    /// <summary>
    ///     Rent a cleared, empty dictionary with the default comparer.
    /// </summary>
    /// <param name="capacity">Number of entries the caller intends to hold.</param>
    /// <returns>A <see cref="PooledDictionary{TKey, TValue}" /> that returns the dictionary to the pool on dispose.</returns>
    /// <remarks>
    ///     Deliberately not defaulted: <c>Rent()</c> with no arguments must resolve to
    ///     the comparer overload, whose two optional parameters would otherwise make
    ///     both candidates applicable and the call ambiguous (CS0121).
    /// </remarks>
    public static PooledDictionary<TKey, TValue> Rent(int capacity) => Rent(null, capacity);

    /// <summary>
    ///     Rent a cleared, empty dictionary. A named <paramref name="comparer" /> — or a
    ///     <paramref name="capacity" /> above <see cref="MaxRetainEntries" /> — is served
    ///     with a fresh dictionary that is not pooled on return.
    /// </summary>
    /// <param name="comparer">The comparer the dictionary must use, or <c>null</c> for the default one.</param>
    /// <param name="capacity">Number of entries the caller intends to hold.</param>
    /// <returns>A <see cref="PooledDictionary{TKey, TValue}" /> that returns the dictionary to the pool on dispose.</returns>
    public static PooledDictionary<TKey, TValue> Rent(IEqualityComparer<TKey>? comparer, int capacity = 0)
    {
        int requested = capacity < 0 ? 0 : capacity;

        if (comparer is null && requested <= MaxRetainEntries)
        {
            int bucket = BucketIndexFor(requested);
            if (Buckets[bucket].TryTake(out var reused))
            {
                return new PooledDictionary<TKey, TValue>(reused);
            }

            return new PooledDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(BandEntries(bucket)));
        }

        // Custom comparer, or a size the pool does not retain: allocate, and Return drops it.
        return new PooledDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(requested, comparer));
    }

    /// <summary>
    ///     Return a dictionary to the band that matches its size. Called by
    ///     <see cref="PooledDictionary{TKey, TValue}.Dispose" />.
    /// </summary>
    /// <param name="dictionary">The dictionary being returned. Cleared before it is stored.</param>
    internal static void Return(Dictionary<TKey, TValue> dictionary)
    {
        int entries = dictionary.Count;

        // Outgrew the largest retained band: drop it rather than hold it.
        if (entries > MaxRetainEntries)
        {
            return;
        }

        // A non-default comparer is never recycled — see the class remarks.
        if (!EqualityComparer<IEqualityComparer<TKey>>.Default.Equals(
                dictionary.Comparer,
                EqualityComparer<TKey>.Default))
        {
            return;
        }

        ConcurrentBag<Dictionary<TKey, TValue>> bucket = Buckets[BucketIndexFor(entries)];
        if (bucket.Count >= MaxRetainedPerBucket)
        {
            return;
        }

        dictionary.Clear();
        bucket.Add(dictionary);
    }

    private static ConcurrentBag<Dictionary<TKey, TValue>>[] CreateBuckets()
    {
        var buckets = new ConcurrentBag<Dictionary<TKey, TValue>>[BucketCount];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new ConcurrentBag<Dictionary<TKey, TValue>>();
        }

        return buckets;
    }

    /// <summary>
    ///     Index of the smallest band whose entry capacity is at least
    ///     <paramref name="entries" />, clamped to the last (1024-entry) one.
    /// </summary>
    /// <param name="entries">The entry count to file under.</param>
    /// <returns>The band index.</returns>
    private static int BucketIndexFor(int entries)
    {
        int bucket = 0;
        int size = MinRetainEntries;
        while (size < entries && bucket < BucketCount - 1)
        {
            size <<= 1;
            bucket++;
        }

        return bucket;
    }

    private static int BandEntries(int bucket) => MinRetainEntries << bucket;
}

/// <summary>
///     A pooled <see cref="Dictionary{TKey, TValue}" /> wrapper. Dispose to return the
///     dictionary to the pool.
/// </summary>
/// <remarks>
///     <para>
///         <b>#90: deliberately NOT a ref struct</b>, for the same reason
///         <see cref="StringBuilderPool.PooledStringBuilder" /> is not: real call sites
///         hold pooled dictionaries in instance fields and across <c>await</c>
///         boundaries, which a ref struct cannot do. The ownership contract is
///         therefore a documented convention — dispose exactly once, never copy.
///     </para>
/// </remarks>
public readonly struct PooledDictionary<TKey, TValue> : IDisposable
{
    /// <summary>
    ///     The underlying <see cref="Dictionary{TKey, TValue}" />. Mutate this in place.
    /// </summary>
    public Dictionary<TKey, TValue> Dictionary { get; }

    /// <summary>
    ///     Construct a wrapper around a pre-existing <see cref="Dictionary{TKey, TValue}" />.
    /// </summary>
    /// <param name="dictionary">The dictionary to wrap.</param>
    public PooledDictionary(Dictionary<TKey, TValue> dictionary)
    {
        Dictionary = dictionary;
    }

    /// <summary>
    ///     Return the underlying dictionary to the pool. Dictionaries that outgrew the
    ///     largest retained band, or whose comparer is not the default one, are dropped.
    ///     Dispose exactly once; do not touch <see cref="Dictionary" /> afterwards.
    /// </summary>
    public void Dispose() => DictionaryPool<TKey, TValue>.Return(Dictionary);
}
