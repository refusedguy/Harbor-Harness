// FileRewinder.cs — restore slice for #2659 (U03/U04 + restore).
//
// A rewind touches only the paths recorded when the pre-images were captured;
// it never walks the workspace. Before any write, the stored path is validated
// by U06's path guard and the live file is compared with the baseline captured
// at rewind-request time by U05's origin guard. A mismatch is an explicit
// Conflict: a user edit made after the checkpoint is never overwritten by a
// pre-image. A null blob hash is a tombstone and deletes a file the agent
// created. Like U01/U05/U06 this type is dependency free: no contract, DI,
// public-API, gate or threshold changes are needed to use it.

namespace Harbor.Storage.Jsonl;

/// <summary>Outcome of restoring one recorded file path.</summary>
public enum RewindRestoreOutcome
{
    /// <summary>The pre-image was restored, or a tombstone file was deleted.</summary>
    Restored,

    /// <summary>The live file changed since the checkpoint; it was left untouched.</summary>
    Conflict,

    /// <summary>The stored path was rejected by the path guard; nothing was written.</summary>
    Rejected,

    /// <summary>The referenced pre-image blob is absent; nothing was written.</summary>
    MissingBlob
}

/// <summary>
///     Result of restoring one path. A non-restored outcome always carries a
///     reason so callers can report it instead of silently skipping the file.
/// </summary>
public sealed record RewindRestoreResult(RewindRestoreOutcome Outcome, string? Reason)
{
    /// <summary>A successful restore with no reason.</summary>
    public static RewindRestoreResult Restored { get; } = new(RewindRestoreOutcome.Restored, null);

    /// <summary>True when the file was not changed on disk.</summary>
    public bool IsRestored => Outcome == RewindRestoreOutcome.Restored;

    /// <summary>Builds a non-restored result naming why the file was skipped.</summary>
    public static RewindRestoreResult Failed(RewindRestoreOutcome outcome, string reason) => new(outcome, reason);
}

/// <summary>
///     One recorded path and the data needed to restore it. <c>Path</c> is
///     workspace-relative. <c>PreImageBlobHash</c> is null for a tombstone: the
///     file did not exist before the agent first wrote it. <c>Baseline</c> is
///     the live file's identity at the moment the rewind was requested; it is
///     what the origin guard protects against user edits made after that point.
/// </summary>
public sealed record FileRewindEntry(
    string Path,
    string? PreImageBlobHash,
    FileOriginSnapshot Baseline,
    int Version = 1)
{
    /// <summary>True when this entry records a file the agent created.</summary>
    public bool IsTombstone => PreImageBlobHash is null;
}

/// <summary>
///     Registry of paths touched by a turn, with first-touch-wins dedup. The
///     first capture of a path fixes its pre-image and baseline; later writes
///     to the same path in the same turn do not create another entry.
/// </summary>
public sealed class RewindTouchedPaths
{
    private readonly Dictionary<string, FileRewindEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>The recorded entries, in first-touch order.</summary>
    public IReadOnlyList<FileRewindEntry> Entries => _entries.Values.ToArray();

    /// <summary>The number of distinct touched paths.</summary>
    public int Count => _entries.Count;

    /// <summary>
    ///     Records a path on first touch and returns the entry that is kept.
    ///     Subsequent calls for the same path are ignored.
    /// </summary>
    public FileRewindEntry Track(FileRewindEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Path))
            throw new ArgumentException("A rewind path must be non-empty.", nameof(entry));

        if (_entries.TryGetValue(entry.Path, out var existing))
            return existing;

        _entries.Add(entry.Path, entry);
        return entry;
    }

    /// <summary>True when the path was already recorded.</summary>
    public bool Contains(string path) => _entries.ContainsKey(path);
}

/// <summary>
///     Restores the pre-images of recorded paths. Each path is checked with
///     U06's path guard and U05's origin guard before anything is written; a
///     failure on one path is reported and does not stop the remaining paths.
/// </summary>
public sealed class FileRewinder
{
    private readonly string _root;
    private readonly RewindBlobStore _blobs;
    private readonly RewindPathGuard _pathGuard;
    private readonly RewindOriginGuard _originGuard = new();

    /// <summary>Creates a rewinder for a workspace root and its pre-image store.</summary>
    public FileRewinder(string workspaceRoot, RewindBlobStore blobs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(blobs);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _blobs = blobs;
        _pathGuard = new RewindPathGuard(_root);
    }

    /// <summary>The normalized workspace root this rewinder confines paths to.</summary>
    public string Root => _root;

    /// <summary>Restores one recorded entry.</summary>
    public RewindRestoreResult Restore(FileRewindEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var pathCheck = _pathGuard.Check(entry.Path);
        if (pathCheck.IsRejected)
            return RewindRestoreResult.Failed(RewindRestoreOutcome.Rejected, pathCheck.Reason!);

        string full = Path.Combine(_root, entry.Path);
        var guard = _originGuard.Check(full, entry.Baseline);
        if (guard.IsConflict)
            return RewindRestoreResult.Failed(RewindRestoreOutcome.Conflict, guard.Reason!);

        if (entry.IsTombstone)
        {
            DeleteTolerant(full);
            return RewindRestoreResult.Restored;
        }

        byte[]? preImage = _blobs.Read(entry.PreImageBlobHash);
        if (preImage is null)
            return RewindRestoreResult.Failed(
                RewindRestoreOutcome.MissingBlob,
                $"pre-image blob not found: '{entry.PreImageBlobHash}'");

        File.WriteAllBytes(full, preImage);
        return RewindRestoreResult.Restored;
    }

    /// <summary>Restores every recorded path, collecting one result per entry.</summary>
    public IReadOnlyList<(string Path, RewindRestoreResult Result)> RestoreAll(RewindTouchedPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var results = new List<(string, RewindRestoreResult)>(paths.Count);
        foreach (var entry in paths.Entries)
            results.Add((entry.Path, Restore(entry)));
        return results;
    }

    private static void DeleteTolerant(string full)
    {
        try
        {
            File.Delete(full);
        }
        catch (DirectoryNotFoundException)
        {
            // A parallel edit already removed the path; the desired end state holds.
        }
        catch (FileNotFoundException)
        {
            // Same as above: idempotent deletion.
        }
    }
}
