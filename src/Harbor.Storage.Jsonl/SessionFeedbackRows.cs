// SessionFeedbackRows.cs — the on-disk shape of the feedback records (#1746)
// and the mapper between them and the domain rows.
//
// The feedback lives in the session file as its OWN records, beside the
// messages and the checkpoints:
//
//   {"type":"messageRating","id":"…","messageId":"…","rating":"helpful",
//    "category":"InstructionFollowing","createdAt":"…","note":"…"}
//   {"type":"sessionRemark","id":"…","text":"…","createdAt":"…"}
//
// Why its own records and not fields on the message: a message is what the
// MODEL reads, and a rating is what the USER said about it. Keeping the two
// apart in the file is what makes "the feedback never reaches the model" a
// property of the format rather than a promise — the message read path skips
// these lines the way it skips the checkpoint markers.
//
// The rating and the category are stored as their NAMES, not as numbers: a
// session file is read by people as often as by code, and a taxonomy that
// grows must not renumber the records already written. A name this build
// does not know is a record it refuses rather than one it guesses at — see
// SessionFeedbackMapperKit.ToContract.

using System.Text.Json.Serialization;
using Harbor.Abstractions.Sessions;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The <c>"type":"messageRating"</c> line: one rating of one message.
/// </summary>
internal sealed record MessageRatingEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("rating")] string Rating,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("note")] string? Note = null);

/// <summary>
///     The <c>"type":"sessionRemark"</c> line: free text about the session.
/// </summary>
internal sealed record SessionRemarkEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>
///     The one mapper between the feedback lines and the domain rows — the
///     same "one border, one converter" rule the checkpoint axis follows
///     (<see cref="SessionCheckpointMapperKit" />).
/// </summary>
internal static class SessionFeedbackMapperKit
{
    /// <summary>
    ///     Parse a stored rating name. A name this build does not know is a
    ///     REFUSAL, not a default: a record from a newer build is skipped
    ///     with a warning rather than read as an "unhelpful" the user never
    ///     gave.
    /// </summary>
    /// <param name="value">The stored name.</param>
    /// <param name="rating">The parsed value.</param>
    /// <returns>True when the name is one this build knows.</returns>
    public static bool TryParseRating(string? value, out FeedbackRating rating)
    {
        // Enum.TryParse also accepts the NUMERIC value of a member, so a
        // hand-edited "7" would parse into a value this build does not
        // define. IsDefined is the second half of the refusal.
        if (!Enum.TryParse(value, ignoreCase: true, out rating))
        {
            return false;
        }

        return Enum.IsDefined(rating);
    }

    /// <summary>
    ///     Parse a stored category name, with the same refusal as
    ///     <see cref="TryParseRating" />.
    /// </summary>
    /// <param name="value">The stored name.</param>
    /// <param name="category">The parsed value.</param>
    /// <returns>True when the name is one this build knows.</returns>
    public static bool TryParseCategory(string? value, out FeedbackCategory category)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out category))
        {
            return false;
        }

        return Enum.IsDefined(category);
    }

    /// <summary>
    ///     Map a scanned rating line to the domain row. The rating and the
    ///     category are passed in already parsed: a caller that could not
    ///     parse them has no row to map.
    /// </summary>
    /// <param name="row">The decoded line.</param>
    /// <param name="sessionId">Owning session id.</param>
    /// <param name="rating">The parsed rating value.</param>
    /// <param name="category">The parsed category.</param>
    /// <returns>The domain row.</returns>
    public static MessageRating ToContract(
        MessageRatingEntry row,
        string sessionId,
        FeedbackRating rating,
        FeedbackCategory category) =>
        new(row.Id, sessionId, row.MessageId, rating, category, row.CreatedAt, row.Note);

    /// <summary>
    ///     Map a scanned remark line to the domain row.
    /// </summary>
    /// <param name="row">The decoded line.</param>
    /// <param name="sessionId">Owning session id.</param>
    /// <returns>The domain row.</returns>
    public static SessionRemark ToContract(SessionRemarkEntry row, string sessionId) =>
        new(row.Id, sessionId, row.Text, row.CreatedAt);

    /// <summary>
    ///     Map a domain rating to the line shape the store appends.
    /// </summary>
    /// <param name="rating">The domain row.</param>
    /// <returns>The on-disk line shape.</returns>
    public static MessageRatingEntry ToRow(MessageRating rating) =>
        new("messageRating", rating.Id, rating.MessageId,
            rating.Rating.ToString(), rating.Category.ToString(),
            rating.CreatedAt, rating.Note);

    /// <summary>
    ///     Map a domain remark to the line shape the store appends.
    /// </summary>
    /// <param name="remark">The domain row.</param>
    /// <returns>The on-disk line shape.</returns>
    public static SessionRemarkEntry ToRow(SessionRemark remark) =>
        new("sessionRemark", remark.Id, remark.Text, remark.CreatedAt);
}
