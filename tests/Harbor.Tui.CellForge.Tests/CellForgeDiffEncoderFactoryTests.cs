using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

// #436: buffers come in two vocabularies here — the engine pair feeds the
// DiffEngine handle, the Rendering pair feeds the portable encoder and the
// adapter under test. Both fill helpers stay verbatim twins on purpose: the
// adapter's snapshot must reproduce the engine front exactly, and the
// single-change assertions below are what proves it.
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Track D (#27): <see cref="RowHashDiffEncoder" /> is the single
///     <see cref="ICellDiffEncoder" /> implementation;
///     <see cref="CellForgeDiffEncoder" /> is a thin factory over it, and the
///     hint-area threshold has one home (<see cref="CellDiffHints" />).
/// </summary>
public class CellForgeDiffEncoderFactoryTests
{
    private static EngineCells.ScreenBuffer MakeEngineBuffer(int cols, int rows, char fill)
    {
        var buffer = new EngineCells.ScreenBuffer(cols, rows);
        for (int y = 0; y < rows; y++)
            buffer.SetText(0, y, new string(fill, cols), EngineCells.CellStyle.Plain);
        return buffer;
    }

    private static UIR.ScreenBuffer MakeRenderingBuffer(int cols, int rows, char fill)
    {
        var buffer = new UIR.ScreenBuffer(cols, rows);
        for (int y = 0; y < rows; y++)
            buffer.SetText(0, y, new string(fill, cols), UIR.CellStyle.Plain);
        return buffer;
    }

    [Test]
    public async Task Threshold_Single_Source_Of_Truth()
    {
        await Assert.That(DiffEngine.HintAreaThreshold).IsEqualTo(RowHashDiffEncoder.HintAreaThreshold);
        await Assert.That(RowHashDiffEncoder.HintAreaThreshold).IsEqualTo(CellDiffHints.HintAreaThreshold);
        await Assert.That(CellDiffHints.HintAreaThreshold).IsEqualTo(0.25);
    }

    [Test]
    public async Task EngineLinked_Encodes_Front_To_Next()
    {
        var front = MakeEngineBuffer(10, 4, 'a');
        var engine = new DiffEngine(front);
        var next = MakeRenderingBuffer(10, 4, 'a');
        next.SetRune(2, 1, new Rune('X'), UIR.CellStyle.Plain);

        var linked = CellForgeDiffEncoder.CreateEngineLinked(engine);
        CellDiffBatch batch = linked.EncodeCellForge(next, hints: null, sequence: 3);

        await Assert.That(batch.Changes.Length).IsEqualTo(1);
        await Assert.That(batch.Changes[0].X).IsEqualTo(2);
        await Assert.That(batch.Changes[0].Y).IsEqualTo(1);

        // Parity with the portable encoder on the same frame pair.
        ICellDiffEncoder portable = CellForgeDiffEncoder.Create();
        var renderingFront = MakeRenderingBuffer(10, 4, 'a');
        CellDiffBatch expected = portable.Encode(renderingFront, next, hints: null, sequence: 3);
        await Assert.That(batch.Changes.Length).IsEqualTo(expected.Changes.Length);
        await Assert.That(batch.Changes[0]).IsEqualTo(expected.Changes[0]);
    }

    [Test]
    public async Task EncodeFromEngineFront_Matches_Portable()
    {
        var front = MakeEngineBuffer(8, 3, 'b');
        var engine = new DiffEngine(front);
        var next = MakeRenderingBuffer(8, 3, 'b');
        next.SetRune(0, 0, new Rune('Z'), UIR.CellStyle.Plain);

        CellDiffBatch viaHelper = CellForgeDiffEncoder.EncodeFromEngineFront(engine, next, hints: null, sequence: 1);
        var renderingFront = MakeRenderingBuffer(8, 3, 'b');
        CellDiffBatch viaPortable = new RowHashDiffEncoder().Encode(renderingFront, next, hints: null, sequence: 1);

        await Assert.That(viaHelper.Changes.Length).IsEqualTo(viaPortable.Changes.Length);
        await Assert.That(viaHelper.Changes[0]).IsEqualTo(viaPortable.Changes[0]);
    }
}
