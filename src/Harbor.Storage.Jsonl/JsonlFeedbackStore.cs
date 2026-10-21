// JsonlFeedbackStore.cs — the feedback axis of the JSONL backend (#1746):
// what the user said about the agent's work, recorded in the session file
// beside the messages and read back from it.
//
// This is the caretaker half of the axis, the same shape the checkpoint axis
// has (#1513): it resolves the session file, decides the session existence
// policy, keeps the on-disk line shapes and scans them back — while the
// store keeps the forwarders, so the ISessionFeedbackStore implementation
// site stays in JsonlSessionStore.
//
// THE FILE FORMAT IS THE POINT
// ----------------------------
// The feedback lives in its OWN lines ("type":"messageRating" /
// "type":"sessionRemark"), never as a field of a message. The message read
// path skips them the way it skips the checkpoint markers, so:
//
//   * the messages the model context is built from carry no feedback — the
//     rule is a property of the format, not a promise;
//   * a session file written before the feedback axis existed reads exactly
//     as it did: there is no feedback line in it, and its absence is not a
//     malformed line.
//
// WHAT IS APPENDED, AND WHAT A CONFLICT IS
// ----------------------------------------
// The axis is append-only, like the rest of the store. A rating of a message
// that already has one is APPENDED, not overwritten: the older rating stays
// in the log, and the effective view folds to the newer one (last write
// wins — the user changed their mind, and the newer word is the one they
// mean). Remarks accumulate; they never conflict.
//
// A rating that names a message the session does not hold is REFUSED before
// anything is written: a rating of a message that is not there is a dangling
// reference, and the store does not create dangling references.
//
// THE SHAPE OF THE RAIL
// ---------------------
// The session file is resolved ONCE per call, on one rail: the resolution and
// the existence policy are a Bind link, and the work hangs off it. The
// expected outcomes (no such session, no such message, a blank remark) are
// decided BEFORE the try boundary, so they stay plain failures — no throw, no
// error log (#199). Only the IO rides the try.

