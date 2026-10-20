using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using TUnit.Assertions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Branch coverage for <see cref="SubAgentFailureFormat.TryExtractResumeHint" />:
///     one test per guard of the extraction chain, and the absence each guard
///     produces. The format lives in Harbor.Abstractions, so its contract is
///     tested here rather than in the project of the tool that reads the hint.
/// </summary>
public class SubAgentFailureFormatTests
{
    [Test]
    public async Task TryExtractResumeHint_NullError_ReturnsNone()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint(null!, "explore");

        await Assert.That(hint.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TryExtractResumeHint_EmptyError_ReturnsNone()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint(string.Empty, "explore");

        await Assert.That(hint.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TryExtractResumeHint_NoMarker_ReturnsNone()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint("model exploded", "explore");

        await Assert.That(hint.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TryExtractResumeHint_TruncatedTrailer_ReturnsNone()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint("boom [resume-session:abc123", "explore");

        await Assert.That(hint.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TryExtractResumeHint_EmptySessionId_ReturnsNone()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint("boom [resume-session:]", "explore");

        await Assert.That(hint.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TryExtractResumeHint_SessionIdWithWhitespace_ReturnsNone()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint("boom [resume-session:abc 123]", "explore");

        await Assert.That(hint.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TryExtractResumeHint_SessionIdWithBracket_ReturnsNone()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint("boom [resume-session:abc[123]", "explore");

        await Assert.That(hint.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TryExtractResumeHint_TrailerPresent_ReturnsHint()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint("boom [resume-session:abc123]", "explore");

        await Assert.That(hint.HasValue).IsTrue();
        await Assert.That(hint.Value.SessionId).IsEqualTo("abc123");
        await Assert.That(hint.Value.AgentName).IsEqualTo("explore");
    }

    [Test]
    public async Task TryExtractResumeHint_LastMarkerWins()
    {
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint(
            "boom [resume-session:first] and [resume-session:second]", "explore");

        await Assert.That(hint.HasValue).IsTrue();
        await Assert.That(hint.Value.SessionId).IsEqualTo("second");
    }

    [Test]
    public async Task WithResumeTrailer_RoundTripsThroughFormatter()
    {
        string message = SubAgentFailureFormat.WithResumeTrailer("Sub-agent 'plan' failed: bad day.", "sess42");

        await Assert.That(message).Contains("[resume-session:sess42]");
        Maybe<SubAgentResumeHint> hint = SubAgentFailureFormat.TryExtractResumeHint(message, "plan");
        await Assert.That(hint.HasValue).IsTrue();
        await Assert.That(hint.Value).IsEqualTo(new SubAgentResumeHint("sess42", "plan"));
    }
}
