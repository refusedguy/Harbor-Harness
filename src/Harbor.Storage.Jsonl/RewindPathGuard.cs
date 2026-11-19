// RewindPathGuard.cs — path containment and symlink guard for #2659 (U06).
//
// A rewind restores stored paths verbatim, so the path itself is untrusted
// input. This guard resolves a stored path against the workspace root and
// rejects absolute paths, parent-directory segments, escapes from the root and
// symlinks below the root before the caller writes anything to disk. Like U01
// and U05 it is deliberately dependency free: no contracts, DI or public API
// of a frozen assembly changes, and a later restore slice can call it directly.

namespace Harbor.Storage.Jsonl;

/// <summary>Outcome of validating a stored rewind path.</summary>
public enum RewindPathGuardOutcome
{
    /// <summary>The path is contained in the workspace and safe to restore.</summary>
    Safe,

    /// <summary>The path would escape the workspace through the root or a symlink.</summary>
    Rejected
}

/// <summary>
///     Result of a rewind path check. A rejection always carries a reason that
///     names the violated rule, so callers can report it instead of writing
///     through an unsafe path.
/// </summary>
public sealed record RewindPathGuardResult(RewindPathGuardOutcome Outcome, string? Reason)
{
    /// <summary>Builds a successful check with no reason.</summary>
    public static RewindPathGuardResult Safe { get; } = new(RewindPathGuardOutcome.Safe, null);

    /// <summary>True when the path must not be restored.</summary>
    public bool IsRejected => Outcome == RewindPathGuardOutcome.Rejected;

    /// <summary>Builds a rejection result naming the rule that failed.</summary>
    public static RewindPathGuardResult Reject(string reason) => new(RewindPathGuardOutcome.Rejected, reason);
}

/// <summary>
///     Validates stored file paths before a rewind writes them back. The path
///     must be relative to a workspace root. An absolute path, a <c>..</c>
///     segment, a resolved path outside the root, or a symlink on the path or
///     any ancestor strictly below the root is rejected.
/// </summary>
/// <remarks>
///     This is a metadata-only check. It is not a TOCTOU proof: a concurrent
///     process could swap a link between the check and the write. It closes the
///     gap where a stored absolute path is written back without validation.
/// </remarks>
public sealed class RewindPathGuard
{
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly StringComparison _comparison;

    /// <summary>Creates a guard rooted at the given absolute workspace directory.</summary>
    public RewindPathGuard(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        _comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <summary>The normalized workspace root the guard confines paths to.</summary>
    public string Root => _root;

    /// <summary>
    ///     Checks a stored path against the workspace root. Returns a safe
    ///     result only when the resolved path stays under the root and neither
    ///     the file nor any ancestor below the root is a symbolic link.
    /// </summary>
    public RewindPathGuardResult Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RewindPathGuardResult.Reject("rewind path is empty");

        if (Path.IsPathRooted(path))
            return RewindPathGuardResult.Reject($"rewind path must be relative to the workspace root: '{path}'");

        if (ContainsTraversalSegments(path))
            return RewindPathGuardResult.Reject($"rewind path contains a parent-directory segment: '{path}'");

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return RewindPathGuardResult.Reject($"rewind path is invalid: '{path}'");
        }

        if (!full.StartsWith(_rootPrefix, _comparison))
            return RewindPathGuardResult.Reject($"rewind path escapes the workspace root: '{path}'");

        return CheckSymlinks(full);
    }

    /// <summary>True when the raw path contains a <c>..</c> segment.</summary>
    public static bool ContainsTraversalSegments(string path)
    {
        int segmentStart = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            bool isSeparator = i == path.Length || path[i] == '/' || path[i] == '\\';
            if (!isSeparator)
                continue;

            if (i - segmentStart == 2 && path[segmentStart] == '.' && path[segmentStart + 1] == '.')
                return true;

            segmentStart = i + 1;
        }

        return false;
    }

    private RewindPathGuardResult CheckSymlinks(string full)
    {
        var file = new FileInfo(full);
        if (file.LinkTarget is not null)
            return RewindPathGuardResult.Reject(
                $"rewind path is a symlink: '{full}' -> '{file.LinkTarget}'");

        string? dir = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir))
        {
            if (IsAtOrAboveRoot(dir))
                break;

            var directory = new DirectoryInfo(dir);
            if (directory.LinkTarget is not null)
                return RewindPathGuardResult.Reject(
                    $"rewind path traverses a symlinked directory: '{dir}' -> '{directory.LinkTarget}'");

            dir = Path.GetDirectoryName(dir);
        }

        return RewindPathGuardResult.Safe;
    }

    private bool IsAtOrAboveRoot(string dir)
    {
        if (string.Equals(dir, _root, _comparison))
            return true;

        string dirPrefix = dir.EndsWith(Path.DirectorySeparatorChar)
            ? dir
            : dir + Path.DirectorySeparatorChar;
        return _root.StartsWith(dirPrefix, _comparison);
    }
}
