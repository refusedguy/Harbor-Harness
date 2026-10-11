// SessionRewritePlans.cs — the three JSONL rewrite shapes, as strategies (#460).
//
// All three used to be spelled the same way: File.ReadAllLines(sessionFile)
// into a List<string>, filter/index the list, then write a temp file and
// rename it over the original. That is two full copies of the session in
// memory, taken while holding the per-session semaphore, for an operation
// that only ever touches a handful of records. Sizing by the file is also what
// made a long session (or a crafted one) decide the peak working set.
//
// A plan is now just a per-record verdict, and SessionFileIO does the copying
// a block at a time. No plan sees more than one record at a time, and none of
// them can see the file length.

using System.Text;

namespace Harbor.Storage.Jsonl;

/// <summary>What a <see cref="SessionRewritePlan" /> does with one record.</summary>
internal enum LineAction : byte
{
    /// <summary>Copy the record through, byte for byte.</summary>
    Keep,

    /// <summary>Leave the record out of the rewritten file.</summary>
    Drop,

    /// <summary>Write <see cref="SessionRewritePlan.Replacement" /> in its place.</summary>
    Replace,
}

/// <summary>
///     Strategy for a streaming, bounded JSONL rewrite. <see cref="Decide" /> is
///     asked once per record, in file order, while
///     <see cref="SessionFileIO.RewriteRecordsAtomic" /> copies the file a
///     block at a time — so the plan's working set is one record, whatever the
///     file weighs.
/// </summary>
/// <remarks>
///     A plan also decides whether the rewrite is worth committing: one that
///     never saw what it was looking for leaves the original file untouched,
///     which is how "message not found" and "an empty session file" stay
///     outcomes rather than rewrites that destroy what they did not match.
/// </remarks>
internal abstract class SessionRewritePlan
{
    /// <summary>True once the plan has seen the record(s) it was looking for.</summary>
    internal bool Found { get; private set; }

    /// <summary>Called by a plan once its target turns up.</summary>
    protected void MarkFound() => Found = true;

    /// <summary>Clears <see cref="Found" />, for a fresh pass over the same file.</summary>
    internal void ClearFound() => Found = false;

    /// <summary>
    ///     True when committing would alter the file. Finding the target
    ///     normally implies that; a plan that can find its target and still
    ///     have nothing to do overrides this.
    /// </summary>
    internal virtual bool Changed => Found;

    /// <summary>
    ///     Bytes to write in place of the record when the verdict is
    ///     <see cref="LineAction.Replace" />. Empty for every other verdict.
    /// </summary>
    internal virtual byte[] Replacement => [];

    /// <summary>Verdict for one record, terminator already stripped.</summary>
    internal abstract LineAction Decide(ReadOnlySpan<byte> record);

    /// <summary>
    ///     Sets <see cref="Found" /> from the file's length alone, without
    ///     reading a record. Returns true when it could decide — the caller then
    ///     skips the dry scan and goes straight to writing. Returns false when
    ///     only the records can answer, and the caller scans.
    /// </summary>
    /// <remarks>
    ///     Default: cannot decide, so the caller scans. This exists so the
    ///     common rewrite (a header rename) does not read the file twice.
    /// </remarks>
    internal virtual bool DecideFromLength(long fileBytes)
    {
        _ = fileBytes;
        return false;
    }

    /// <summary>
    ///     Returns the plan to its just-constructed state, so the same instance
    ///     can be dry-scanned and then walked for real without its counters
    ///     accumulating across the two passes.
    /// </summary>
    internal abstract void Reset();

    /// <summary>
    ///     True when every record after the first is kept verbatim, so the
    ///     rewrite can pipe the rest of the file through without ever
    ///     assembling a record. A plan that inspects records to classify them
    ///     cannot say this. Default: no.
    /// </summary>
    internal virtual bool CopiesRemainderVerbatim => false;
}

/// <summary>
///     Rewrite the session header — the first record — in place. Title, agent,
///     model, status and git-branch edits all land here, so this is the
///     operation the issue is about: it changed one line and paid for the whole
///     file twice. Everything after the header is copied through untouched.
/// </summary>
internal sealed class HeaderRewritePlan : SessionRewritePlan
{
    private readonly byte[] _headerRecord;
    private bool _rewritten;

    internal HeaderRewritePlan(byte[] headerRecord) => _headerRecord = headerRecord;

    internal override byte[] Replacement => _headerRecord;

