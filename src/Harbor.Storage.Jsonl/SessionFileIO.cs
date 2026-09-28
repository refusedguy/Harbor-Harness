// SessionFileIO.cs — crash-safe rewrite + timestamp helpers for the JSONL store.
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). Pure file mechanics; no session semantics.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Low-level JSONL file mechanics: atomic rewrites and the legacy fallback
///     for the last-activity timestamp.
/// </summary>
internal static class SessionFileIO
{
    /// <summary>
    ///     Crash-safe streaming rewrite (#460). The source is copied to a temp
    ///     sibling one record at a time — each record's verdict coming from
    ///     <paramref name="plan" /> — and the temp is renamed over the target,
    ///     so a crash leaves either the old file or the new one, never a
    ///     half-written one, exactly as before.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     The point of the rewrite: peak memory is one pooled block plus the
    ///     longest record (see <see cref="ChunkedLineReader" />), where
    ///     <c>File.ReadAllLines(...).ToList()</c> was two full copies of the
    ///     session for a change that touches a handful of records. Records are
    ///     copied as raw UTF-8 and terminated with an explicit LF, so no record
    ///     is decoded to a <see cref="string" /> and re-encoded, and a rewrite
    ///     no longer follows <see cref="Environment.NewLine" /> the way
    ///     <c>WriteAllLines</c> did — on Windows a rewrite used to emit CRLF
    ///     where every append emitted LF. The reader strips CR, so both
    ///     decode, but the file is now uniformly LF.
    /// </para>
    /// <para>
    ///     Returns false with the original file untouched when the plan never
    ///     found its target or had nothing to change; a leftover temp is
    ///     removed. <paramref name="trailer" /> is written after the last
    ///     record, if given.
    /// </para>
    /// </remarks>
    internal static bool RewriteRecordsAtomic(
        string targetPath,
        SessionRewritePlan plan,
        byte[]? trailer = null)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        bool committed = false;
        try
        {
            using (var source = new FileStream(
                       targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024))
            using (var sink = new FileStream(
                       tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024))
            {
                StreamRecords(source, sink, plan, trailer);
            }

            if (!plan.Found || !plan.Changed)
            {
                // Nothing to do: leave the original alone and drop the copy.
                return false;
            }

            File.Move(tempPath, targetPath, overwrite: true);
            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemp(tempPath);
            }
        }
    }

    /// <summary>
    ///     Best-effort removal of a leftover temp. Swallows IO and permission
    ///     errors: when this runs, the original exception — or the deliberate
    ///     "nothing to do" return — is the one that matters.
    /// </summary>
    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; swallow.
        }
    }

    /// <summary>
    ///     Copies record by record from <paramref name="source" /> to
    ///     <paramref name="sink" />. Synchronous on purpose: the verdict is
    ///     decided over a <c>ReadOnlySpan&lt;byte&gt;</c>, which cannot be a
    ///     local inside an async method, and a rewrite runs under the
    ///     per-session semaphore where parking a thread on a continuation would
    ///     be the wrong trade anyway.
    /// </summary>
    private static void StreamRecords(
        Stream source,
        Stream sink,
        SessionRewritePlan plan,
        byte[]? trailer)
    {
        using var reader = new ChunkedLineReader(source);
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out var record))
            {
                ApplyVerdict(sink, plan, record);
            }
        }

        if (reader.TryGetTrailingRecord(out var trailing))
        {
            ApplyVerdict(sink, plan, trailing);
        }

        if (trailer is not null)
        {
            WriteRecord(sink, trailer);
        }
    }

    /// <summary>Asks the plan about one record and does what it says.</summary>
    private static void ApplyVerdict(Stream sink, SessionRewritePlan plan, ReadOnlySpan<byte> record)
    {
        switch (plan.Decide(record))
        {
            case LineAction.Keep:
                WriteRecord(sink, record);
                break;
            case LineAction.Drop:
                break;
            case LineAction.Replace:
                WriteRecord(sink, plan.Replacement);
                break;
            default:
                throw new System.Diagnostics.UnreachableException(
                    $"{nameof(LineAction)} gained a verdict the rewrite does not implement.");
        }
    }

    /// <summary>
    ///     Writes one record plus an explicit LF separator, straight from the
    ///     source bytes — no decode, no re-encode, no
    ///     <see cref="Environment.NewLine" />.
    /// </summary>
    private static void WriteRecord(Stream sink, ReadOnlySpan<byte> record)
    {
        sink.Write(record);
        sink.WriteByte((byte)'\n');
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
    ///     Real last-activity timestamp for a session. Legacy files written
    ///     before the header carried <c>updatedAt</c> fall back to the file's
    ///     last-write time so ordering stays stable across consecutive reads.
    /// </summary>
    internal static DateTimeOffset ResolveUpdatedAt(SessionHeaderEntry header, string sessionFile) =>
        header.UpdatedAt != default ? header.UpdatedAt : File.GetLastWriteTimeUtc(sessionFile);
}
