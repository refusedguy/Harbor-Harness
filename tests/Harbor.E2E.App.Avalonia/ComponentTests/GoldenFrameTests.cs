using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Services;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Golden-frame tests (sprint Testing Strategy З.2): four core components
///     rendered in a headless Skia compositor, snapshotted as the CONTENT of
///     their visual tree and compared against the committed baselines under
///     <c>tests/fixtures/golden/&lt;TestName&gt;.golden.txt</c>.
/// </summary>
/// <remarks>
///     <para>
///         ANY difference in the rendered content fails the test — this is a
///         comparison, not an existence check. Regenerate the baselines with
///         <c>HARBOR_UPDATE_GOLDENS=1</c> (or the <c>goldens</c> workflow)
///         when a visual change is intended, and commit the regenerated
///         baseline in the SAME change: a change of the frame that does not
///         touch the baseline fails the build, and a change of the baseline
///         that does not touch the frame is where the reviewer looks.
///     </para>
///     <para>
///         These tests are NOT skipped on CI. The lane used to compare
///         SHA-256 hashes of PNG rasters and skipped every comparison on a
///         shared runner, because a raster is a function of the machine's
///         Skia build and font stack — the baseline captured on one host
///         never matched another, so the most fragile part of the UI was
///         never checked (#1795). The snapshot is a function of the code
///         alone: the same baseline is checked on every host.
///     </para>
/// </remarks>
[NotInParallel("e2e-framework")]
public sealed class GoldenFrameTests : GoldenTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Golden").ConfigureAwait(false);

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task ToolCallCardView_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => ToolCard(ToolCallState.Success, "Applied 1 hunk — +12 −3", "✎"), 380, 120);
        await VerifyGoldenAsync("tests/fixtures/golden/ToolCallCardView_GoldenFrame.golden.txt", snapshot, "edit");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task TypewriterStreamingText_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => TypewriterView("Streaming code response…"), 380, 90);
        await VerifyGoldenAsync("tests/fixtures/golden/TypewriterStreamingText_GoldenFrame.golden.txt", snapshot, "Streaming code response");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task Sparkline_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => SparklineView(4, 9, 2, 7, 5, 11, 3, 8), 200, 80);
        await VerifyGoldenAsync("tests/fixtures/golden/Sparkline_GoldenFrame.golden.txt", snapshot, "Sparkline");
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task ToastNotificationsView_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var snapshot = CaptureFrame(() => ToastView(ThreeToasts()), 420, 260);
        await VerifyGoldenAsync("tests/fixtures/golden/ToastNotificationsView_GoldenFrame.golden.txt", snapshot, "connection established");
    }
}
