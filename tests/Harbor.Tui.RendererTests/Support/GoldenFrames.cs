namespace Harbor.Tui.RendererTests.Support;

/// <summary>
///     Golden-frame persistence for renderer visual regression tests
///     (renderer-unification sprint Phase 5). A golden frame is the captured
///     renderer output for a canonical event stream; a test fails when the
///     captured frame drifts from the committed golden file.
/// </summary>
/// <remarks>
///     Regenerate goldens after an intentional visual change with
///     <c>HARBOR_UPDATE_GOLDEN=1 dotnet test tests/Harbor.Tui.RendererTests</c>
///     and commit the diff — the golden diff then documents the visual change
///     in review.
/// </remarks>
public static class GoldenFrames
{
    /// <summary>
    ///     Compares <paramref name="actual"/> against the committed golden
    ///     file <c>GoldenFrames/&lt;name&gt;.golden.txt</c>, or regenerates the
    ///     golden when <c>HARBOR_UPDATE_GOLDEN=1</c>.
    /// </summary>
    public static async Task AssertGoldenAsync(string name, string actual)
    {
        string path = GoldenPath(name);
        string expected = Normalize(actual);

        if (Environment.GetEnvironmentVariable("HARBOR_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, expected + Environment.NewLine);
            return;
        }

        await Assert.That(File.Exists(path)).IsTrue()
            .Because($"golden frame '{name}' is missing at {path} — run with HARBOR_UPDATE_GOLDEN=1 to create it");
        string committed = Normalize(await File.ReadAllTextAsync(path));
        await Assert.That(expected.TrimEnd('\n')).IsEqualTo(committed.TrimEnd('\n'))
            .Because(
                $"renderer output for '{name}' drifted from the committed golden frame. "
                + "If the visual change is intentional, regenerate with HARBOR_UPDATE_GOLDEN=1 "
                + "and review the golden diff in the PR.");
    }

    /// <summary>
    ///     Walks up from the test host base directory to locate the test
    ///     project directory (by its .csproj marker), then the
    ///     <c>GoldenFrames</c> folder inside it.
    /// </summary>
    private static string GoldenPath(string name)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && Directory.GetFiles(dir.FullName, "*.csproj").Length == 0)
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Test project directory not found — cannot resolve the GoldenFrames location.");
        }

        return Path.Combine(dir.FullName, "GoldenFrames", $"{name}.golden.txt");
    }

    /// <summary>
    ///     Normalizes environment-dependent line endings so goldens are stable
    ///     across Windows/Linux test hosts.
    /// </summary>
    public static string Normalize(string frame) =>
        frame.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
}