using Harbor.Storage.Shared;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The feedback axis of the JSONL backend (#1746): record what the user
///     said about the agent's work, and read it back.
/// </summary>
internal sealed class JsonlFeedbackStore : ISessionFeedbackStore
{
    /// <summary>
    ///     The store this axis belongs to: its per-session lock strip and
    ///     its parsed-message cache. The axis borrows them, never copies.
    /// </summary>
    private readonly JsonlSessionStore _owner;

    /// <summary>
    ///     Root directory of the session files — the store's own, so a
    ///     feedback write resolves the very same file a message does.
    /// </summary>
    private readonly string _rootDirectory;

    /// <summary>
    ///     Logger of the owning store — the log category stays the store's.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    ///     Bind the axis to its store.
    /// </summary>
    /// <param name="owner">The store that owns the session files.</param>
    /// <param name="rootDirectory">Root directory of the session files.</param>
    /// <param name="logger">Logger of the owning store.</param>
    internal JsonlFeedbackStore(JsonlSessionStore owner, string rootDirectory, ILogger logger)
    {
        _owner = owner;
        _rootDirectory = rootDirectory;
        _logger = logger;
    }

    /// <summary>
    ///     Record a rating of one message: the line is appended under the
    ///     per-session lock, and the parse cache for the session is dropped
    ///     (the file changed).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">The rated message id.</param>
    /// <param name="rating">The value the user gave.</param>
    /// <param name="category">The axis the value was given on.</param>
    /// <param name="note">Optional free-text note from the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded rating, or failure if the session/message is not found.</returns>
    public async Task<Result<MessageRating>> RecordMessageRatingAsync(
        string sessionId,
        string messageId,
        FeedbackRating rating,
        FeedbackCategory category,
        string? note = null,
        CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();

        // The id and the timestamp are the store's to mint, so they are
        // minted here. A write that is refused afterwards simply drops the
        // record it minted — nothing durable is left behind either way.
        var stored = new MessageRating(
            Guid.NewGuid().ToString("N"),
            sessionId,
            messageId,
            rating,
            category,
            DateTimeOffset.UtcNow,
            note);

        return await ResolveSessionFile(sessionId)
            .Bind(sessionFile => AppendRatingAsync(sessionFile, stored, ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Append one rating line under the per-session lock, after the
    ///     checks that decide whether the rating may be written at all.
    /// </summary>
    /// <param name="sessionFile">The session file.</param>
    /// <param name="rating">The rating to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded rating, or failure if the message is not in the session.</returns>
    private async Task<Result<MessageRating>> AppendRatingAsync(
        string sessionFile,
        MessageRating rating,
        CancellationToken ct)
    {
        // #199: the expected outcome is decided BEFORE the try boundary, so
        // it stays a plain failure (no throw, no error log).
        if (string.IsNullOrWhiteSpace(rating.MessageId))
        {
            return Result.Failure<MessageRating>(
                $"A message rating in session '{rating.SessionId}' must name a message.");
        }

        return await Result.Try(async () =>
        {
            var semaphore = await _owner.GetSessionLockAsync(rating.SessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                // The message must be one the session HOLDS: a rating of a
                // message that is not there is a dangling reference, and the
                // write is refused rather than recorded. The check is under
                // the lock, so a rewind cannot drop the message between the
                // check and the append.
                //
                // A property pattern, not `messages.Value`: CFE0001 does not
                // model the early-return guard shape, and the read here sits
                // in a branch below it.
                var messages = await _owner.GetMessagesAsync(rating.SessionId, ct).ConfigureAwait(false);
                if (messages is { IsSuccess: true, Value: var history })
                {
                    if (!HoldsMessage(history, rating.MessageId))
                    {
                        return Result.Failure<MessageRating>(
                            SessionStoreErrors.MessageNotFound(rating.SessionId, rating.MessageId));
                    }
                }
                else
                {
                    return messages.ConvertFailure<MessageRating>();
                }

                byte[] line = SessionFileIO.EncodeLine(
                    // #1740: the note is free text the user typed, and a
                    // credential can be in it as easily as in a tool result.
                    SessionFeedbackMapperKit.ToRow(
                        rating with { Note = _owner.ScrubOptionalTextForWrite(rating.Note) }),
                    JsonlCodecContext.Default.MessageRatingEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
                _owner.InvalidateMessageCache(rating.SessionId);
                return Result.Success(rating);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .Bind(x => x)
            .TapError(e => JsonlSessionStoreLog.RecordMessageRatingFailed(_logger, rating.SessionId, e));
    }

    /// <summary>
    ///     Record a remark about the session: the line is appended under the
    ///     per-session lock, and the parse cache for the session is dropped.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="text">The user's text; it must not be blank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded remark, or failure if the session is not found.</returns>
    public async Task<Result<SessionRemark>> RecordSessionRemarkAsync(
        string sessionId,
        string text,
        CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();

        // The id and the timestamp are the store's to mint (see the rating
        // method above).
        var stored = new SessionRemark(
            Guid.NewGuid().ToString("N"),
            sessionId,
            text,
            DateTimeOffset.UtcNow);

        return await ResolveSessionFile(sessionId)
            .Bind(sessionFile => AppendRemarkAsync(sessionFile, stored, ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Append one remark line under the per-session lock, after the
    ///     checks that decide whether the remark may be written at all.
    /// </summary>
    /// <param name="sessionFile">The session file.</param>
    /// <param name="remark">The remark to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded remark, or failure if the remark has no text.</returns>
    private async Task<Result<SessionRemark>> AppendRemarkAsync(
        string sessionFile,
        SessionRemark remark,
        CancellationToken ct)
    {
        // #199: the expected outcome is decided BEFORE the try boundary.
        if (string.IsNullOrWhiteSpace(remark.Text))
        {
            return Result.Failure<SessionRemark>(
                $"A session remark in session '{remark.SessionId}' must have text.");
        }

        return await Result.Try(async () =>
        {
            var semaphore = await _owner.GetSessionLockAsync(remark.SessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                byte[] line = SessionFileIO.EncodeLine(
                    // #1740: the remark is free text about the session, and it
                    // leaves the machine with the rest of the log.
                    SessionFeedbackMapperKit.ToRow(
                        remark with { Text = _owner.ScrubTextForWrite(remark.Text) }),
                    JsonlCodecContext.Default.SessionRemarkEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
                _owner.InvalidateMessageCache(remark.SessionId);
                return Result.Success(remark);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .Bind(x => x)
            .TapError(e => JsonlSessionStoreLog.RecordSessionRemarkFailed(_logger, remark.SessionId, e));
    }

    /// <summary>
    ///     Read the session's feedback: the ratings in force (the newest
    ///     rating of a message wins) and the remarks in the order they were
    ///     recorded. A session with no feedback yields empty lists.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The feedback, or failure if the session is not found.</returns>
    public Task<Result<SessionFeedback>> GetFeedbackAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();

        return ResolveSessionFile(sessionId)
            .Bind(sessionFile => ReadFeedbackAsync(sessionFile, sessionId, ct));
    }

    /// <summary>
    ///     Scan the session file for the feedback records, under the
    ///     per-session lock.
    /// </summary>
    /// <param name="sessionFile">The session file.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folded feedback of the session.</returns>
    private async Task<Result<SessionFeedback>> ReadFeedbackAsync(
        string sessionFile,
        string sessionId,
        CancellationToken ct)
    {
        return await Result.Try(async () =>
        {
            var semaphore = await _owner.GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                return ReadFeedback(sessionFile, sessionId, _logger);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .TapError(e => JsonlSessionStoreLog.ReadFeedbackFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Resolve the session's file and decide the existence policy: a
    ///     session id that cannot name a file, and a session whose file is
    ///     not there, are both plain failures decided before anything is
    ///     written.
    /// </summary>
    /// <remarks>
    ///     One rail, one home: the three public verbs all resolve the file
    ///     here, so the policy cannot drift between them. The parse cache
    ///     entry for a session whose file is gone is dropped on that path —
    ///     the same thing the store does when a message write finds no
    ///     file.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The session file path, or failure when the session is not there.</returns>
    private Result<string> ResolveSessionFile(string sessionId) =>
        SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId)
            .Bind(path =>
            {
                if (!File.Exists(path))
                {
                    _owner.InvalidateMessageCache(sessionId);
                    return Result.Failure<string>(SessionStoreErrors.SessionNotFound(sessionId));
                }

                return Result.Success(path);
            });

    /// <summary>
    ///     Whether the session's message list holds the given message id.
    /// </summary>
    /// <param name="messages">The session's messages.</param>
    /// <param name="messageId">The id to look for.</param>
    /// <returns>True when the session holds a message with that id.</returns>
    private static bool HoldsMessage(IReadOnlyList<AgentMessage> messages, string messageId)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (string.Equals(messages[i].Id, messageId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Sync byte-level scan of one session file for the feedback lines.
    ///     Sync on purpose: the verdicts run over <c>ReadOnlySpan&lt;byte&gt;</c>
    ///     locals (CS4012), mirroring <see cref="SessionFileIO.Scan" />.
    /// </summary>
    /// <param name="sessionFile">The session file.</param>
    /// <param name="sessionId">The session id (for the rows and the warnings).</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    /// <returns>The folded feedback of the session.</returns>
    private static SessionFeedback ReadFeedback(string sessionFile, string sessionId, ILogger logger)
    {
        var ratings = new List<MessageRating>();
        var ratingSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        var remarks = new List<SessionRemark>();

        using var source = new FileStream(
            sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
        using var reader = new ChunkedLineReader(source);
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out var record))
            {
                FoldFeedbackRecord(record, sessionId, ratings, ratingSlots, remarks, logger);
            }
        }

        if (reader.TryGetTrailingRecord(out var trailing))
        {
            FoldFeedbackRecord(trailing, sessionId, ratings, ratingSlots, remarks, logger);
        }

        return new SessionFeedback(sessionId, ratings, remarks);
    }

    /// <summary>
    ///     Fold one record into the feedback. A record that is not a
    ///     feedback line is not this method's business; a feedback line that
    ///     cannot be read warns and is skipped (§ROP-001), so one bad line
    ///     never costs the session its whole feedback.
    /// </summary>
    /// <param name="record">The record to fold.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ratings">The ratings collected so far.</param>
    /// <param name="ratingSlots">Message id to index in <paramref name="ratings" />.</param>
    /// <param name="remarks">The remarks collected so far.</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    private static void FoldFeedbackRecord(
        ReadOnlySpan<byte> record,
        string sessionId,
        List<MessageRating> ratings,
        Dictionary<string, int> ratingSlots,
        List<SessionRemark> remarks,
        ILogger logger)
    {
        if (SessionFileReader.IsMessageRatingLine(record))
        {
            MessageRatingEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.MessageRatingEntry);
            }
            catch (JsonException ex)
            {
                JsonlSessionStoreLog.SkippingMalformedFeedbackLine(logger, ex, sessionId);
                return;
            }

            if (entry is not { Type: "messageRating", Id: { Length: > 0 }, MessageId: { Length: > 0 } })
            {
                JsonlSessionStoreLog.FeedbackLineMissingIds(logger, sessionId);
                return;
            }

            // A value this build does not know is a record from a newer
            // build. It is skipped, not guessed at: reading it as
            // "unhelpful" would put a verdict in the numbers the user never
            // gave.
            if (!SessionFeedbackMapperKit.TryParseRating(entry.Rating, out FeedbackRating rating))
            {
                JsonlSessionStoreLog.SkippingUnknownFeedbackValue(logger, sessionId, entry.Rating, entry.Category);
                return;
            }

            if (!SessionFeedbackMapperKit.TryParseCategory(entry.Category, out FeedbackCategory category))
            {
                JsonlSessionStoreLog.SkippingUnknownFeedbackValue(logger, sessionId, entry.Rating, entry.Category);
                return;
            }

            MessageRating mapped = SessionFeedbackMapperKit.ToContract(entry, sessionId, rating, category);
            if (ratingSlots.TryGetValue(mapped.MessageId, out int slot))
            {
                // The newer rating of the same message replaces the older one
                // IN PLACE: the position is the message's, and the value is
                // the newest word the user gave about it.
                ratings[slot] = mapped;
                return;
            }

            ratingSlots.Add(mapped.MessageId, ratings.Count);
            ratings.Add(mapped);
            return;
        }

        if (!SessionFileReader.IsSessionRemarkLine(record))
        {
            return;
        }

        SessionRemarkEntry? remark;
        try
        {
            remark = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.SessionRemarkEntry);
        }
        catch (JsonException ex)
        {
            JsonlSessionStoreLog.SkippingMalformedFeedbackLine(logger, ex, sessionId);
            return;
        }

        if (remark is not { Type: "sessionRemark", Id: { Length: > 0 } })
        {
            JsonlSessionStoreLog.FeedbackLineMissingIds(logger, sessionId);
            return;
        }

        remarks.Add(SessionFeedbackMapperKit.ToContract(remark, sessionId));
    }
}
