namespace Harbor.Tui.RendererTests;

using Harbor.Tui.NickConsoleEx;
using Harbor.Tui.RendererTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Golden-frame visual regression for the NickConsoleEx backend
///     (renderer-unification sprint Phase 5): the wrapper over the vendored
///     nickprotop/ConsoleEx renders onto a fixed 120x40
///     <see cref="RecordingConsoleDriver"/> (headless + cell capture), and the
///     composed visible screen is pinned against a committed golden frame.
/// </summary>
public class NickConsoleExGoldenFrameTests
{
    [Test]
    public async Task NickConsoleEx_RendersCanonicalStream_IntoHeadlessDriver()
    {
        var driver = new RecordingConsoleDriver(120, 40);
        var renderer = new NickConsoleExTuiRenderer(
            NullLogger<NickConsoleExTuiRenderer>.Instance, driverOverride: driver);

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

        string frame = driver.Snapshot();
        await Assert.That(frame).IsNotEmpty();
        await GoldenFrames.AssertGoldenAsync("nickconsoleex", frame);
    }
}
