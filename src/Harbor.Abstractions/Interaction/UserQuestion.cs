namespace Harbor.Abstractions.Interaction;

/// <summary>
///     The stable codes the question channel reports to the model.
/// </summary>
/// <remarks>
///     <para>
///         A code is a CONTRACT, not a message: the model and every policy that
///         reads a question outcome match on the code, never on the sentence
///         around it. The same discipline the tool-result codes use
///         (<c>UnresolvedToolCall.NotStartedCode</c> and its siblings): the
///         text may be reworded, the code may not.
///     </para>
///     <para>
///         These four are the whole vocabulary of a question outcome. The
///         success case carries <see cref="Answered" />; the three failure
///         cases each say WHY there is no answer, and each is recoverable in a
///         different way: <see cref="Pending" /> means "ask again later",
///         <see cref="Aborted" /> means "the human dismissed it" and
///         <see cref="NoChannel" /> means "there is no human to ask".
///     </para>
/// </remarks>
public static class UserQuestionCodes
{
    /// <summary>The human answered; the outcome carries the answers.</summary>
    public const string Answered = "ASK_ANSWERED";

    /// <summary>The human dismissed the question without answering it.</summary>
    public const string Aborted = "ASK_ABORTED";

    /// <summary>The wait window expired; the human has not answered yet.</summary>
    public const string Pending = "ASK_PENDING";

    /// <summary>Nobody can answer: this host has no interactive human.</summary>
    public const string NoChannel = "ASK_NO_CHANNEL";
}

/// <summary>
///     One answerable option of a question.
/// </summary>
/// <param name="Label">The option text, shown to the human and echoed back in the answer.</param>
/// <param name="Description">An optional longer explanation of what picking this option means.</param>
public sealed record UserQuestionOption(string Label, string? Description = null);

/// <summary>
///     One question in a batch: what to ask, and what the human may answer.
/// </summary>
/// <param name="Id">
///     A stable id the model chose. It is echoed back in the answer, so the
///     model can tell two questions of the same batch apart without parsing
///     the text.
/// </param>
/// <param name="Question">The question itself, in the model's own words.</param>
/// <param name="Header">A short label for the question, for a UI that shows a title.</param>
/// <param name="Options">
///     The choices the human may pick. <see langword="null" /> (or empty) means
///     the question is open-ended and the answer is free text.
/// </param>
/// <param name="MultiSelect">Whether the human may pick more than one option.</param>
public sealed record UserQuestion(
    string Id,
    string Question,
    string? Header = null,
    IReadOnlyList<UserQuestionOption>? Options = null,
    bool MultiSelect = false);

/// <summary>
///     A batch of questions asked in one call, and the window the human has to
///     answer them.
/// </summary>
/// <param name="Questions">The questions, in the order the human should see them.</param>
/// <param name="Timeout">
///     How long to wait for the answer. <see langword="null" /> means the
///     channel's default. A window is always finite: a question that waits
///     forever is a run that never ends.
/// </param>
public sealed record UserQuestionRequest(
    IReadOnlyList<UserQuestion> Questions,
    TimeSpan? Timeout = null);

/// <summary>
///     The human's answer to one question.
/// </summary>
/// <param name="Id">The id of the question this answers, which is the model's own id, echoed.</param>
/// <param name="Selected">The labels of the options the human picked. Empty for a free-text answer.</param>
/// <param name="Custom">The free-text answer, when the human typed one instead of picking an option.</param>
public sealed record UserQuestionAnswer(
    string Id,
    IReadOnlyList<string> Selected,
    string? Custom = null);

/// <summary>
///     How a question ended.
/// </summary>
public enum UserQuestionStatus
{
    /// <summary>The human answered. The outcome carries the answers.</summary>
    Answered,

    /// <summary>The wait window expired before an answer arrived.</summary>
    Pending,

    /// <summary>The human dismissed the question without answering it.</summary>
    Aborted,

    /// <summary>There is nobody who could answer: the host has no interactive human.</summary>
    Unavailable,
}

/// <summary>
///     The result of asking a batch of questions: how it ended, what the human
///     said, and, when it did not end in an answer, the stable code and the
///     reason.
/// </summary>
/// <param name="Status">How the question ended.</param>
/// <param name="Answers">The answers, empty unless <paramref name="Status" /> is <see cref="UserQuestionStatus.Answered" />.</param>
/// <param name="Code">The stable outcome code, one of <see cref="UserQuestionCodes" />.</param>
/// <param name="Reason">A human-readable explanation of a non-answer.</param>
public sealed record UserQuestionOutcome(
    UserQuestionStatus Status,
    IReadOnlyList<UserQuestionAnswer> Answers,
    string? Code = null,
    string? Reason = null)
{
    /// <summary>The human answered: the outcome carries what they said.</summary>
    /// <param name="answers">The answers, one per answered question.</param>
    /// <returns>An <see cref="UserQuestionStatus.Answered" /> outcome.</returns>
    public static UserQuestionOutcome Answered(IReadOnlyList<UserQuestionAnswer> answers) =>
        new(UserQuestionStatus.Answered, answers, UserQuestionCodes.Answered);

    /// <summary>The wait window expired: the human has not answered yet.</summary>
    /// <param name="reason">Why the window ended without an answer.</param>
    /// <returns>A <see cref="UserQuestionStatus.Pending" /> outcome.</returns>
    public static UserQuestionOutcome Pending(string reason) =>
        new(UserQuestionStatus.Pending, Array.Empty<UserQuestionAnswer>(), UserQuestionCodes.Pending, reason);

    /// <summary>The human dismissed the question without answering it.</summary>
    /// <param name="reason">Why the question was dismissed.</param>
    /// <returns>An <see cref="UserQuestionStatus.Aborted" /> outcome.</returns>
    public static UserQuestionOutcome Aborted(string reason) =>
        new(UserQuestionStatus.Aborted, Array.Empty<UserQuestionAnswer>(), UserQuestionCodes.Aborted, reason);

    /// <summary>There is nobody who could answer the question.</summary>
    /// <param name="reason">Why the channel cannot ask.</param>
    /// <returns>An <see cref="UserQuestionStatus.Unavailable" /> outcome.</returns>
    public static UserQuestionOutcome Unavailable(string reason) =>
        new(UserQuestionStatus.Unavailable, Array.Empty<UserQuestionAnswer>(), UserQuestionCodes.NoChannel, reason);
}
