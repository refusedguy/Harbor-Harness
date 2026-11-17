using Harbor.Abstractions.Interaction;
using Harbor.Application.Interaction;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Tests for <see cref="UserQuestionService" /> (issue #1759): the waiting
///     side of the question channel. The window, the dismissal, the timeout and
///     the cancelled caller are the four facts the service has to get right.
/// </summary>
public class UserQuestionServiceTests
{
    private static UserQuestionRequest Request(TimeSpan? timeout = null) =>
        new([new UserQuestion("q1", "Which one?")], timeout);

    [Test]
    public async Task An_Answer_Inside_The_Window_Comes_Back()
    {
        var service = new UserQuestionService(NullLogger<UserQuestionService>.Instance);
        service.AttachPresenter((_, _) => Task.FromResult(
            UserQuestionOutcome.Answered([new UserQuestionAnswer("q1", ["A"], null)])));

        UserQuestionOutcome outcome = await service.AskAsync(Request(TimeSpan.FromSeconds(5)));

        await Assert.That(outcome.Status == UserQuestionStatus.Answered).IsTrue();
        await Assert.That(outcome.Answers.Count).IsEqualTo(1);
        await Assert.That(outcome.Answers[0].Selected[0]).IsEqualTo("A");
    }

    [Test]
    public async Task A_Dismissal_Comes_Back_As_Aborted()
    {
        var service = new UserQuestionService(NullLogger<UserQuestionService>.Instance);
        service.AttachPresenter((_, _) => Task.FromResult(
            UserQuestionOutcome.Aborted("The user skipped the question.")));

        UserQuestionOutcome outcome = await service.AskAsync(Request(TimeSpan.FromSeconds(5)));

        await Assert.That(outcome.Status == UserQuestionStatus.Aborted).IsTrue();
        await Assert.That(outcome.Code).IsEqualTo(UserQuestionCodes.Aborted);
    }

    [Test]
    public async Task The_Window_Closing_Leaves_The_Question_Open()
    {
        var service = new UserQuestionService(NullLogger<UserQuestionService>.Instance);
        // A presenter that waits for the token and never answers.
        service.AttachPresenter(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return UserQuestionOutcome.Aborted("cancelled");
        });

        UserQuestionOutcome outcome = await service.AskAsync(Request(TimeSpan.FromMilliseconds(50)));

        await Assert.That(outcome.Status == UserQuestionStatus.Pending).IsTrue();
        await Assert.That(outcome.Code).IsEqualTo(UserQuestionCodes.Pending);
        await Assert.That(outcome.Answers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_Host_With_No_Human_Answers_That_Nobody_Is_There()
    {
        var service = new UserQuestionService(NullLogger<UserQuestionService>.Instance);

        UserQuestionOutcome outcome = await service.AskAsync(Request(TimeSpan.FromMilliseconds(50)));

        await Assert.That(outcome.Status == UserQuestionStatus.Unavailable).IsTrue();
        await Assert.That(outcome.Code).IsEqualTo(UserQuestionCodes.NoChannel);
    }

    [Test]
    public async Task A_Cancelled_Caller_Never_Gets_A_Verdict()
    {
        var service = new UserQuestionService(NullLogger<UserQuestionService>.Instance);
        service.AttachPresenter(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return UserQuestionOutcome.Aborted("cancelled");
        });

        using var cts = TestCancellation.Cancelled();

        await Assert.That(async () => await service.AskAsync(Request(TimeSpan.FromSeconds(30)), cts.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task The_Window_Is_Bounded_And_The_Presenter_Sees_The_Effective_One()
    {
        var service = new UserQuestionService(NullLogger<UserQuestionService>.Instance);
        TimeSpan? seen = null;
        service.AttachPresenter((request, _) =>
        {
            seen = request.Timeout;
            return Task.FromResult(UserQuestionOutcome.Answered([]));
        });

        await service.AskAsync(Request(TimeSpan.FromDays(1)));

        // A question may not hold a run for a day: the window is capped, and
        // the surface that draws the countdown is told the capped value.
        await Assert.That(seen.HasValue).IsTrue();
        await Assert.That(seen.GetValueOrDefault()).IsEqualTo(UserQuestionService.MaxWindow);
    }

    [Test]
    public async Task A_Request_Without_A_Window_Gets_The_Default_One()
    {
        var service = new UserQuestionService(NullLogger<UserQuestionService>.Instance);
        TimeSpan? seen = null;
        service.AttachPresenter((request, _) =>
        {
            seen = request.Timeout;
            return Task.FromResult(UserQuestionOutcome.Answered([]));
        });

        await service.AskAsync(Request());

        await Assert.That(seen.HasValue).IsTrue();
        await Assert.That(seen.GetValueOrDefault()).IsEqualTo(UserQuestionService.DefaultWindow);
    }
}
