namespace Harbor.Tools.Builtin;
/// <summary>
///     Refuses file writes whose target or any ancestor directory below the workspace root
///     is a symbolic link (symlink-attack guard for write-style tools).
/// </summary>
/// <remarks>
///     <para>
///         A symlink placed inside the workspace (by the model via bash, or pre-planted) can
///         redirect a permitted write to an arbitrary location such as <c>/etc/cron.d</c>. This
///         guard inspects the final path component and every ancestor directory strictly below
///         the workspace root; any symlink found is a refusal. The workspace root itself and
///         directories above it are not inspected — those reflect the user's environment, not
///         model-controlled state.
///     </para>
///     <para>
///         Purely local checks, no I/O beyond metadata reads. Not a TOCTOU-proof guarantee:
///         a concurrent process could swap a link between the check and the write.
///     </para>
/// </remarks>
public static class SymlinkGuard
{
    /// <summary>
    ///     Checks <paramref name="path" /> and its ancestors below the workspace root for symlinks.
    /// </summary>
    /// <param name="path">Absolute, already-normalized target path.</param>
    /// <param name="workspaceRoot">Absolute workspace root; ancestors at or above it are not inspected.</param>
    /// <returns>Success when safe; failure with a user-facing reason otherwise.</returns>
    public static Result Check(string path, string workspaceRoot)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
            return Result.Failure(
                $"Refusing to write: '{path}' is a symlink to '{file.LinkTarget}'. " +
                "Remove the link or ask the user to approve the real destination.");

        string root = Path.GetFullPath(workspaceRoot);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(dir))
        {
            if (IsAtOrAboveWorkspace(dir, root))
                return Result.Success();

            if (new DirectoryInfo(dir).LinkTarget is not null)
                return Result.Failure(
                    $"Refusing to write: '{dir}' is a symbolic link. " +
                    "Writing through it would escape its permission-rule path.");

            dir = Path.GetDirectoryName(dir);
        }

        return Result.Success();
    }

    /// <summary>
    ///     Checks <paramref name="path" /> against the process working directory as workspace root.
    /// </summary>
    public static Result Check(string path) => Check(path, Environment.CurrentDirectory);

    /// <summary>
    ///     Returns true when the raw path string contains a <c>..</c> segment
    ///     (parent-directory traversal). Both '/' and '\\' are treated as
    ///     separators so the check behaves identically on every platform.
    /// </summary>
    public static bool ContainsTraversalSegments(string path)
    {
        int segmentStart = 0;
        int length = path.Length;
        for (int i = 0; i <= length; i++)
        {
            bool isSeparator = i == length || path[i] == '/' || path[i] == '\\';
            if (!isSeparator)
                continue;

            if (i - segmentStart == 2 && path[segmentStart] == '.' && path[segmentStart + 1] == '.')
                return true;

            segmentStart = i + 1;
        }

        return false;
    }

    private static bool IsAtOrAboveWorkspace(string dir, string root)
    {
        if (string.Equals(dir, root, StringComparison.Ordinal))
            return true;

        // A2 fix: "at or above" means dir == root OR dir is an ANCESTOR of
        // root. The previous implementation compared `root` against its own
        // prefix (always false), so only exact equality stopped the walk — and
        // with a trailing separator on workspaceRoot even that failed, making
        // the guard skip ALL inspection. Ancestors strictly below root do not
        // match either branch here, so they keep being inspected.
        string dirPrefix = dir.EndsWith(Path.DirectorySeparatorChar)
            ? dir
            : dir + Path.DirectorySeparatorChar;
        return root.StartsWith(dirPrefix, StringComparison.Ordinal);
    }
}
