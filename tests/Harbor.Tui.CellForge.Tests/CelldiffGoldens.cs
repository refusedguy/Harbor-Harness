using System.Globalization;
using System.Text;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Golden-file plumbing for the celldiff lane: fixtures live in
///     <c>tests/fixtures/celldiff</c> and are located by the shared helper
///     (<c>Golden.FixtureDir</c>), which finds the repository root once
///     instead of by a walk-up this file owns.
///
///     Regeneration contract: run with <c>HARBOR_UPDATE_GOLDENS=1</c> to
///     overwrite goldens (and emit companion SVGs for human review) — CI never
///     regenerates; without the flag a mismatching golden fails the test.
/// </summary>
/// <remarks>
///     The mechanics — where the root is, how the update flag is spelled, how
///     line endings are normalised, what a missing baseline says — live in
///     <see cref="Golden" /> since #1776. What stays here is the celldiff
///     lane's own: the fixture directory, the SVG sidecar, and the document
///     format.
/// </remarks>
internal static class CelldiffGoldens
{
    /// <summary>Shared fixture directory (celldiff goldens + baselines manifest).</summary>
    internal static string FixtureDir => Golden.FixtureDir("celldiff");

    /// <summary>
    ///     Verifies the named golden against <paramref name="actualContent"/>.
    ///     With HARBOR_UPDATE_GOLDENS=1 the file is (re)written and an optional
    ///     SVG artifact is dropped next to it; otherwise returns the expected
    ///     content for the caller to assert.
    /// </summary>
    public static string Verify(string name, string actualContent, string? svgContent = null)
    {
        string expected = Golden.VerifyText(FixtureDir, name, actualContent);

        // The SVG is a write-only artifact for human review: it is never
        // compared, and it goes stale silently — see ADR-010 §4.4.
        if (svgContent is not null && Golden.IsUpdateMode)
        {
            File.WriteAllText(Path.Combine(FixtureDir, name + ".svg"), svgContent);
        }

        return expected;
    }

    /// <summary>Whether this run is allowed to rewrite the goldens.</summary>
    public static bool IsUpdateMode() => Golden.IsUpdateMode;
}

/// <summary>Formats the standard three-layer golden document.</summary>
internal static class GoldenDoc
{
    public static string Build(string title, ScreenBuffer finalGrid, RecordingBackend backend)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# golden: {title}\n");
        sb.Append($"## grid art ({finalGrid.Cols}x{finalGrid.Rows})\n");
        sb.Append(TestRender.Art(finalGrid));
        sb.Append("## cells (x,y: rune fg/bg/attrs width)\n");
        sb.Append(GridDump.Cells(finalGrid));
        sb.Append("## frames\n");
        sb.Append(GridDump.Frames(backend));
        return sb.ToString();
    }
}
