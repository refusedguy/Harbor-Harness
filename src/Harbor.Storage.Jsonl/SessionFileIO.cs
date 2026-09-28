// SessionFileIO.cs — crash-safe rewrite + timestamp helpers for the JSONL store.
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). Pure file mechanics; no session semantics.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
    ///     Encode one JSONL line: source-generated serialization straight to UTF-8
    ///     bytes plus the trailing newline (#177 — no intermediate string, no
    ///     <c>Serialize + newline</c> concat).
    /// </summary>
    internal static byte[] EncodeLine<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var line = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, line, 0, payload.Length);
        line[payload.Length] = (byte)'\n';
        return line;
    }

    /// <summary>
    ///     Crash-safe rewrite of <paramref name="lines" /> plus one pre-serialized
    ///     UTF-8 entry (<c>EncodeLine</c> output): same temp-file +
    ///     atomic-move protocol as <see cref="WriteAllLinesAtomic" />, but the file
    ///     is written line-wise as bytes with explicit LF separators — no
    ///     string join, and no <c>Environment.NewLine</c> surprise (previously a
    ///     rewrite on Windows emitted CRLF while appends always wrote LF; the reader strips CR, so both decode).
    /// </summary>
    internal static void WriteLinesAtomic(string targetPath, IReadOnlyList<string> lines, byte[] lastLine)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(lines[i]);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.WriteByte((byte)'\n');
                }

                fs.Write(lastLine, 0, lastLine.Length);
                fs.WriteByte((byte)'\n');
            }

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
