namespace Harbor.Tui.RendererTests;

using System.Text;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.CellForge;
using Harbor.Tui.NickConsoleEx;
using Harbor.Tui.RendererTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Golden-frame regression for the generated renderer-backend adapters
///     (codegen-boilerplate sprint, Task 2): the frame-boundary sandwich
///     (generated prologue + canonical stream + generated epilogue) is pinned
///     byte-for-byte for every covered backend, and the backend-id → boundary
///     table is asserted.
/// </summary>
public class RendererAdapterGoldenFrameTests
{
    [Test]
    public async Task AnsiBackend_FrameSandwich_PinsCanonicalFrame()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var renderer = new AnsiTuiRenderer(NullLogger<AnsiTuiRenderer>.Instance, writer);

        try
        {
            AnsiTuiRenderer.WriteFramePrologue(writer);
            await renderer.InitializeAsync();
            foreach (var evt in CanonicalStreams.ChatWithToolRoundTrip())
            {
                await renderer.RenderAsync(evt);
            }
        }
        finally
        {
            renderer.Dispose();
        }

        await writer.FlushAsync();
        AnsiTuiRenderer.WriteFrameEpilogue(writer);
        await GoldenFrames.AssertGoldenAsync("adapter-ansi", sb.ToString());
    }

    [Test]
    public async Task PlainBackend_FrameSandwich_PinsCanonicalFrame()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var renderer = new PlainTuiRenderer(writer);

        await renderer.InitializeAsync();
        foreach (var evt in CanonicalStreams.ChatWithToolRoundTrip())
        {
            await renderer.RenderAsync(evt);
        }

        await writer.FlushAsync();
        renderer.Dispose();
        PlainTuiRenderer.WriteFramePrologue(writer);
        PlainTuiRenderer.WriteFrameEpilogue(writer);
        await GoldenFrames.AssertGoldenAsync("adapter-plain", sb.ToString());
    }

    [Test]
    public async Task CellForgeBackend_FrameBoundary_PinsPrologueEpilogue()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);

        CellForgeTuiRenderer.WriteFramePrologue(writer);
        CellForgeTuiRenderer.WriteFrameEpilogue(writer);
        await writer.FlushAsync();

        await GoldenFrames.AssertGoldenAsync("adapter-cellforge", sb.ToString());
    }

    [Test]
    public async Task NickConsoleExBackend_FrameBoundary_PinsPrologueEpilogue()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);

        NickConsoleExTuiRenderer.WriteFramePrologue(writer);
        NickConsoleExTuiRenderer.WriteFrameEpilogue(writer);
        await writer.FlushAsync();

        await GoldenFrames.AssertGoldenAsync("adapter-nickconsoleex", sb.ToString());
    }

    [Test]
    public async Task BackendIds_AndBoundaryTable_AreGenerated()
    {
        await Assert.That(AnsiTuiRenderer.BackendId).IsEqualTo("ansi");
        await Assert.That(PlainTuiRenderer.BackendId).IsEqualTo("plain");
        await Assert.That(CellForgeTuiRenderer.BackendId).IsEqualTo("cellforge");
        await Assert.That(NickConsoleExTuiRenderer.BackendId).IsEqualTo("nickconsoleex");

        await Assert.That(AnsiTuiRenderer.CursorFrameBoundary).IsTrue();
        await Assert.That(PlainTuiRenderer.CursorFrameBoundary).IsFalse();
        await Assert.That(CellForgeTuiRenderer.CursorFrameBoundary).IsTrue();
        await Assert.That(NickConsoleExTuiRenderer.CursorFrameBoundary).IsTrue();

        // Per-namespace boundary tables.
        await Assert.That(Harbor.Tui.AnsiPlain.TuiRendererFrameBoundaries.HasCursorFrameBoundary("ansi")).IsTrue();
        await Assert.That(Harbor.Tui.AnsiPlain.TuiRendererFrameBoundaries.HasCursorFrameBoundary("plain")).IsFalse();
        await Assert.That(Harbor.Tui.AnsiPlain.TuiRendererFrameBoundaries.HasCursorFrameBoundary("unknown")).IsFalse();
        await Assert.That(Harbor.Tui.CellForge.TuiRendererFrameBoundaries.HasCursorFrameBoundary("cellforge")).IsTrue();
        await Assert.That(Harbor.Tui.NickConsoleEx.TuiRendererFrameBoundaries.HasCursorFrameBoundary("nickconsoleex")).IsTrue();
    }
}
