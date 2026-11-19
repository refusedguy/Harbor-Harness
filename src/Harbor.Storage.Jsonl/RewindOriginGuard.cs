// RewindOriginGuard.cs — three-level "did the origin change?" check for #2659 (U05).
//
// Before a file pre-image is restored, the guard compares the live file with the
// snapshot captured at first touch. Stat (mode + size) is checked first, then
// mtime, then the content hash. Any mismatch is an explicit Conflict: the guard
// never decides to overwrite a user edit. This type is deliberately dependency
// free so it can be used by a later restore slice without changing contracts,
// DI or the public API of any frozen assembly.

namespace Harbor.Storage.Jsonl;

/// <summary>Outcome of comparing a live file with its captured origin.</summary>
public enum RewindGuardOutcome
{
    /// <summary>The file still matches the captured origin.</summary>
    Unchanged,

    /// <summary>The file changed; restoring the pre-image would overwrite an edit.</summary>
    Conflict
}

/// <summary>
///     Result of a rewind origin check. A conflict always carries a reason that
///     names the level which failed, so callers can report it instead of
///     silently overwriting the file.
/// </summary>
public sealed record RewindGuardResult(RewindGuardOutcome Outcome, string? Reason)
{
    /// <summary>A successful check with no reason.</summary>
    public static RewindGuardResult Unchanged { get; } = new(RewindGuardOutcome.Unchanged, null);

    /// <summary>True when the live file no longer matches its captured origin.</summary>
    public bool IsConflict => Outcome == RewindGuardOutcome.Conflict;

    /// <summary>Builds a conflict result naming the level that failed.</summary>
    public static RewindGuardResult Conflict(string reason) => new(RewindGuardOutcome.Conflict, reason);
}

/// <summary>
///     The stat and content identity of a file at the moment its pre-image was
///     captured. Stored alongside the blob hash so the guard can detect edits
///     made after the snapshot.
/// </summary>
public sealed record FileOriginSnapshot(long Size, int Mode, long LastWriteTimeUtcTicks, string ContentHash);

/// <summary>
///     Three-level guard used before restoring a file pre-image. Level one
///     compares size and mode, level two compares the UTC last-write time, and
///     level three compares the SHA-256 content hash. The first mismatch wins
///     and is returned as a <see cref="RewindGuardResult"/> conflict.
/// </summary>
public sealed class RewindOriginGuard
{
    /// <summary>Captures the current stat and content identity of a file.</summary>
    public static FileOriginSnapshot Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("Cannot capture an origin for a missing file.", path);

        return new FileOriginSnapshot(
            info.Length,
            CaptureMode(path),
            info.LastWriteTimeUtc.Ticks,
            RewindBlobStore.ComputeHash(File.ReadAllBytes(path)));
    }

    /// <summary>
    ///     Checks a live file against a captured snapshot. A missing file and a
    ///     mismatch at any of the three levels yields an explicit conflict.
    /// </summary>
    public RewindGuardResult Check(string path, FileOriginSnapshot expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expected);

        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
            return RewindGuardResult.Conflict("origin file no longer exists");

        // Level 1: stat — mode and size.
        if (info.Length != expected.Size)
            return RewindGuardResult.Conflict($"size changed from {expected.Size} to {info.Length}");
        int mode = CaptureMode(path);
        if (mode != expected.Mode)
            return RewindGuardResult.Conflict($"mode changed from {expected.Mode} to {mode}");

        // Level 2: mtime.
        long ticks = info.LastWriteTimeUtc.Ticks;
        if (ticks != expected.LastWriteTimeUtcTicks)
            return RewindGuardResult.Conflict("last-write time changed since snapshot");

        // Level 3: content hash, reusing U01's canonical hasher.
        string hash = RewindBlobStore.ComputeHash(File.ReadAllBytes(path));
        if (!string.Equals(hash, expected.ContentHash, StringComparison.Ordinal))
            return RewindGuardResult.Conflict("content hash changed since snapshot");

        return RewindGuardResult.Unchanged;
    }

    private static int CaptureMode(string path) =>
        OperatingSystem.IsWindows()
            ? (int)File.GetAttributes(path)
            : (int)File.GetUnixFileMode(path);
}
