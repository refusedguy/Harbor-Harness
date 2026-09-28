using System.Text.RegularExpressions;
namespace Harbor.Abstractions.Permissions;
/// <summary>
///     Compiled-glob cache seam for <see cref="PermissionRule" /> (#195).
///     The default <see cref="BoundedPatternRegexCache" /> replaces the former
///     unbounded process-wide <c>ConcurrentDictionary</c>: same sharing across
///     rulesets, but capped with LRU eviction and replaceable in tests via
///     <see cref="PermissionRule.RegexCacheProvider" />.
/// </summary>
public interface IPatternRegexCache
{
    /// <summary>
    ///     Return the compiled regex for a canonical glob pattern,
    ///     compiling and caching it on miss.
    /// </summary>
    public Regex GetOrAdd(string pattern);

    /// <summary>Number of compiled entries currently held.</summary>
    public int Count { get; }
}

/// <summary>
///     Bounded LRU cache of compiled glob regexes. Thread-safe (single lock;
///     the default ruleset warms to ~30 entries, so steady state is an
///     uncontended hit). Evicts the least-recently-used entry past capacity —
///     eviction only recompiles on next use, never changes match semantics.
/// </summary>
public sealed class BoundedPatternRegexCache : IPatternRegexCache
{
    /// <summary>Default cap: comfortably above the builtin pattern count.</summary>
    public const int DefaultCapacity = 512;

    private readonly int _capacity;
    private readonly Dictionary<string, Regex> _entries;
    private readonly Dictionary<string, LinkedListNode<string>> _nodes;
    private readonly LinkedList<string> _recency = new();
    private readonly object _gate = new();

    public BoundedPatternRegexCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Regex cache capacity must be >= 1.");
        }

        _capacity = capacity;
        _entries = new Dictionary<string, Regex>(capacity, StringComparer.Ordinal);
        _nodes = new Dictionary<string, LinkedListNode<string>>(capacity, StringComparer.Ordinal);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Regex GetOrAdd(string pattern)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(pattern, out var hit))
            {
                Touch(pattern);
                return hit;
            }

            var compiled = CompileGlob(pattern);
            if (_entries.Count >= _capacity)
            {
                var oldest = _recency.First!;
                _recency.RemoveFirst();
                _nodes.Remove(oldest.Value);
                _entries.Remove(oldest.Value);
            }

            _nodes[pattern] = _recency.AddLast(pattern);
            _entries[pattern] = compiled;
            return compiled;
        }
    }

    /// <summary>
    ///     Compile a canonical glob (<c>*</c> = any sequence, <c>?</c> = one
    ///     char) into a case-insensitive regex with a 5s match timeout.
    /// </summary>
    internal static Regex CompileGlob(string pattern)
    {
        string regexPattern = "^" +
                              Regex.Escape(pattern)
                                  .Replace("\\*", ".*")
                                  .Replace("\\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    }

    private void Touch(string pattern)
    {
        var node = _nodes[pattern];
        _recency.Remove(node);
        _recency.AddLast(node);
    }
}
