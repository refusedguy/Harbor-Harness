using Harbor.Abstractions.Interaction;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     A question channel that answers with whatever the test hands it. It
///     exists so a test can hold a tool that needs the channel without
///     building a presenter for it.
/// </summary>
internal sealed class StubUserQuestionService(UserQuestionOutcome outcome) : IUserQuestionService
{
    /// <summary>The outcome every ask returns.</summary>
    internal UserQuestionOutcome Outcome { get; set; } = outcome;

    /// <summary>The requests the channel was asked, in order.</summary>
    internal List<UserQuestionRequest> Requests { get; } = [];

    public Task<UserQuestionOutcome> AskAsync(UserQuestionRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Outcome);
    }
}
