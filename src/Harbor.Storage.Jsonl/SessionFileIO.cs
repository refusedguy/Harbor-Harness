// SessionFileIO.cs — crash-safe rewrite + timestamp helpers for the JSONL store.
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). Pure file mechanics; no session semantics.

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Low-level JSONL file mechanics: atomic full-file rewrites and the
///     legacy fallback for the last-activity timestamp.
/// </summary>
internal static class SessionFileIO
{
    /// <summary>
    ///     Issue #83: crash-safe full-file rewrite. Content goes to a temp
    ///     file in the SAME directory, then <c>File.Move(overwrite: true)</c>
    ///     renames it over the target — same-volume rename is atomic, so a
    ///     crash leaves either the old file or the new file, never a
    ///     half-written one. A leftover temp is removed on failure.
    /// </summary>
    internal static void WriteAllLinesAtomic(string targetPath, IReadOnlyList<string> lines)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(tempPath, lines);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort temp cleanup; the original exception below is what matters.
            }
            throw;
        }
    }

    /// <summary>
    ///     Real last-activity timestamp for a session. Legacy files written
    ///     before the header carried <c>updatedAt</c> fall back to the file's
    ///     last-write time so ordering stays stable across consecutive reads.
    /// </summary>
    internal static DateTimeOffset ResolveUpdatedAt(SessionHeaderEntry header, string sessionFile) =>
        header.UpdatedAt != default ? header.UpdatedAt : File.GetLastWriteTimeUtc(sessionFile);
}
