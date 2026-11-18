namespace Harbor.Tui.RendererTests;

using Harbor.Tui.AnsiPlain;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Issue #2640: <c>AnsiPlainTuiRenderer.ReadLineAsync</c> called
///     <see cref="Console.ReadLine"/> synchronously and ignored its
///     <see cref="CancellationToken"/>, so a cancelled permission ask left the
///     user staring at a live prompt. The renderer now uses the stdin owner
///     shared with CellForge (#2639), which detaches the caller on cancellation
///     without touching stdin or competing with a later prompt.
/// </summary>
public class AnsiPlainReadLineCancellationTests
{
    [Test]
    public async Task PreCancelledToken_ThrowsWithoutTouchingStdin()
    {
        using var writer = new StringWriter();
        using var renderer = new PlainTuiRenderer(writer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await renderer.ReadLineAsync("prompt> ", cts.Token));

        // The prompt and cursor handling still run before the read, so the
        // visible event sequence is unchanged for callers (#2640).
        await Assert.That(writer.ToString().Contains("prompt> ")).IsTrue();
    }
}
