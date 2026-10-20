// ISessionFeedbackStore.cs — the optional feedback surface of a session store
// (#1746): what the USER said about the agent's work, recorded with the
// session and read back from it.
//
// WHY A SEPARATE SEAM
// -------------------
// Interface segregation, the same call the checkpoint axis made (#1247):
// ISessionStore stays the persistence contract every backend implements
// (Memory included, and a backend that is not durable has no meaningful
// feedback story), while these three verbs are a seam the durable backends
// implement. A host that wants feedback names this interface once; a store
// that does not implement it is not forced into members it cannot honour
// (LSP).
//
// THE RULES THIS CONTRACT FIXES
// -----------------------------
//
// WHO MAY WRITE. The user, and only the user. The agent loop, the tool
// registry and the model context have no path to these records: a rating is
// not an AgentMessage, so the prompt path cannot carry it, and no builtin
// tool writes one. The store is the one door, and the host holds it.
//
// WHAT A CONFLICT IS. Two ratings of the SAME message: the newer one wins
// and the older one stays in the log (the store is append-only, so nothing is
// destroyed — the effective rating is the fold). A remark never conflicts:
// remarks accumulate, and each one is kept.
//
// WHAT "NO RATING" MEANS. A message nobody rated carries no rating, and the
// absence of a record is how that is said — there is no sentinel value and no
// "not rated" member of FeedbackRating.
//
// NEVER IN THE MODEL. Reading the feedback does not change what the model
// sees: the model context is built from messages, and a rating is not one.
// A store that keeps the feedback in the message stream would break that
// rule, and the interface is where the rule is stated.
//
// THE FAILURE CONTRACT (shared with ISessionStore): expected states travel
// the Result rail — nothing here throws except OperationCanceledException.
// The shapes are the shared ones: "Session '{id}' not found." and
// "Message '{mid}' not found in session '{sid}'.".

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The feedback seam of a session store: record a rating of one message or
///     a remark about the session, and read both back.
/// </summary>
public interface ISessionFeedbackStore
{
    /// <summary>
    ///     Record the user's rating of one message: a value and the axis it
    ///     was given on, with an optional free-text note.
    /// </summary>
    /// <remarks>
    ///     A second rating of the same message replaces the first in the
    ///     effective view (last write wins) and leaves the earlier one in the
    ///     log. A rating of a message the session does not hold fails without
    ///     writing anything, so the feedback never names a message that is
    ///     not there.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">The rated message id.</param>
    /// <param name="rating">The value the user gave.</param>
    /// <param name="category">The axis the value was given on.</param>
    /// <param name="note">Optional free-text note from the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded rating, or failure if the session/message is not found.</returns>
    public Task<Result<MessageRating>> RecordMessageRatingAsync(
        string sessionId,
        string messageId,
        FeedbackRating rating,
        FeedbackCategory category,
        string? note = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Record a remark about the session as a whole — free text that
    ///     belongs to no message. Remarks accumulate: one session may carry
    ///     many.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="text">The user's text; it must not be blank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded remark, or failure if the session is not found.</returns>
    public Task<Result<SessionRemark>> RecordSessionRemarkAsync(
        string sessionId,
        string text,
        CancellationToken ct = default);

    /// <summary>
    ///     Read the session's feedback: the ratings in force (one per rated
    ///     message, the newest winning) and the remarks in the order they
    ///     were recorded. A session with no feedback yields empty lists, not
    ///     a failure.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The feedback, or failure if the session is not found.</returns>
    public Task<Result<SessionFeedback>> GetFeedbackAsync(
        string sessionId,
        CancellationToken ct = default);
}
