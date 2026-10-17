using System.Text.RegularExpressions;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Flyweight cache of compiled <see cref="Regex" /> instances (#1585):
///     key is (pattern, options, timeout), entries are shared across callers.
///     <see cref="Regex" /> is an expensive, immutable, thread-safe object —
///     compiling it once per distinct key instead of once per call is the
///     Flyweight pattern. Bounded, LRU-evicting, following the
///     <c>BoundedPatternRegexCache</c> precedent for permission patterns.
/// </summary>
internal sealed class BoundedRegexCache
{
    /// <summary>Default cap: comfortably above the distinct grep patterns a session realistically produces.</summary>
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly Dictionary<Key, Regex> _entries;
    private readonly Dictionary<Key, LinkedListNode<Key>> _nodes;
    private readonly LinkedList<Key> _recency = new();
    private readonly object _gate = new();

    public BoundedRegexCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Regex cache capacity must be >= 1.");
        }

        _capacity = capacity;
        _entries = new Dictionary<Key, Regex>(capacity);
        _nodes = new Dictionary<Key, LinkedListNode<Key>>(capacity);
    }

    /// <summary>Process-wide cache the builtin tools compile their regexes through.</summary>
    public static BoundedRegexCache Shared { get; } = new(DefaultCapacity);

    /// <summary>Number of entries currently cached.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    ///     Return the shared compiled instance for <paramref name="pattern" /> under
    ///     <paramref name="options" />, compiling via <paramref name="factory" /> on a
    ///     miss only. Compilation happens outside the gate, so a slow compile never
    ///     serialises in-flight greps; a rare concurrent miss compiles twice, then
    ///     one instance is discarded.
    /// </summary>
    public Regex GetOrAdd(
        string pattern,
        RegexOptions options,
        TimeSpan matchTimeout,
        Func<string, RegexOptions, TimeSpan, Regex> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var key = new Key(pattern, options, matchTimeout);
        if (TryGet(key) is { } hit)
            return hit;

        var compiled = factory(key.Pattern, key.Options, key.Timeout);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var race))
            {
                Touch(key);
                return race;
            }

            if (_entries.Count >= _capacity)
            {
                var oldest = _recency.First!;
                _recency.RemoveFirst();
                _nodes.Remove(oldest.Value);
                _entries.Remove(oldest.Value);
            }

            _nodes[key] = _recency.AddLast(key);
            _entries[key] = compiled;
            return compiled;
        }
    }

    private Regex? TryGet(Key key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                Touch(key);
                return hit;
            }

            return null;
        }
    }

    private void Touch(Key key)
    {
        var node = _nodes[key];
        _recency.Remove(node);
        _recency.AddLast(node);
    }

    /// <summary>
    ///     Cache key. The timeout is part of the key on purpose: the same regex
    ///     text under different <see cref="Regex" /> match timeouts is a different
    ///     contract, and a caller must not inherit another caller's bound.
    /// </summary>
    private readonly record struct Key(string Pattern, RegexOptions Options, TimeSpan Timeout);
}
