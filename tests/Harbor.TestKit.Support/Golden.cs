using System.Text;

namespace Harbor.TestKit;

/// <summary>
///     One golden contract for the whole repository (#1776).
/// </summary>
/// <remarks>
///     <para>
///         A golden is a committed snapshot of what a piece of code renders.
///         The tree used to carry SIX implementations of the same three
///         mechanics — find the repository root, compare the actual against
///         the committed baseline (or overwrite it), normalise the line
///         endings — one per lane, each with its own copy of the walk-up to
///         <c>Harbor.slnx</c> and its own spelling of the update flag.
///     </para>
///     <para>
///         This class is the ONE implementation. What a lane keeps is what is
///         genuinely the lane's own: the directory its baselines live in, the
///         shape of its document, and the assertion it wants to fail with.
///     </para>
///     <para>
///         The contract, in full: a baseline lives in the suite's fixture
///         directory and is named <c>&lt;name&gt;.golden.txt</c>; the run
///         rewrites baselines only when <see cref="UpdateEnvironmentVariable" />
///         is <c>1</c>; CI never rewrites them; and a missing baseline fails
///         with the command that seeds it.
///     </para>
/// </remarks>
public static class Golden
{
    /// <summary>
    ///     The environment variable that turns every golden compare into a
    ///     rewrite.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ONE spelling for the whole repository. The renderer lane used
    ///         to read <c>HARBOR_UPDATE_GOLDEN</c> (singular) while the
    ///         <c>goldens</c> workflow sets the plural one — so the flag that
    ///         lane documented silently did nothing, and a regenerated
    ///         baseline was a file nobody asked for.
    ///     </para>
    /// </remarks>
    public const string UpdateEnvironmentVariable = "HARBOR_UPDATE_GOLDENS";

    /// <summary>
    ///     The suffix every committed text baseline carries.
    /// </summary>
    public const string BaselineExtension = ".golden.txt";

    /// <summary>
    ///     The repository root — the directory that holds <c>Harbor.slnx</c>.
    /// </summary>
    public static string Root => RepoPaths.Root;

    /// <summary>
    ///     Whether this run may rewrite baselines.
    /// </summary>
    /// <remarks>
    ///     Read on every call rather than cached: the flag belongs to the
    ///     process, and a test that wants to exercise the update path sets it
    ///     for the duration of one test.
    /// </remarks>
    public static bool IsUpdateMode =>
        Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) == "1";

    /// <summary>
    ///     Normalises line endings, so a baseline written on a Windows
    ///     checkout and one written on a Linux one compare equal.
    /// </summary>
    /// <param name="text">The text to normalise.</param>
    /// <returns>The text with CRLF and lone CR turned into LF.</returns>
    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>
    ///     The fixture directory of a suite: <c>tests/fixtures/&lt;suite&gt;</c>
    ///     under the repository root.
    /// </summary>
    /// <param name="suite">The suite's directory name, e.g. <c>celldiff</c>.</param>
    public static string FixtureDir(string suite) =>
        Path.Combine(Root, "tests", "fixtures", suite);

    /// <summary>
    ///     The path of a suite's baseline.
    /// </summary>
    /// <param name="fixtureDir">The suite's fixture directory.</param>
    /// <param name="name">The baseline's name, without the extension.</param>
    public static string BaselinePath(string fixtureDir, string name) =>
        Path.Combine(fixtureDir, name + BaselineExtension);

    /// <summary>
    ///     Compares <paramref name="actual" /> with the committed baseline and
    ///     returns the EXPECTED text: the baseline when there is one, the
    ///     actual when the run is in update mode.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The comparison is byte-for-byte AFTER the line-ending
    ///         normalisation, and the normalisation is deliberately not
    ///         "improved": the committed baselines were written under it, and
    ///         a laxer comparison would let a drifted baseline pass.
    ///     </para>
    /// </remarks>
    /// <param name="fixtureDir">The suite's fixture directory.</param>
    /// <param name="name">The baseline's name, without the extension.</param>
    /// <param name="actual">What the code just produced.</param>
    /// <returns>The expected text.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The baseline does not exist and the run is not in update mode.
    /// </exception>
    public static string VerifyText(string fixtureDir, string name, string actual)
    {
        string normalized = Normalize(actual);
        string path = BaselinePath(fixtureDir, name);

        if (IsUpdateMode)
        {
            WriteBaseline(path, normalized);
            return normalized;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(MissingFixtureMessage(path));
        }

        return Normalize(File.ReadAllText(path));
    }

    /// <summary>
    ///     Writes a baseline, creating its directory when needed.
    /// </summary>
    /// <param name="path">The baseline's path.</param>
    /// <param name="content">The content to write.</param>
    public static void WriteBaseline(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }

    /// <summary>
    ///     The message a missing baseline gets: what is missing, and the
    ///     command that seeds it.
    /// </summary>
    /// <param name="path">The path of the baseline that is missing.</param>
    public static string MissingFixtureMessage(string path) =>
        $"golden fixture missing: {path} (run once with {UpdateEnvironmentVariable}=1 to seed it, "
        + "or dispatch the `goldens` workflow)";

    /// <summary>
    ///     The first difference between two snapshots, with a little context —
    ///     the shape the public-API snapshot gate prints.
    /// </summary>
    /// <param name="expected">The committed text.</param>
    /// <param name="actual">The text the code just produced.</param>
    public static string DescribeDifference(string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        int count = Math.Max(expectedLines.Length, actualLines.Length);

        for (int i = 0; i < count; i++)
        {
            string expectedLine = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            string actualLine = i < actualLines.Length ? actualLines[i] : "<missing>";
            if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.Append("  first difference at line ").Append(i + 1).Append(":\n");
            sb.Append("    baseline: ").Append(expectedLine.TrimEnd('\r')).Append('\n');
            sb.Append("    actual:  ").Append(actualLine.TrimEnd('\r')).Append('\n');
            sb.Append("  (baseline ").Append(expectedLines.Length)
              .Append(" lines, actual ").Append(actualLines.Length).Append(" lines)");
            return sb.ToString();
        }

        return "  the snapshots differ only in their line endings.";
    }
}
