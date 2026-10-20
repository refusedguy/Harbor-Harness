// SessionFeedback.cs — the feedback model of #1746: what the USER said about
// the agent's work, kept with the session and never handed back to the model.
//
// WHY IT IS NOT PART OF A MESSAGE
// -------------------------------
// A message is what the model READS. A rating is what the user said ABOUT
// what the model wrote, and it must never become the model's next input —
// an agent that can read its own score optimises for the rater instead of for
// the task. So the feedback is not a field of AgentMessage: it is a record of
// its own, with its own lifecycle, stored beside the history.
//
// THE TWO SHAPES, AND WHY THEY ARE TWO
// ------------------------------------
//   * a RATING is about ONE message — a value and the reason for it, attached
//     to a message id. At most one rating is in force per message: a second
//     rating of the same message replaces the first, because the user changed
//     their mind and the newer word is the one they mean.
//   * a REMARK is about the SESSION — free text that belongs to no message.
//
// ABSENCE IS NOT A VALUE
// ----------------------
// FeedbackRating has no "not rated" member, and that is deliberate. A message
// nobody rated is NOT rated Unhelpful: it is not rated AT ALL, and the only
// honest shape for that is the absence of a record. A sentinel member would
// make "the user said this was bad" and "nobody answered" the same value, and
// every aggregate over it would then be wrong by exactly the population that
// never answered. So "what did the user think of message X?" is answered by
// "is there a rating for X?", and "no" is the absence — see
// SessionFeedback.RatingFor.
//
// WHO MAY WRITE
// -------------
// The user, and only the user. Nothing in the agent loop, in the tool
// registry or in the model context can reach these records: they are not
// AgentMessages, so the prompt path cannot carry them, and no builtin tool
// writes them. The store seam is the one door, and the host holds it.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     How a user rated one message. There is deliberately no "not rated"
///     member: a message nobody rated carries no rating at all, and the
///     absence of a <see cref="MessageRating" /> is how that is said.
/// </summary>
public enum FeedbackRating
{
    /// <summary>The message did not help.</summary>
    Unhelpful = 0,

    /// <summary>The message helped only partly, or the verdict is mixed.</summary>
    Neutral = 1,

    /// <summary>The message helped.</summary>
    Helpful = 2,
}

/// <summary>
///     Why a message was rated the way it was — the closed taxonomy that
///     makes the ratings aggregable. A rating without a category says "it was
///     bad" and cannot be counted; a category says which axis failed, which
///     is the part that can be compared across sessions.
/// </summary>
public enum FeedbackCategory
{
    /// <summary>The answer to the task was wrong, incomplete or missing.</summary>
    TaskResult = 0,

    /// <summary>The agent did not do what it was asked to do.</summary>
    InstructionFollowing = 1,

    /// <summary>The agent got stuck, asked the wrong things or was clumsy.</summary>
    ProductInteraction = 2,

    /// <summary>The agent broke, hung or failed in a way the user had to recover from.</summary>
    ServiceStability = 3,

    /// <summary>The run cost more time or money than it was worth.</summary>
    ResourceCost = 4,

    /// <summary>The agent touched permissions, secrets or privacy the user did not grant.</summary>
    SecurityPrivacyPermission = 5,

    /// <summary>None of the named axes fits; the free-text note says more.</summary>
    Other = 6,
}

/// <summary>
///     One message rating: the value the user gave, the axis they gave it on,
///     and an optional free-text note. The store mints <see cref="Id" /> and
///     <see cref="CreatedAt" /> — a caller never sets them.
/// </summary>
/// <param name="Id">Rating id (unique inside the session).</param>
/// <param name="SessionId">Owning session.</param>
/// <param name="MessageId">The rated message.</param>
/// <param name="Rating">The value the user gave.</param>
/// <param name="Category">The axis the value was given on.</param>
/// <param name="CreatedAt">When the rating was recorded.</param>
/// <param name="Note">Optional free-text note from the user.</param>
public sealed record MessageRating(
    string Id,
    string SessionId,
    string MessageId,
    FeedbackRating Rating,
    FeedbackCategory Category,
    DateTimeOffset CreatedAt,
    string? Note = null);

/// <summary>
///     One remark about the session as a whole — free text that belongs to no
///     message. The store mints <see cref="Id" /> and <see cref="CreatedAt" />.
/// </summary>
/// <param name="Id">Remark id (unique inside the session).</param>
/// <param name="SessionId">Owning session.</param>
/// <param name="Text">The user's text.</param>
/// <param name="CreatedAt">When the remark was recorded.</param>
public sealed record SessionRemark(
    string Id,
    string SessionId,
    string Text,
    DateTimeOffset CreatedAt);

/// <summary>
///     The session's feedback: the ratings in force and the remarks. Both
///     lists are empty rather than null for a session that carries no
///     feedback — "nothing was said" is a fact about the session, not a
///     missing value.
/// </summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="Ratings">
///     One rating per rated message, in the order the messages were first
///     rated. A message rated twice appears once, carrying the newer rating.
/// </param>
/// <param name="Remarks">The remarks, in the order they were recorded.</param>
public sealed record SessionFeedback(
    string SessionId,
    IReadOnlyList<MessageRating> Ratings,
    IReadOnlyList<SessionRemark> Remarks)
{
    /// <summary>
    ///     The rating in force for one message, or <see langword="null" /> when
    ///     the message carries none. The null is the whole point: "the user
    ///     did not rate this" and "the user rated this as unhelpful" must
    ///     never be the same answer.
    /// </summary>
    /// <param name="messageId">The message to look up.</param>
    /// <returns>The rating, or null when the message carries none.</returns>
    public MessageRating? RatingFor(string messageId)
    {
        for (int i = 0; i < Ratings.Count; i++)
        {
            if (string.Equals(Ratings[i].MessageId, messageId, StringComparison.Ordinal))
            {
                return Ratings[i];
            }
        }

        return null;
    }
}