    /// <summary>
    ///     Answerable from the length alone: any file with at least one byte has
    ///     a first record to replace, and one with none has no header to update.
    ///     So the header rename — the rewrite the issue is about, and the one on
    ///     the interactive path — decides in O(1) and reads the file once.
    /// </summary>
    internal override bool DecideFromLength(long fileBytes)
    {
        if (fileBytes <= 0)
        {
            return true; // decided: no records, so Found stays false
        }

        MarkFound();
        return true;
    }

    /// <summary>
    ///     Everything after the header is kept, always — so the rewrite streams
    ///     the rest of the file straight through. That makes the header rename
    ///     O(1) in memory: not one record of the session is ever assembled,
    ///     however large a single message in it happens to be.
    /// </summary>
    internal override bool CopiesRemainderVerbatim => true;

    internal override void Reset()
    {
        ClearFound();
        _rewritten = false;
    }

    internal override LineAction Decide(ReadOnlySpan<byte> record)
    {
        if (_rewritten)
        {
            return LineAction.Keep;
        }

        // A file with no records at all never reaches here, so Found stays
        // false and the rewrite is abandoned — the header is written by
        // CreateAsync, so an empty file is corruption, not a session to edit.
        _rewritten = true;
        MarkFound();
        return LineAction.Replace;
    }
}

/// <summary>
///     Drop every record that is a <c>"message"</c> entry carrying the given
///     id, leaving the rest alone. Header records and unparseable records are
///     never matched, so the file stays readable.
/// </summary>
internal sealed class DropMessagePlan : SessionRewritePlan
{
    private readonly string _messageId;
    private readonly byte[] _idNeedle;

    internal DropMessagePlan(string messageId)
    {
        _messageId = messageId;
        _idNeedle = Encoding.UTF8.GetBytes($"\"id\":\"{messageId}\"");
    }

    internal override void Reset() => ClearFound();

    internal override LineAction Decide(ReadOnlySpan<byte> record)
    {
        if (!SessionFileReader.IsMessageEntryWithId(record, _messageId, _idNeedle))
        {
            return LineAction.Keep;
        }

        MarkFound();
        return LineAction.Drop;
    }
}

/// <summary>
///     "Rewind to here": keep every record up to and including the one carrying
///     the given id, then drop every further <c>"message"</c> record. Header
///     and non-message records survive wherever they are — except checkpoint
///     markers past the anchor (#1247 slice 1): a checkpoint pointing into
///     the dropped future is stale and goes with the messages it indexes.
///
///     One pass, and it needs to be one: file order IS insertion order for this
///     store (append-only plus rewrite-in-place), so the first id match is the
///     anchor and everything the old index-scan kept or dropped follows from
///     having passed it. Records before the anchor are kept, including message
///     records — that is the point of a rewind.
/// </summary>
internal sealed class DeleteAfterAnchorPlan : SessionRewritePlan
{
    private readonly string _messageId;
    private readonly byte[] _idNeedle;
    private bool _pastAnchor;

    internal DeleteAfterAnchorPlan(string messageId)
    {
        _messageId = messageId;
        _idNeedle = Encoding.UTF8.GetBytes($"\"id\":\"{messageId}\"");
    }

    /// <summary>Message records dropped after the anchor.</summary>
    internal int Removed { get; set; }

    /// <summary>
    ///     Reaching the anchor is not itself a change — the old code only
    ///     rewrote when something was actually removed, so a rewind to the last
    ///     message left the file (and its mtime) alone.
    /// </summary>
    internal override bool Changed => Removed > 0;

    internal override void Reset()
    {
        ClearFound();
        _pastAnchor = false;
        Removed = 0;
    }

    internal override LineAction Decide(ReadOnlySpan<byte> record)
    {
        if (!_pastAnchor)
        {
            if (!SessionFileReader.IsMessageEntryWithId(record, _messageId, _idNeedle))
            {
                return LineAction.Keep;
            }

            _pastAnchor = true;
            MarkFound();
            return LineAction.Keep; // the anchor itself survives
        }

        if (SessionFileReader.IsAnyMessageEntry(record))
        {
            Removed++;
            return LineAction.Drop;
        }

        // #1247 slice 1: a checkpoint past the anchor indexes the dropped
        // future, so it goes too. Pre-anchor checkpoints never reach here.
        // Dropped checkpoints do not count toward Removed — that number
        // reports messages, matching DeleteMessagesAfterAsync's contract —
        // and do not flip Changed: a rewind to the tail message stays a
        // no-op even when a checkpoint line sits after it.
        if (SessionFileReader.IsCheckpointLine(record))
        {
            return LineAction.Drop;
        }

        return LineAction.Keep;
    }
}
