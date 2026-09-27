namespace Harbor.Application.Permissions;

/// <summary>
///     Shared path-normalization behind <see cref="IPathExtractionPolicy" />
///     implementations (moved verbatim out of <see cref="PermissionService" />,
///     issue #178). Relative paths resolve against the workspace root; the
///     normalized path is expressed relative to the root while it stays inside
///     it, and as an absolute path otherwise.
/// </summary>
public static class PermissionPathNormalizer
{
    /// <summary>
    ///     Normalizes a raw tool <c>path</c> argument into its rule-matching form.
    /// </summary>
    /// <param name="raw">The raw argument value (<see langword="null" /> when absent).</param>
    /// <param name="workspaceRoot">The workspace root paths resolve against.</param>
    public static PathExtraction Normalize(string? raw, string workspaceRoot)
    {
        // No usable path argument: fall back to the legacy wildcard (rule matching decides).
        if (string.IsNullOrWhiteSpace(raw) || raw == "*")
            return new PathExtraction("*", false);

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(raw)
                ? raw
                : Path.Combine(workspaceRoot, raw));
        }
        catch
        {
            // Unresolvable path: do not match path-anchored rules; force user decision.
            return new PathExtraction("*", true);
        }

        if (IsInsideWorkspace(full, workspaceRoot))
            return new PathExtraction(RelativeToWorkspace(full, workspaceRoot), false);

        return new PathExtraction(full, true);
    }

    private static bool IsInsideWorkspace(string full, string root)
    {
        if (string.Equals(full, root, StringComparison.Ordinal))
            return true;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string RelativeToWorkspace(string full, string root)
    {
        if (full.Length == root.Length)
            return string.Empty;
        int start = root.Length;
        if (full[start] == Path.DirectorySeparatorChar || full[start] == Path.AltDirectorySeparatorChar)
            start++;
        return full[start..];
    }
}
