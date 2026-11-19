// FileHistoryTracker.cs — pre-image capture point for #2659 (U02).
//
// A write tool calls TrackBeforeWrite(path) BEFORE it applies an edit. The
// tracker reads the current bytes, stores them as a content-addressed
// pre-image and records the path with first-touch-wins dedup: the first touch
// of a file in a turn fixes the pre-image, later touches in the same turn do
// not create another version. A path that does not exist yet is recorded as a
// tombstone, so a later rewind deletes the file the agent created instead of
// restoring a blob. One tracker instance is one turn; the caller owns its
// lifetime. Like the U01/U05/U06/U04 slices this type is dependency free: no
// contract, DI, public-API, gate or threshold changes are needed to use it.

namespace Harbor.Storage.Jsonl;

/// <summary>Outcome of one pre-image capture attempt.</summary>
public enum FileHistoryTrackOutcome
{
    /// <summary>The pre-image was captured and recorded for the first time.</summary>
    Captured,

    /// <summary>The path was already recorded in this turn; nothing was captured.</summary>
    AlreadyTracked,

    /// <summary>The path was rejected by the guard; nothing was captured or written.</summary>
    Rejected
}

/// <summary>
///     Result of a pre-image capture. A rejection always carries the reason
///     from the path guard so the caller can report it instead of silently
///     skipping the capture.
/// </summary>
public sealed record FileHistoryTrackResult(
    FileHistoryTrackOutcome Outcome,
    FilePreImage? PreImage,
    string? Reason)
{
    /// <summary>Builds a first-touch capture result.</summary>
    public static FileHistoryTrackResult Captured(FilePreImage preImage) =>
        new(FileHistoryTrackOutcome.Captured, preImage, null);

    /// <summary>Builds a deduplicated result carrying the first touch's pre-image.</summary>
    public static FileHistoryTrackResult AlreadyTracked(FilePreImage preImage) =>
        new(FileHistoryTrackOutcome.AlreadyTracked, preImage, null);

    /// <summary>Builds a rejection result naming the guard rule that failed.</summary>
    public static FileHistoryTrackResult Rejected(string reason) =>
        new(FileHistoryTrackOutcome.Rejected, null, reason);

    /// <summary>True when this call captured a new pre-image.</summary>
    public bool IsCaptured => Outcome == FileHistoryTrackOutcome.Captured;

    /// <summary>True when the path was rejected and no state changed.</summary>
    public bool IsRejected => Outcome == FileHistoryTrackOutcome.Rejected;
}

/// <summary>
///     Pre-image of one workspace-relative path. <c>BlobHash</c> is the
///     SHA-256 object name of the bytes the file held before the first write;
///     a null hash means the file did not exist and this is a tombstone.
///     <c>ExistedBefore</c> repeats that fact explicitly for metadata writers.
///     <c>Version</c> is 1 because dedup keeps one pre-image per file per turn;
///     a later slice persisting across turns may bump it.
/// </summary>
public sealed record FilePreImage(
    string Path,
    string? BlobHash,
    bool ExistedBefore,
    int Version = 1)
{
    /// <summary>True when this pre-image records a file the agent created.</summary>
    public bool IsTombstone => BlobHash is null;
}

/// <summary>
///     Captures file pre-images before a write is applied. Paths are validated
///     with U06's path guard before any filesystem read, deduplicated by path,
///     and stored through U01's content-addressed blob store. The captured
///     pre-images are exposed in first-touch order for a later slice to persist
///     in the session log and feed the restore engine.
/// </summary>
public sealed class FileHistoryTracker
{
    private readonly string _root;
    private readonly RewindBlobStore _blobs;
    private readonly RewindPathGuard _pathGuard;
    private readonly Dictionary<string, FilePreImage> _preImages = new(StringComparer.Ordinal);

    /// <summary>Creates a tracker for a workspace root and its pre-image store.</summary>
    public FileHistoryTracker(string workspaceRoot, RewindBlobStore blobs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(blobs);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _blobs = blobs;
        _pathGuard = new RewindPathGuard(_root);
    }

    /// <summary>The normalized workspace root this tracker confines paths to.</summary>
    public string Root => _root;

    /// <summary>Captured pre-images in first-touch order.</summary>
    public IReadOnlyList<FilePreImage> PreImages => _preImages.Values.ToArray();

    /// <summary>The number of distinct paths captured in this turn.</summary>
    public int Count => _preImages.Count;

    /// <summary>
    ///     Captures the pre-image of a workspace-relative path before it is
    ///     written. Unsafe paths are rejected before any disk access; a path
    ///     already captured in this turn returns its first-touch pre-image.
    /// </summary>
    public FileHistoryTrackResult TrackBeforeWrite(string relativePath)
    {
        var check = _pathGuard.Check(relativePath);
        if (check.IsRejected)
            return FileHistoryTrackResult.Rejected(check.Reason!);

        if (_preImages.TryGetValue(relativePath, out var existing))
            return FileHistoryTrackResult.AlreadyTracked(existing);

        string full = Path.Combine(_root, relativePath);
        string? hash;
        bool existedBefore;
        if (File.Exists(full))
        {
            hash = _blobs.Store(File.ReadAllBytes(full));
            existedBefore = true;
        }
        else
        {
            hash = null;
            existedBefore = false;
        }

        var preImage = new FilePreImage(relativePath, hash, existedBefore);
        _preImages.Add(relativePath, preImage);
        return FileHistoryTrackResult.Captured(preImage);
    }
}
