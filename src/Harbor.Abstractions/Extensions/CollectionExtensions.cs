using System.Collections.Frozen;

namespace Harbor.Abstractions.Extensions;

/// <summary>
/// Extension methods for working with collections in a performance-oriented way.
/// Uses Frozen collections for read-only scenarios to minimize lookup overhead.
/// </summary>
public static class CollectionExtensions
{
    /// <summary>
    /// Convert any enumerable to a FrozenSet{T} for O(1) lookups.
    /// Frozen sets have lower per-item memory than HashSet and faster lookups.
    /// </summary>
    public static FrozenSet<T> ToFrozenSet<T>(this IEnumerable<T> source, IEqualityComparer<T>? comparer = null)
        where T : notnull
    {
        return source as FrozenSet<T> ?? FrozenSet.ToFrozenSet(source, comparer);
    }

    /// <summary>
    /// Convert any enumerable to a FrozenDictionary{TKey, TValue} for O(1) lookups.
    /// </summary>
    public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(
        this IEnumerable<KeyValuePair<TKey, TValue>> source,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        return FrozenDictionary.ToFrozenDictionary(source, comparer);
    }

    /// <summary>
    /// Convert any enumerable to a FrozenDictionary{TKey, TValue} for O(1) lookups.
    /// </summary>
    public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue, TSource>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<TSource, TValue> valueSelector,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        return FrozenDictionary.ToFrozenDictionary(source, keySelector, valueSelector, comparer);
    }

    /// <summary>
    /// Returns the input typed as IReadOnlyCollection{T}, materializing if needed.
    /// Use this for public APIs that should accept any enumerable but expose Count.
    /// </summary>
    public static IReadOnlyCollection<T> AsReadOnlyCollection<T>(this IEnumerable<T> source)
    {
        return source as IReadOnlyCollection<T> ?? source.ToList();
    }

    /// <summary>
    /// Returns the input typed as IReadOnlyList{T}, materializing if needed.
    /// </summary>
    public static IReadOnlyList<T> AsReadOnlyList<T>(this IEnumerable<T> source)
    {
        return source as IReadOnlyList<T> ?? source.ToList();
    }

    /// <summary>
    /// Add multiple items to a List{T} without intermediate allocations.
    /// </summary>
    public static void AddRange<T>(this List<T> list, ReadOnlySpan<T> items)
    {
        foreach (var item in items)
            list.Add(item);
    }
}
