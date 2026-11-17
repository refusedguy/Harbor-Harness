// #2352: cache of ready cell patterns keyed by (grapheme, style, width).
// Modelled on GPUI's PlatformAtlas::get_or_insert_with (zed-gpui-engine.md §5,
// "Отдельно про glyph cache и texture atlas") — but this is NOT a GPU texture
// atlas. The value is a ready Cell[] pattern (lead + optional wide tail), and
// the only "rendering" is copying those cells into a ScreenBuffer or emitting
// them through AnsiWriter. The engine is a BCL-only, AOT-compatible leaf: no
// reflection, no dynamic code, no Harbor references.

using System;
using System.Collections.Generic;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     The cache key: the grapheme (a rune for the single-rune clusters Cell can
///     represent), the packed style, and the resolved display width. Width is a
///     field of the key, not of the value, so a future cluster-aware emitter can
///     store two patterns for the same leading rune at different widths without
///     changing the lookup. A record struct gives value equality for free — the
///     key never allocates and never boxes on a hit.
/// </summary>
public readonly record struct GlyphKey(int Grapheme, CellStyle Style, byte Width)
{
    /// <summary>
    ///     Builds the key for a single rune, resolving its width once through
    ///     <see cref="UnicodeWidth.Width(Rune)" />. Invalid scalar values are
    ///     treated as narrow so the key can always be formed.
    /// </summary>
    public static GlyphKey ForRune(int rune, in CellStyle style)
    {
        byte width = Rune.IsValid(rune)
            ? (byte)UnicodeWidth.Width(new Rune(rune))
            : Cell.Narrow;
        return new GlyphKey(rune, style, width);
    }
}

/// <summary>
///     #2352 — a lazy cache of ready <see cref="Cell"/> patterns. A miss calls
///     the supplied factory once (UnicodeWidth + style packing happen there),
///     stores the result, and returns it; a hit hands back the stored array with
///     no width lookup, no style packing and no allocation. This is the terminal
///     analogue of a glyph atlas: the display path references a prebuilt cell
///     pattern instead of recomputing it for every occurrence of a symbol,
///     icon, emoji or Nerd-Font glyph.
/// </summary>
/// <remarks>
///     <para>
///         Bounded by construction: at most <see cref="Capacity"/> entries are
///         retained, and inserting past the cap evicts one entry (the same
///         never-grows policy as <see cref="UnicodeWidth.WidthCached"/>). Hits
///         allocate nothing — the dictionary lookup uses the struct key directly.
///     </para>
///     <para>
///         Not thread-safe by design: a cache instance belongs to one render
///         session, the same ownership <see cref="ScreenBuffer"/> and
///         <see cref="DiffEngine"/> have. A hit is a plain dictionary read with
///         no monitor, so the per-frame paint path does not serialise renderers.
///     </para>
/// </remarks>
public sealed class GlyphCache
{
    /// <summary>Default retained-pattern cap.</summary>
    public const int DefaultCapacity = 512;

    private readonly int _capacity;
    private readonly Dictionary<GlyphKey, Cell[]> _entries;
    private long _hits;
    private long _misses;

    /// <summary>Creates a cache retaining at most <paramref name="capacity"/> patterns.</summary>
    public GlyphCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _entries = new Dictionary<GlyphKey, Cell[]>(capacity);
    }

    /// <summary>Retained-pattern cap (the constructor argument).</summary>
    public int Capacity => _capacity;

    /// <summary>Number of retained patterns (≤ <see cref="Capacity"/>).</summary>
    public int Count => _entries.Count;

    /// <summary>Cumulative successful lookups.</summary>
    public long Hits => _hits;

    /// <summary>Cumulative cache misses (patterns actually built).</summary>
    public long Misses => _misses;

    /// <summary>
    ///     Lazy get-or-insert. <paramref name="factory"/> runs only on a miss.
    ///     The returned array must not be mutated by callers — it is shared by
    ///     every hit for the key (copy it into the grid; never write into it).
    /// </summary>
    public Cell[] GetOrInsert(in GlyphKey key, Func<GlyphKey, Cell[]> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_entries.TryGetValue(key, out Cell[]? hit))
        {
            _hits++;
            return hit;
        }

        Cell[] built = factory(key)
            ?? throw new InvalidOperationException("GlyphCache factory returned null.");
        _misses++;
        if (_entries.Count >= _capacity)
        {
            EvictOne();
        }

        _entries[key] = built;
        return built;
    }

    /// <summary>
    ///     Convenience overload for the per-rune case: forms the key (one width
    ///     lookup) and builds the pattern on a miss. The build uses
    ///     <see cref="Cell.FromRaw"/> with the width already in the key, so even
    ///     the miss path resolves the width only once.
    /// </summary>
    public Cell[] GetOrInsert(int rune, in CellStyle style) =>
        GetOrInsert(GlyphKey.ForRune(rune, style), static key => Build(key));

    /// <summary>Tries a lookup without building anything. Useful for tests.</summary>
    public bool TryGet(in GlyphKey key, out Cell[] pattern)
    {
        if (_entries.TryGetValue(key, out Cell[]? found))
        {
            pattern = found;
            return true;
        }

        pattern = null!;
        return false;
    }

    /// <summary>Clears every pattern and resets the hit/miss counters.</summary>
    public void Clear()
    {
        _entries.Clear();
        _hits = 0;
        _misses = 0;
    }

    /// <summary>
    ///     Builds the canonical pattern for a key. Narrow glyphs are one cell;
    ///     wide glyphs are a lead plus a <see cref="Cell.WideTail"/>; zero-width
    ///     glyphs paint nothing and use the allocation-free empty array.
    /// </summary>
    private static Cell[] Build(in GlyphKey key)
    {
        if (key.Width == 0)
        {
            return Array.Empty<Cell>();
        }

        uint fg = key.Style.Fg.Value;
        uint bg = key.Style.Bg.Value;
        ushort flags = (ushort)key.Style.Attrs;
        switch (key.Width)
        {
            case Cell.Narrow:
                return [Cell.FromRaw(key.Grapheme, fg, bg, flags, Cell.Narrow)];
            case Cell.Wide:
                return
                [
                    Cell.FromRaw(key.Grapheme, fg, bg, flags, Cell.Wide),
                    Cell.WideTail,
                ];
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(key), key.Width, "Cell patterns support widths 0, 1 or 2 only.");
        }
    }

    private void EvictOne()
    {
        GlyphKey first;
        using (var enumerator = _entries.GetEnumerator())
        {
            if (!enumerator.MoveNext())
            {
                return;
            }

            first = enumerator.Current.Key;
        }

        _entries.Remove(first);
    }
}
