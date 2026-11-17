using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #2352 — the ready-cell-pattern cache. The tests pin the lazy
///     get-or-insert contract (one build per key, hits thereafter), the
///     (grapheme, style, width) key, the ready Cell[] value shape, the
///     ScreenBuffer/DiffEngine integration, and the zero-allocation hit path.
///     The allocation gate uses the per-thread counter with NO await in the
///     measured window: a continuation on another thread would make the two
///     samples come from unrelated counters (the #741 trap).
/// </summary>
public class GlyphCacheTests
{
    private static readonly EngineCells.CellStyle Plain = EngineCells.CellStyle.Plain;

    [Test]
    public async Task GetOrInsert_BuildsOnce_ThenHits()
    {
        var cache = new EngineCells.GlyphCache();
        var key = EngineCells.GlyphKey.ForRune('A', Plain);

        EngineCells.Cell[] first = cache.GetOrInsert(key, static k =>
            [EngineCells.Cell.FromRaw(k.Grapheme, k.Style.Fg.Value, k.Style.Bg.Value, (ushort)k.Style.Attrs, k.Width)]);
        EngineCells.Cell[] second = cache.GetOrInsert(key, _ =>
        {
            throw new InvalidOperationException("The factory must not run on a hit.");
        });

        await Assert.That(cache.Misses).IsEqualTo(1)
            .Because("The first lookup builds the pattern; a factory that throws on a hit proves no rebuild.");
        await Assert.That(cache.Hits).IsEqualTo(1)
            .Because("The second lookup is a hit.");
        await Assert.That(cache.Count).IsEqualTo(1)
            .Because("One key, one retained pattern.");
        await Assert.That(ReferenceEquals(first, second)).IsTrue()
            .Because("A hit hands back the SAME stored array — the display path references the pattern.");
        await Assert.That(cache.TryGet(key, out EngineCells.Cell[] lookedUp)).IsTrue();
        await Assert.That(ReferenceEquals(lookedUp, first)).IsTrue();
    }

    [Test]
    public async Task Key_DistinguishesStyleAndWidth()
    {
        var cache = new EngineCells.GlyphCache();
        var bold = new EngineCells.CellStyle(
            EngineCells.PackedColor.Indexed(3), EngineCells.PackedColor.Default, EngineCells.StyleAttr.Bold);

        _ = cache.GetOrInsert('A', Plain);
        _ = cache.GetOrInsert('A', Plain);
        _ = cache.GetOrInsert('A', bold);          // different style
        _ = cache.GetOrInsert('界', Plain);        // different grapheme + width

        await Assert.That(cache.Misses).IsEqualTo(3)
            .Because("Style and grapheme are part of the key: three distinct keys build three patterns.");
        await Assert.That(cache.Hits).IsEqualTo(1)
            .Because("Only the repeated (A, plain) lookup is a hit.");
        await Assert.That(cache.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Pattern_IsReadyCells_NarrowWideAndZero()
    {
        var cache = new EngineCells.GlyphCache();
        var bold = new EngineCells.CellStyle(
            EngineCells.PackedColor.Default, EngineCells.PackedColor.Default, EngineCells.StyleAttr.Bold);

        EngineCells.Cell[] narrow = cache.GetOrInsert('A', bold);
        EngineCells.Cell[] wide = cache.GetOrInsert('界', bold);
        EngineCells.Cell[] zero = cache.GetOrInsert('\u0301', bold); // combining acute: width 0

        var expectedNarrow = EngineCells.Cell.FromRaw('A', bold.Fg.Value, bold.Bg.Value, (ushort)bold.Attrs, EngineCells.Cell.Narrow);
        var expectedWideLead = EngineCells.Cell.FromRaw('界', bold.Fg.Value, bold.Bg.Value, (ushort)bold.Attrs, EngineCells.Cell.Wide);

        await Assert.That(narrow.Length).IsEqualTo(1);
        await Assert.That(narrow[0]).IsEqualTo(expectedNarrow)
            .Because("A narrow pattern is a ready styled cell, not a rune to be re-measured.");
        await Assert.That(wide.Length).IsEqualTo(2)
            .Because("A wide pattern carries the lead AND the invisible tail.");
        await Assert.That(wide[0]).IsEqualTo(expectedWideLead);
        await Assert.That(wide[1]).IsEqualTo(EngineCells.Cell.WideTail);
        await Assert.That(zero.Length).IsEqualTo(0)
            .Because("A zero-width glyph paints nothing and reuses the empty array.");
        await Assert.That(ReferenceEquals(zero, Array.Empty<EngineCells.Cell>())).IsTrue();
    }

    [Test]
    public async Task SetGlyph_PaintsTheSameCellsAsSetRune()
    {
        const int cols = 8;
        const int rows = 3;
        var plain = new EngineCells.ScreenBuffer(cols, rows);
        var cached = new EngineCells.ScreenBuffer(cols, rows) { GlyphCache = new EngineCells.GlyphCache() };

        var bold = new EngineCells.CellStyle(
            EngineCells.PackedColor.Indexed(2), EngineCells.PackedColor.Indexed(7), EngineCells.StyleAttr.Underline);

        // Narrow, wide and zero-width runes through both paint paths.
        (int X, int Y, int Rune)[] glyphs =
        [
            (0, 0, 'H'), (1, 0, '界'), (3, 0, '\u0301'), (0, 1, 0x1F642),
        ];
        foreach (var (x, y, rune) in glyphs)
        {
            plain.SetRune(x, y, new Rune(rune), bold);
            cached.SetGlyph(x, y, rune, bold);
        }

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                await Assert.That(cached.Get(x, y)).IsEqualTo(plain.Get(x, y))
                    .Because($"cell ({x},{y}) must be identical through SetGlyph and SetRune.");
            }
        }

        await Assert.That(cached.GlyphCache!.Hits).IsEqualTo(0)
            .Because("Each glyph here is painted once: all misses, no repeats.");
        await Assert.That(cached.GlyphAdvance('界', bold)).IsEqualTo(2)
            .Because("The cached wide pattern's length IS its column advance.");
    }

