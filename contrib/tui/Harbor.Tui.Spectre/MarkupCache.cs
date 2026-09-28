using Spectre.Console;

namespace Harbor.Tui.Spectre;

/// <summary>
/// Parse cache for Spectre markup (ENG5, issue #276): <c>new Markup(text)</c>
/// re-tokenizes tags on every write while the same strings repeat heavily
/// (style wrappers, status rhythms, constant banners). A tiny direct-mapped
/// table (128 slots, hash-indexed, collision = evict, never grows) memoizes
/// the parsed renderable. Hits allocate nothing; misses parse once.
/// <para/>
/// Sharing is safe: <see cref="Markup"/> rendering is a pure function of its
/// parts plus live console state (wrapping re-resolves per render).
/// Callers must not mutate the returned instance.
/// </summary>
public static class MarkupCache
{
    private const int Capacity = 128;

    private static readonly Entry[] _entries = new Entry[Capacity];
    private static readonly object _lock = new();

    private sealed class Entry
    {
        public string? Source;
        public Markup? Parsed;
    }

    /// <summary>Returns the cached parse of <paramref name="markup"/>, parsing once on miss.</summary>
    public static Markup GetOrParse(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        int index = (markup.GetHashCode() & int.MaxValue) & (Capacity - 1);
        lock (_lock)
        {
            var entry = _entries[index];
            if (entry is not null && (ReferenceEquals(entry.Source, markup) || entry.Source == markup))
            {
                return entry.Parsed!;
            }

            var parsed = new Markup(markup);
            _entries[index] = new Entry { Source = markup, Parsed = parsed };
            return parsed;
        }
    }
}
