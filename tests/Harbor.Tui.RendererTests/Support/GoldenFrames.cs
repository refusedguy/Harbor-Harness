using Harbor.TestKit;

namespace Harbor.Tui.RendererTests.Support;

/// <summary>
///     Golden-frame persistence for renderer visual regression tests
///     (renderer-unification sprint Phase 5). A golden frame is the captured
///     renderer output for a canonical event stream; a test fails when the
///     captured frame drifts from the committed golden file.
/// </summary>
/// <remarks>
///     <para>
///         Regenerate goldens after an intentional visual change with
///         <c>HARBOR_UPDATE_GOLDENS=1 dotnet run --project
///         tests/Harbor.Tui.RendererTests</c> and commit the diff — the golden
///         diff then documents the visual change in review.
///     </para>
///     <para>
///         The mechanics — where the root is, how the update flag is spelled,
///         how line endings are normalised — live in <see cref="Golden" />
///         since #1776. What stays here is the renderer lane's own: the
///         fixture directory, the trailing-newline tolerance of the compare,
///         and the assertion it fails with.
///     </para>
/// </remarks>
public static class GoldenFrames
{
    /// <summary>
    ///     The fixture directory the renderer's golden frames live in.
    /// </summary>
    private static string FixtureDir => Golden.FixtureDir("renderer-frames");

    /// <summary>
    ///     Compares <paramref name="actual"/> against the committed golden
    ///     file, or regenerates the golden when the run is in update mode.
    /// </summary>
    public static async Task AssertGoldenAsync(string name, string actual)
    {
        string path = Golden.BaselinePath(FixtureDir, name);
        string expected = Golden.Normalize(actual);

        if (Golden.IsUpdateMode)
        {
            Golden.WriteBaseline(path, expected + "\n");
            return;
        }

        await Assert.That(File.Exists(path)).IsTrue()
            .Because($"golden frame '{name}' is missing at {path} — run with {Golden.UpdateEnvironmentVariable}=1 to create it");
        string committed = Golden.Normalize(await File.ReadAllTextAsync(path));
        await Assert.That(expected.TrimEnd('\n')).IsEqualTo(committed.TrimEnd('\n'))
            .Because(
                $"renderer output for '{name}' drifted from the committed golden frame. "
                + "If the visual change is intentional, regenerate with " + Golden.UpdateEnvironmentVariable + "=1 "
                + "and review the golden diff in the PR.");
    }

    /// <summary>
    ///     Normalizes environment-dependent line endings so goldens are stable
    ///     across Windows/Linux test hosts.
    /// </summary>
    public static string Normalize(string frame) => Golden.Normalize(frame);
}
