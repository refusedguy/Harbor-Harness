using System.Globalization;
using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Golden-file plumbing: fixtures live in <c>tests/fixtures/celldiff</c> and are
/// located by walking up from the test binaries to the repo root
/// (<c>Harbor.slnx</c> marker, same approach as ThemeParityTests).
///
/// Regeneration contract: run with <c>HARBOR_UPDATE_GOLDENS=1</c> to overwrite
/// goldens (and emit companion SVGs for human review) — CI never regenerates;
/// without the flag a mismatching golden fails the test.
/// </summary>
internal static class Golden
{
    private static readonly Lazy<string> _fixtureDir = new(ResolveFixtureDir);

    /// <summary>
    /// Verifies the named golden against <paramref name="actualContent"/>.
    /// With HARBOR_UPDATE_GOLDENS=1 the file is (re)written and an optional
    /// SVG artifact is dropped next to it; otherwise returns the expected
    /// content for the caller to assert.
    /// </summary>
    public static string Verify(string name, string actualContent, string? svgContent = null)
    {
        string path = Path.Combine(_fixtureDir.Value, name + ".golden.txt");
        if (IsUpdateMode())
        {
            Directory.CreateDirectory(_fixtureDir.Value);
            File.WriteAllText(path, actualContent);
            if (svgContent is not null)
            {
                File.WriteAllText(Path.Combine(_fixtureDir.Value, name + ".svg"), svgContent);
            }

            return actualContent;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"golden fixture missing: {path} (run once with HARBOR_UPDATE_GOLDENS=1 to seed it)");
        }

        return File.ReadAllText(path);
    }

    public static bool IsUpdateMode() =>
        Environment.GetEnvironmentVariable("HARBOR_UPDATE_GOLDENS") == "1";

    /// <summary>Shared fixture directory (celldiff goldens + baselines manifest).</summary>
    internal static string FixtureDir => _fixtureDir.Value;

    private static string ResolveFixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("repo root (Harbor.slnx) not found from " + AppContext.BaseDirectory);
        }

        return Path.Combine(dir.FullName, "tests", "fixtures", "celldiff");
    }
}

/// <summary>Formats the standard three-layer golden document.</summary>
internal static class GoldenDoc
{
    public static string Build(string title, ScreenBuffer finalGrid, RecordingBackend backend)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# golden: {title}\n");
        sb.Append($"## grid art ({finalGrid.Cols}x{finalGrid.Rows})\n");
        sb.Append(GridDump.Art(finalGrid));
        sb.Append("## cells (x,y: rune fg/bg/attrs width)\n");
        sb.Append(GridDump.Cells(finalGrid));
        sb.Append("## frames\n");
        sb.Append(GridDump.Frames(backend));
        return sb.ToString();
    }
}
