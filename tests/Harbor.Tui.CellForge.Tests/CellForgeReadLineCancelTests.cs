using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1110 blocking-input slice: <see cref="CellForgeTuiRenderer.ReadLineAsync"/>
/// did a bare <c>Console.ReadLine()</c>, ignoring the <c>CancellationToken</c>
/// — the fullscreen frame-loop thread stalled on input and a cancel (e.g. the
/// permission asker, which passes <c>ct</c> expecting Deny-on-cancel) never
/// landed. Pre-cancelled token must throw before touching stdin.
/// </summary>
public class CellForgeReadLineCancelTests
{
    [Test]
    public async Task ReadLineAsync_PreCancelledToken_ThrowsWithoutBlocking()
    {
        using var renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance,
            new RecordingBackend());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await renderer.ReadLineAsync(">", cts.Token));
    }
}
