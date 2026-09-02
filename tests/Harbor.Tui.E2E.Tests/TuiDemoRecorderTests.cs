using Harbor.E2E.Framework;
using TUnit.Core;
using TUnit.Core.Enums;

namespace Harbor.Tui.E2E.Tests;

/// <summary>
///     Regenerates the README demo GIFs by driving <c>harbor --demo</c> in a
///     PTY and capturing frames (see <see cref="TuiDemoRecorder" />).
///     Opt-in — the suite only runs when <c>HARBOR_DEMO=1</c>, because it
///     needs a real PTY, a frame renderer (Chromium/ImageMagick) and an
///     assembler (ffmpeg/ImageMagick):
///     <c>HARBOR_DEMO=1 dotnet test tests/Harbor.Tui.E2E.Tests</c>.
///     CI regenerates demos via VHS instead (see .github/workflows/demo.yml).
/// </summary>
public class TuiDemoRecorderTests
{
    [Test]
    public async Task Record_DemoScenes_ProducesNonEmptyGifs()
    {
        if (Environment.GetEnvironmentVariable("HARBOR_DEMO") is not "1")
        {
            Skip.Test("Set HARBOR_DEMO=1 to regenerate the README demo GIFs (requires PTY + chromium/ffmpeg).");
        }

        if (!TuiDriver.IsPtyAvailable())
        {
            Skip.Test(TuiDriver.NoPtySkipReason);
        }

        // The 4 README GIFs — mirrors demo/*.tape (plain = renderer-switch variant).
        var recordings = new (string Scene, string Tui, string Output)[]
        {
            ("hero", "ansi", "assets/demo/hero-compressed.gif"),
            ("markdown", "ansi", "assets/demo/markdown-compressed.gif"),
            ("approval", "ansi", "assets/demo/approval-compressed.gif"),
            ("hero", "plain", "assets/demo/plain-compressed.gif"),
        };

        foreach (var (scene, tui, output) in recordings)
        {
            TuiDemoRecording recording = await TuiDemoRecorder.RecordAsync(new TuiDemoRecordingOptions
            {
                Scene = scene,
                TuiName = tui,
                OutputGif = output
            }).ConfigureAwait(false);

            await Assert.That(File.Exists(recording.GifPath)).IsTrue();
            await Assert.That(recording.FrameCount).IsGreaterThanOrEqualTo(3);
            await Assert.That(recording.GifBytes).IsGreaterThan(0);
        }
    }
}
