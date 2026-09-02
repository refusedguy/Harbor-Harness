namespace Harbor.Tui.RendererTests;

using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.RendererTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Golden-frame visual regression for the CellForge backend
///     (renderer-unification sprint Phase 5): the event-driven adapter path
///     through the AnsiWriter SGR automaton is captured via an in-memory
///     <see cref="ITerminalBackend"/> and pinned against a committed golden
///     frame. CellForge's own optimizations (SGR diffing, cell-diff
///     ScreenBuffer/DiffEngine) are treated as an opaque box — hard rule 5 —
///     only its frame output is asserted.
/// </summary>
public class CellForgeGoldenFrameTests
{
    [Test]
    public async Task CellForge_RendersCanonicalStream_ThroughInMemoryBackend()
    {
        var backend = new InMemoryTerminalBackend();
        var renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance, backend);

        try
        {
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

        await Assert.That(backend.Output).IsNotEmpty();
        await GoldenFrames.AssertGoldenAsync("cellforge", backend.Output);
    }
}
