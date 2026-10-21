// SessionGoalRows.cs — the on-disk shape of the goal records (#1761) and the
// mapper between them and the domain row.
//
// The goal lives in the session file as its OWN records, beside the messages,
// the checkpoints and the feedback:
//
//   {"type":"goal","objective":"…","phase":"Active","maxRounds":3,
//    "roundsStarted":1,"revision":2,"createdAt":"…","updatedAt":"…",
//    "blockedReason":null,"blockedCode":null}
//
// Why its own records and not a field on a message: a message is what the MODEL
// reads, and a goal is what the HARNESS tracks. Keeping the two apart in the
// file is what makes "the goal never reaches the model as history" a property
// of the format rather than a promise — the message read path skips these
// lines the way it skips the checkpoint markers and the feedback lines.
//
// The phase and the block code are stored as their NAMES, not as their
// numbers, for the reason the feedback axis already gave: a session file is
// read by people as often as by code, and a taxonomy that grows must not
// renumber the records already written. A name this build does not know is a
// record it REFUSES rather than one it guesses at.

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The <c>"type":"goal"</c> line: one snapshot of a session's goal.
/// </summary>
internal sealed record GoalEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("objective")] string Objective,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("maxRounds")] int MaxRounds,
    [property: JsonPropertyName("roundsStarted")] int RoundsStarted,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("blockedReason")] string? BlockedReason = null,
    [property: JsonPropertyName("blockedCode")] string? BlockedCode = null);

/// <summary>
///     The one mapper between the goal lines and the domain row — the same
///     "one border, one converter" rule the checkpoint axis and the feedback
///     axis follow.
/// </summary>
internal static class SessionGoalMapperKit
{
    /// <summary>
    ///     Parse a stored phase name. A name this build does not know is a
    ///     REFUSAL, not a default: a record from a newer build is skipped with
    ///     a warning rather than read as a phase the writer never set.
    /// </summary>
    /// <param name="value">The stored name.</param>
    /// <param name="phase">The parsed phase.</param>
    /// <returns>True when the name is one this build knows.</returns>
    public static bool TryParsePhase(string? value, out GoalPhase phase)
    {
        // Enum.TryParse also accepts the NUMERIC value of a member, so a
        // hand-edited "7" would parse into a value this build does not
        // define. IsDefined is the second half of the refusal.
        if (!Enum.TryParse(value, ignoreCase: true, out phase))
        {
            return false;
        }

        return Enum.IsDefined(phase);
    }

    /// <summary>
    ///     Parse a stored block-code name, with the same refusal as
    ///     <see cref="TryParsePhase" />.
    /// </summary>
    /// <param name="value">The stored name.</param>
    /// <param name="code">The parsed block code.</param>
    /// <returns>True when the name is one this build knows.</returns>
    public static bool TryParseBlockCode(string? value, out GoalBlockCode code)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out code))
        {
            return false;
        }

        return Enum.IsDefined(code);
    }

    /// <summary>
    ///     Map a scanned goal line to the domain row.
    /// </summary>
    /// <param name="row">The decoded line.</param>
    /// <param name="phase">The parsed phase.</param>
    /// <param name="code">The parsed block code, or null when the line names none.</param>
    /// <returns>The domain row.</returns>
    public static Goal ToContract(GoalEntry row, GoalPhase phase, GoalBlockCode? code) =>
        new(row.Objective, phase, row.MaxRounds, row.RoundsStarted, row.Revision,
            row.CreatedAt, row.UpdatedAt, row.BlockedReason, code);

    /// <summary>
    ///     Map a domain goal to the line shape the store appends.
    /// </summary>
    /// <param name="goal">The domain row.</param>
    /// <returns>The on-disk line shape.</returns>
    public static GoalEntry ToRow(Goal goal) =>
        new("goal", goal.Objective, goal.Phase.ToString(), goal.MaxRounds,
            goal.RoundsStarted, goal.Revision, goal.CreatedAt, goal.UpdatedAt,
            goal.BlockedReason, goal.BlockedCode?.ToString());
}
