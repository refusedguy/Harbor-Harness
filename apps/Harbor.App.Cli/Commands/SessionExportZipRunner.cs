using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Core of <c>harbor sessions export-zip</c>: writes one session as a
///     ZIP archive — the canonical log, the attachments and the manifest that
///     says which format wrote them (#1750).
/// </summary>
/// <remarks>
///     <para>
///         The archive is written to a TEMP file beside the target and moved
///         into place only after the writer has finished. A half-written
///         archive under the name the user asked for is worse than no archive
///         at all: it looks like a finished export, and it is not one.
///     </para>
///     <para>
///         The path the user names is honoured as given; the temp file lives
///         in the same directory so the move is a rename on one filesystem
///         rather than a copy across two.
///     </para>
/// </remarks>
internal static class SessionExportZipRunner
{
    /// <summary>
    ///     Write the archive of one session.
    /// </summary>
    /// <param name="output">Where the summary line goes.</param>
    /// <param name="error">Where the diagnostic goes.</param>
    /// <param name="store">The store to read the session from.</param>
    /// <param name="writer">The archive writer.</param>
    /// <param name="sessionId">The session to export.</param>
    /// <param name="path">Where the archive is written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A process exit code.</returns>
    internal static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        ISessionStore store,
        SessionArchiveWriter writer,
        string sessionId,
        string path,
        CancellationToken ct = default)
    {
        // The temp file's name is only known once the target's directory is,
        // and the directory is only known once the path is resolved — so the
        // resolution happens inside the try, and the scratch file's name is
        // carried out of it for the cleanup.
        string temp = string.Empty;
        try
        {
            string target = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(target) ?? Directory.GetCurrentDirectory();
            temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

            Directory.CreateDirectory(directory);

            SessionArchiveManifest manifest;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var written = await writer.WriteAsync(store, sessionId, stream, ct).ConfigureAwait(false);
                if (written.IsFailure)
                {
                    await error.WriteLineAsync($"Export failed: {written.Error}").ConfigureAwait(false);
                    return 1;
                }

                manifest = written.Value;
            }

            File.Move(temp, target, overwrite: true);
            await output.WriteLineAsync(
                    $"Exported session {sessionId} → {path}: {manifest.LogEntries} record(s) "
                    + $"({manifest.CurrentEntries} current, {manifest.ShadowedEntries} shadowed), "
                    + $"{manifest.Attachments.Length} attachment(s), log source '{manifest.LogSource}'.")
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException)
        {
            await error.WriteLineAsync($"Export failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            // The temp file is the export's scratch space; it never outlives
            // the call, whether the export succeeded or not.
            try
            {
                if (temp.Length > 0 && File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // A leftover temp file is noise, not a failed export.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