    [Test]
    public async Task DiffEngine_SharesItsCacheWithBothGrids()
    {
        var writer = new AnsiWriter(new CountingBackend(), syncUpdates: true);
        var engine = new DiffEngine(20, 4);
        var back = new EngineCells.ScreenBuffer(20, 4);

        await Assert.That(ReferenceEquals(engine.Front.GlyphCache, engine.GlyphCache)).IsTrue()
            .Because("The diff engine hands its cache to the mirror grid at construction.");

        writer.BeginFrame();
        engine.Flush(back, writer);
        writer.EndFrame();

        await Assert.That(ReferenceEquals(back.GlyphCache, engine.GlyphCache)).IsTrue()
            .Because("Flush propagates the cache to the back buffer so the next frame paints hit it.");
    }

    [Test]
    public async Task Cache_IsBoundedByCapacity()
    {
        var cache = new EngineCells.GlyphCache(capacity: 4);
        for (int i = 0; i < 32; i++)
        {
            _ = cache.GetOrInsert('A' + i, Plain);
        }

        await Assert.That(cache.Count).IsLessThanOrEqualTo(4)
            .Because("The cache never grows past its cap — the UnicodeWidth-style never-grows policy.");
        await Assert.That(cache.Misses).IsEqualTo(32);
    }

    /// <summary>
    ///     The allocation gate. A cache hit and a cache-backed paint allocate
    ///     nothing: no width lookup result, no style object, no Cell[] copy on
    ///     the heap. Warm past JIT tier-up first; the measured window is fully
    ///     synchronous, so the per-thread counter is exact.
    /// </summary>
    [Test]
    public async Task HitPath_AllocatesNothing()
    {
        var cache = new EngineCells.GlyphCache();
        var screen = new EngineCells.ScreenBuffer(80, 24) { GlyphCache = cache };
        var style = new EngineCells.CellStyle(
            EngineCells.PackedColor.Indexed(9), EngineCells.PackedColor.Indexed(4), EngineCells.StyleAttr.Bold);

        int[] glyphs = ['A', 'B', '界', 0x1F642];

        // Populate the keys before counting: misses legitimately allocate arrays.
        for (int i = 0; i < 100_000; i++)
        {
            int rune = glyphs[i & 3];
            _ = cache.GetOrInsert(rune, style);
            screen.SetGlyph(i % screen.Cols, i % screen.Rows, rune, style);
            _ = screen.GlyphAdvance(rune, style);
        }

        long hitsBefore = cache.Hits;
        long missesBefore = cache.Misses;
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int iterations = 100_000;
        for (int i = 0; i < iterations; i++)
        {
            int rune = glyphs[i & 3];
            _ = cache.GetOrInsert(rune, style);
            screen.SetGlyph(i % screen.Cols, i % screen.Rows, rune, style);
            _ = screen.GlyphAdvance(rune, style);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Current?.Output.WriteLine(
            $"#2352 hit path: {iterations} iterations, {allocated} B allocated, "
            + $"{cache.Hits - hitsBefore} hits, {cache.Misses - missesBefore} misses");

        await Assert.That(cache.Misses - missesBefore).IsEqualTo(0)
            .Because("Warm-up must have populated every key — the measured window is all hits.");
        await Assert.That(cache.Hits - hitsBefore).IsEqualTo(iterations * 3)
            .Because("Each iteration does a cache lookup, a paint and an advance lookup.");
        await Assert.That(allocated).IsEqualTo(0)
            .Because("#2352: a hit hands back the prebuilt pattern; no width, style or array is recomputed.");
    }
}
