namespace Harbor.Tui.ConsoleEx.PtyTests;

/// <summary>
///     Golden-file plumbing for PTY scenarios. Fixtures live in
///     <c>tests/fixtures/consoleex-pty</c> and are located by walking up from
///     the test binaries to the repo root (<c>Harbor.slnx</c> marker).
///
///     Regeneration contract: run with <c>HARBOR_UPDATE_GOLDENS=1</c> to
///     overwrite goldens; CI never regenerates — a mismatching golden fails.
///
///     Normalization contract (flake control, celldiff §8): only deterministic
///     screen states are goldenized — idle launch frame, settled post-resize
///     frame. Streaming phases are asserted by markers instead because the
///     mock provider's chunk cadence makes intermediate frames nondeterministic.
/// </summary>
internal static class PtyGolden
{
    private static readonly Lazy<string> FixtureDir = new(ResolveFixtureDir);

    /// <summary>Returns the expected normalized grid text, seeding the fixture in update mode.</summary>
    public static string Verify(string name, string actualNormalizedText)
    {
        string path = Path.Combine(FixtureDir.Value, name + ".golden.txt");
        if (Environment.GetEnvironmentVariable("HARBOR_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(FixtureDir.Value);
            File.WriteAllText(path, actualNormalizedText);
            return actualNormalizedText;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"golden fixture missing: {path} (run once with HARBOR_UPDATE_GOLDENS=1 to seed it)");
        }

        return File.ReadAllText(path);
    }

    private static string ResolveFixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir is null
            ? throw new InvalidOperationException("repo root not found — cannot locate fixtures")
            : Path.Combine(dir.FullName, "tests", "fixtures", "consoleex-pty");
    }
}
