using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Services;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Frames-lane golden tests (#417): eight deterministic component states
///     captured through <see cref="GoldenFrame.CaptureSettledSnapshot" /> and
///     compared against
///     <c>tests/fixtures/avalonia/frames/&lt;surface&gt;/&lt;scenario&gt;.golden.txt</c>.
/// </summary>
/// <remarks>
///     <para>
///         Only control types already goldenized in <see cref="GoldenFrameTests" />
///         are used, with fixed text, fixed durations and no animations, so every
///         frame settles to the same content. Regenerate with
///         <c>HARBOR_UPDATE_GOLDENS=1</c> (or the <c>goldens</c> workflow),
///         which writes the baseline; commit it in the SAME change as the
///         code that changed the frame.
///     </para>
///     <para>
///         The lane is NOT skipped on CI. It used to compare PNG rasters and
///         skipped every comparison on a shared runner, so the frames were
///         never checked anywhere (#1795); the content snapshot is a function
///         of the code alone and is checked on every host.
///     </para>
/// </remarks>
[NotInParallel("e2e-framework")]
public sealed class ComponentGoldenFramesTests : GoldenTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Frames").ConfigureAwait(false);

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Toolcall_Success()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => ToolCard(ToolCallState.Success, "Applied 1 hunk +12 -3"), 380, 120);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/toolcall/success.golden.txt", snapshot, "Applied 1 hunk");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Toolcall_Error()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => ToolCard(ToolCallState.Error, "Exit 1: file not found"), 380, 120);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/toolcall/error.golden.txt", snapshot, "file not found");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Toolcall_Running()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => ToolCard(ToolCallState.Running, "Running: reading src/App.cs"), 380, 120);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/toolcall/running.golden.txt", snapshot, "Running: reading");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Toast_Single()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => ToastView((ToastKind.Info, "Info: connection established.")), 420, 140);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/toast/single.golden.txt", snapshot, "connection established");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Toast_Triple()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => ToastView(ThreeToasts()), 420, 260);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/toast/triple.golden.txt", snapshot, "file saved");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Typewriter_Plain()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => TypewriterView("All checks passed."), 380, 90);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/typewriter/plain.golden.txt", snapshot, "All checks passed.");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Sparkline_Ramp()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => SparklineView(2, 5, 3, 8, 4, 9, 6, 7), 200, 80);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/sparkline/ramp.golden.txt", snapshot, "Sparkline");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Frames_Sparkline_Flat()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => SparklineView(5, 5, 5, 5, 5, 5, 5, 5), 200, 80);
        await VerifyGoldenAsync("tests/fixtures/avalonia/frames/sparkline/flat.golden.txt", snapshot, "Sparkline");
    }
}
