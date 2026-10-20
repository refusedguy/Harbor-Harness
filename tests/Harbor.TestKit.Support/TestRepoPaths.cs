namespace Harbor.TestKit;

/// <summary>
///     Repository layout helpers: where the checkout is, and how to build a
///     path inside it.
/// </summary>
/// <remarks>
///     <para>
///         The walk-up this replaces was written out six times in one test
///         project and again in four others — the same loop, the same marker
///         file, four different error messages. A copy that drifts is a copy
///         that finds a different root, and "the fixture was not found" is a
///         much worse failure message than "the repository root was not found
///         above …".
///     </para>
///     <para>
///         The root is found by walking up from <see cref="AppContext.BaseDirectory" />
///         (the test host's <c>bin/</c> directory) until a directory holding
///         <c>Harbor.slnx</c> is found. The result is memoised: the tree does
///         not move while a test run is in progress, and a rule that walks the
///         repository many times should not re-walk it many times.
///     </para>
/// </remarks>
public static class RepoPaths
{
    /// <summary>
    ///     The solution file that marks the repository root.
    /// </summary>
    private const string RootMarker = "Harbor.slnx";

    /// <summary>
    ///     MSBuild is case-insensitive on Windows and case-sensitive on Linux;
    ///     a probe for one spelling therefore misses a checkout whose solution
    ///     file is upper-cased. Probe both.
    /// </summary>
    private static readonly string[] RootMarkers = [RootMarker, "Harbor.SLNX"];

    private static readonly Lazy<string> CachedRoot = new(FindRepoRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     Absolute path of the repository root.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The marker file was not found above <see cref="AppContext.BaseDirectory" />.
    ///     A published or trimmed test host has no checkout above it, and the
    ///     message says so instead of letting the caller fail later with an
    ///     unrelated "file not found".
    /// </exception>
    public static string Root => CachedRoot.Value;

    /// <summary>
    ///     Tries to locate the repository root without throwing.
    /// </summary>
    /// <param name="root">
    ///     The absolute path of the repository root, or <see langword="null" />
    ///     when the host is not running out of a checkout.
    /// </param>
    /// <returns><see langword="true" /> when the root was found.</returns>
    public static bool TryGetRoot(out string? root)
    {
        try
        {
            root = CachedRoot.Value;
            return true;
        }
        catch (InvalidOperationException)
        {
            root = null;
            return false;
        }
    }

    /// <summary>
    ///     Combines path segments under the repository root.
    /// </summary>
    /// <param name="parts">Path segments, relative to the repository root.</param>
    public static string Combine(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (string marker in RootMarkers)
            {
                if (File.Exists(Path.Combine(dir.FullName, marker)))
                {
                    return dir.FullName;
                }
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Repository root (" + RootMarker + ") not found above " + AppContext.BaseDirectory
            + ". The test host is not running out of a checkout — the fixture this test reads "
            + "cannot exist.");
    }
}
