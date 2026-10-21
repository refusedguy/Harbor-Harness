namespace Harbor.Abstractions.Interaction;

/// <summary>
///     The channel through which the MODEL asks the HUMAN a question and gets
///     an answer back (issue #1759).
/// </summary>
/// <remarks>
///     <para>
///         This is the second half of the human-facing surface Harbor has, and
///         it is deliberately NOT the permission channel. A permission question
///         is binary and about an ACTION ("may I run this?") and it is
///         answered with allow/deny. A question here is about a TASK ("which
///         of these three approaches?", "test or documentation?") and it is
///         answered with a choice or with free text.
///         <see cref="Permissions.PermissionRequest" /> is the vocabulary of
///         the first; this is the vocabulary of the second, and the two never
///         borrow each other's shapes.
///     </para>
///     <para>
///         Implementations MUST be thread-safe: several tool calls may ask at
///         once, and the answers arrive on whatever thread the human's UI
///         uses.
///     </para>
/// </remarks>
public interface IUserQuestionService
{
    /// <summary>
    ///     Ask the human a batch of questions and wait for the answer.
    /// </summary>
    /// <param name="request">The questions and the wait window.</param>
    /// <param name="ct">
    ///     The caller's token. A cancelled caller never gets a verdict: the
    ///     cancellation propagates, it does not collapse into "no answer".
    /// </param>
    /// <returns>
    ///     The outcome: answered, pending (the window expired), aborted (the
    ///     human dismissed it) or unavailable (there is nobody to ask).
    /// </returns>
    public Task<UserQuestionOutcome> AskAsync(
        UserQuestionRequest request,
        CancellationToken ct = default);
}
