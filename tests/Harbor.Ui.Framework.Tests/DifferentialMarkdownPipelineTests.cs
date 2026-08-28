namespace Harbor.Ui.Framework.Tests;

using System.Text;
using System.Collections.Immutable;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Markdown;
using Harbor.Ui.Framework.Rendering.PerformanceContracts;
using Harbor.Ui.Framework.Rendering.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Differential markdown pipeline + frozen-tail cache tests
///     (renderer-unification sprint Phase 6.4): LRU eviction, freeze
///     observability, tail-only diffs, and the formal performance contract.
/// </summary>
public class DifferentialMarkdownPipelineTests
{
    [Test]
    public async Task Cache_FreezeAndRestore_RoundTripsCells()
    {
        var cache = new FrozenTailMarkdownCache();
        var cells = new Cell[] { Cell.Blank, Cell.From(new Rune('X'), CellStyle.Plain) };
        BlockFrozenEventArgs? frozen = null;
        cache.BlockFrozen += (_, e) => frozen = e;

        cache.Freeze(7, cells);
        bool hit = cache.TryGet(7, out Cell[]? restored);

        await Assert.That(hit).IsTrue();
        await Assert.That(restored![1].Rune).IsEqualTo((int)'X');
        await Assert.That(frozen).IsNotNull();
        await Assert.That(frozen!.BlockId).IsEqualTo(7);
    }

    [Test]
    public async Task Cache_LruEvicts_WhenOverCapacity()
    {
        var cache = new FrozenTailMarkdownCache(capacity: 3);
        foreach (int id in new[] { 1, 2, 3 })
        {
            cache.Freeze(id, [Cell.Blank]);
        }

        cache.TryGet(1, out _); // touch 1 → 2 becomes the LRU victim
        cache.Freeze(4, [Cell.Blank]);

        await Assert.That(cache.Count).IsEqualTo(3);
        await Assert.That(cache.TryGet(2, out _)).IsFalse();
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        await Assert.That(cache.TryGet(4, out _)).IsTrue();
    }

    [Test]
    public async Task Pipeline_FrozenBlock_RestoresWithoutRestyle()
    {
        var pipeline = new DifferentialMarkdownPipeline(40, 10);
        var lines = new[] { BuildLine("completed block") };

        CellDiffBatch first = pipeline.RenderBlock(1, lines, isComplete: true, y: 0);
        await Assert.That(first.Changes.Length).IsGreaterThan(0); // full row painted

        // Mutate the incoming lines — a frozen block must NOT restyle them.
        lines[0] = BuildLine("mutated tail!!");
        CellDiffBatch second = pipeline.RenderBlock(1, lines, isComplete: true, y: 0);

        await Assert.That(second.Changes.Length).IsEqualTo(0); // cache hit → byte-identical rows
    }

    [Test]
    public async Task Pipeline_TailBlock_DiffsOnlyChangedCells()
    {
        var pipeline = new DifferentialMarkdownPipeline(40, 10);
        _ = pipeline.RenderBlock(1, [BuildLine("tail")], isComplete: false, y: 0);

        // Stream one more token into the tail: only the delta is reported.
        CellDiffBatch batch = pipeline.RenderBlock(1, [BuildLine("tail+token")], isComplete: false, y: 0);

        await Assert.That(batch.Changes.Length).IsGreaterThan(0);
        await Assert.That(batch.Changes.Length).IsLessThan(40);
    }

    [Test]
    public async Task Pipeline_BroadcastsTailDiffs_ToSubscribedSinks()
    {
        var pipeline = new DifferentialMarkdownPipeline(40, 10);
        var batches = new List<CellDiffBatch>();
        pipeline.Diffs.Subscribe(new CollectingSink(batches));

        _ = pipeline.RenderBlock(1, [BuildLine("tail")], isComplete: false, y: 0);

        await Assert.That(batches.Count).IsEqualTo(1);
    }

    private static void FreezeDocument(DifferentialMarkdownPipeline pipeline, int frozenBlocks)
    {
        var lines = new[] { BuildLine("frozen") };
        for (int i = 0; i < frozenBlocks; i++)
        {
            _ = pipeline.RenderBlock(i, lines, isComplete: true, y: 0);
        }
    }

    private static MdLine BuildLine(string text) =>
        new([new MdSpan(text, MdStyle.Normal)]);

    private sealed class CollectingSink(List<CellDiffBatch> batches) : ICellDiffSink
    {
        public void Accept(in CellDiffBatch batch) => batches.Add(batch);
    }
}
