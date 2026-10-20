using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The feedback axis of the JSONL store (#1746): what the USER said about
///     the agent's work is recorded with the session, survives a restart, and
///     stays out of the message history the model reads.
/// </summary>
/// <remarks>
///     The rules these tests pin:
///     <list type="bullet">
///         <item>a rating is a value plus the reason for it, attached to one message;</item>
///         <item>a remark is free text about the session;</item>
///         <item>a message nobody rated carries NO rating — the absence is the answer;</item>
///         <item>both survive a restart, because they live in the session file;</item>
///         <item>neither ever becomes history, so neither can reach the model;</item>
///         <item>a session file written before the axis existed reads exactly as it did;</item>
///         <item>the newer rating of a message wins, and the older one stays in the log.</item>
///     </list>
/// </remarks>
public class JsonlSessionFeedbackTests
{
    /// <summary>
    ///     A string that appears ONLY in the feedback, so a test can prove it
    ///     is nowhere in the history.
    /// </summary>
    private const string Marker = "feedback-marker-1746";

    private static (JsonlSessionStore Store, CapturingLogger<JsonlSessionStore> Logger) CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var logger = new CapturingLogger<JsonlSessionStore>();
        return (new JsonlSessionStore(tempDir, logger), logger);
    }

    private static UserMessage Msg(string sessionId, int i) => new(
        $"msg-{i:D2}-{Guid.NewGuid():N}",
        sessionId,
        DateTimeOffset.UtcNow.AddMilliseconds(i),
        $"message {i}",
        "code",
        "claude");

    private static AssistantMessage AssistantMsg(string sessionId, int i) => new(
        $"asst-{i:D2}-{Guid.NewGuid():N}",
        sessionId,
        DateTimeOffset.UtcNow.AddMilliseconds(i),
        [new TextPart("the answer")],
        StopReason.Stop,
        new Usage(3, 5),
        "claude");

    private static string SessionFile(JsonlSessionStore store, string sessionId) =>
        Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");

    private static void Delete(JsonlSessionStore store)
    {
        if (Directory.Exists(store.GetRootDirectory()))
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task RecordMessageRating_RoundTrips_AndSurvivesARestart()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;
            var message = Msg(session.Id, 0);
            await store.AppendMessageAsync(session.Id, message);

            var recorded = await store.RecordMessageRatingAsync(
                session.Id,
                message.Id,
                FeedbackRating.Unhelpful,
                FeedbackCategory.InstructionFollowing,
                "it did not do what I asked");

            await Assert.That(recorded.IsSuccess).IsTrue();
            await Assert.That(recorded.Value.MessageId).IsEqualTo(message.Id);
            await Assert.That(recorded.Value.SessionId).IsEqualTo(session.Id);
            await Assert.That(recorded.Value.Rating).IsEqualTo(FeedbackRating.Unhelpful);
            await Assert.That(recorded.Value.Category).IsEqualTo(FeedbackCategory.InstructionFollowing);
            await Assert.That(recorded.Value.Note).IsEqualTo("it did not do what I asked");
            await Assert.That(recorded.Value.Id).IsNotNull();

            // A restart: a NEW store over the same directory reads what the
            // previous process wrote.
            var reopened = new JsonlSessionStore(
                store.GetRootDirectory(),
                NullLogger<JsonlSessionStore>.Instance);

            var feedback = await reopened.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.IsSuccess).IsTrue();
            await Assert.That(feedback.Value.Ratings.Count).IsEqualTo(1);

            MessageRating rating = feedback.Value.Ratings[0];
            await Assert.That(rating.Id).IsEqualTo(recorded.Value.Id);
            await Assert.That(rating.MessageId).IsEqualTo(message.Id);
            await Assert.That(rating.Rating).IsEqualTo(FeedbackRating.Unhelpful);
            await Assert.That(rating.Category).IsEqualTo(FeedbackCategory.InstructionFollowing);
            await Assert.That(rating.Note).IsEqualTo("it did not do what I asked");
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task RecordSessionRemark_RoundTrips_AndSurvivesARestart()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, Msg(session.Id, 0));

            var recorded = await store.RecordSessionRemarkAsync(
                session.Id,
                $"the whole session felt slow ({Marker})");

            await Assert.That(recorded.IsSuccess).IsTrue();
            await Assert.That(recorded.Value.Text).IsEqualTo($"the whole session felt slow ({Marker})");
            await Assert.That(recorded.Value.SessionId).IsEqualTo(session.Id);

            var reopened = new JsonlSessionStore(
                store.GetRootDirectory(),
                NullLogger<JsonlSessionStore>.Instance);

            var feedback = await reopened.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.IsSuccess).IsTrue();
            await Assert.That(feedback.Value.Remarks.Count).IsEqualTo(1);
            await Assert.That(feedback.Value.Remarks[0].Text).IsEqualTo($"the whole session felt slow ({Marker})");
            await Assert.That(feedback.Value.Remarks[0].Id).IsEqualTo(recorded.Value.Id);
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task AMessageNobodyRated_CarriesNoRating()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;
            var rated = Msg(session.Id, 0);
            var unrated = Msg(session.Id, 1);
            await store.AppendMessageAsync(session.Id, rated);
            await store.AppendMessageAsync(session.Id, unrated);

            await store.RecordMessageRatingAsync(
                session.Id, rated.Id, FeedbackRating.Neutral, FeedbackCategory.TaskResult);

            var feedback = await store.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.IsSuccess).IsTrue();

            // The absence is the answer: no record, not a "Neutral" and not
            // a null row with a rating in it.
            await Assert.That(feedback.Value.Ratings.Count).IsEqualTo(1);
            await Assert.That(feedback.Value.RatingFor(unrated.Id)).IsNull();
            await Assert.That(feedback.Value.RatingFor(rated.Id)).IsNotNull();
            await Assert.That(feedback.Value.RatingFor(rated.Id)!.Rating).IsEqualTo(FeedbackRating.Neutral);
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task TheFeedback_NeverEntersTheMessageHistory()
    {
        var (store, logger) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;
            var first = Msg(session.Id, 0);
            var second = Msg(session.Id, 1);
            var answer = AssistantMsg(session.Id, 2);
            await store.AppendMessageAsync(session.Id, first);
            await store.AppendMessageAsync(session.Id, second);
            await store.AppendMessageAsync(session.Id, answer);

            await store.RecordMessageRatingAsync(
                session.Id, first.Id, FeedbackRating.Unhelpful, FeedbackCategory.Other, Marker);
            await store.RecordSessionRemarkAsync(session.Id, Marker);

            // The model context is built from the messages the store hands
            // out, so "the feedback never reaches the model" IS "the
            // messages carry no feedback".
            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(3);
            await Assert.That(messages.Value[0].Id).IsEqualTo(first.Id);
            await Assert.That(messages.Value[1].Id).IsEqualTo(second.Id);
            await Assert.That(messages.Value[2].Id).IsEqualTo(answer.Id);

            foreach (UserMessage message in messages.Value.OfType<UserMessage>())
            {
                await Assert.That(message.Content).DoesNotContain(Marker);
            }

            // The stats are derived from the messages, so the two feedback
            // records do not inflate the session's message count either.
            var stats = await store.GetStatsAsync(session.Id);
            await Assert.That(stats.Value.MessageCount).IsEqualTo(1);

            // And the read did not trip over the feedback lines: they are
            // recognised as feedback, not reported as malformed records.
            await Assert.That(logger.Warnings).IsEmpty();
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task ALegacySessionFile_WithoutFeedback_ReadsAsIs()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/legacy", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, Msg(session.Id, 0));

            // Rewrite the file the way a build that knew nothing about the
            // feedback axis wrote it: the same header MINUS the fields that
            // came later, and no feedback line at all.
            string[] lines = File.ReadAllLines(SessionFile(store, session.Id));
            string legacyHeader =
                "{\"type\":\"session\",\"version\":1,\"id\":\"" + session.Id + "\"," +
                "\"projectId\":\"" + session.ProjectId + "\"," +
                "\"directory\":\"/legacy\",\"title\":\"Legacy\",\"agent\":\"code\"," +
                "\"model\":\"claude-opus-4\",\"providerId\":\"anthropic\"," +
                "\"createdAt\":\"2026-01-02T03:04:05+00:00\",\"updatedAt\":\"2026-01-02T03:04:05+00:00\"}";
            File.WriteAllText(
                SessionFile(store, session.Id),
                string.Join("\n", new[] { legacyHeader }.Concat(lines.Skip(1))) + "\n");

            var reopened = new JsonlSessionStore(
                store.GetRootDirectory(),
                NullLogger<JsonlSessionStore>.Instance);

            var messages = await reopened.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);

            // A session that never heard of feedback has none — and that is
            // an answer, not a failure.
            var feedback = await reopened.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.IsSuccess).IsTrue();
            await Assert.That(feedback.Value.Ratings).IsEmpty();
            await Assert.That(feedback.Value.Remarks).IsEmpty();
            await Assert.That(feedback.Value.RatingFor(messages.Value[0].Id)).IsNull();
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task RatingAMessageTheSessionDoesNotHold_Fails_AndWritesNothing()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;
            var message = Msg(session.Id, 0);
            await store.AppendMessageAsync(session.Id, message);

            var recorded = await store.RecordMessageRatingAsync(
                session.Id,
                "no-such-message",
                FeedbackRating.Helpful,
                FeedbackCategory.TaskResult);

            await Assert.That(recorded.IsFailure).IsTrue();
            await Assert.That(recorded.Error)
                .IsEqualTo($"Message 'no-such-message' not found in session '{session.Id}'.");

            // Nothing was written: a refused rating leaves no record behind.
            var feedback = await store.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.IsSuccess).IsTrue();
            await Assert.That(feedback.Value.Ratings).IsEmpty();
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task TheNewerRatingOfAMessage_Wins_AndTheOlderOneStaysInTheLog()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;
            var message = Msg(session.Id, 0);
            await store.AppendMessageAsync(session.Id, message);

            var first = await store.RecordMessageRatingAsync(
                session.Id, message.Id, FeedbackRating.Unhelpful, FeedbackCategory.TaskResult);
            var second = await store.RecordMessageRatingAsync(
                session.Id, message.Id, FeedbackRating.Helpful, FeedbackCategory.InstructionFollowing);

            await Assert.That(first.IsSuccess).IsTrue();
            await Assert.That(second.IsSuccess).IsTrue();

            // The effective rating is the newer word.
            var feedback = await store.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.Value.Ratings.Count).IsEqualTo(1);
            await Assert.That(feedback.Value.RatingFor(message.Id)!.Rating).IsEqualTo(FeedbackRating.Helpful);
            await Assert.That(feedback.Value.RatingFor(message.Id)!.Category)
                .IsEqualTo(FeedbackCategory.InstructionFollowing);

            // The older one is not destroyed: the store is append-only, and
            // the log keeps what was said.
            string[] lines = File.ReadAllLines(SessionFile(store, session.Id));
            int ratingLines = lines.Count(line => line.Contains("\"type\":\"messageRating\"", StringComparison.Ordinal));
            await Assert.That(ratingLines).IsEqualTo(2);
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task AMessageWhosePayloadCarriesTheFeedbackType_IsNotTakenForAFeedbackRecord()
    {
        var (store, logger) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;

            // A tool call whose ARGUMENTS are a nested JSON object with the
            // feedback "type". The line carries the literal, so a probe that
            // only asked "does the line contain it?" would drop this message
            // from the history — silently, and for ever.
            using var argsDoc = JsonDocument.Parse("""{"type":"messageRating","id":"not-a-feedback-record"}""");
            var message = TestMessages.AssistantParts([ToolCallPart.Create("tc_1", "read", argsDoc.RootElement)], id: $"msg-{Guid.NewGuid():N}", sessionId: session.Id, stopReason: StopReason.ToolUse, usage: new Usage(1, 1), model: "claude");
            await store.AppendMessageAsync(session.Id, message);

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(messages.Value[0].Id).IsEqualTo(message.Id);
            await Assert.That(logger.Warnings).IsEmpty();
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task ARewind_KeepsTheFeedback()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;
            var kept = Msg(session.Id, 0);
            var dropped = Msg(session.Id, 1);
            await store.AppendMessageAsync(session.Id, kept);
            await store.AppendMessageAsync(session.Id, dropped);

            await store.RecordMessageRatingAsync(
                session.Id, kept.Id, FeedbackRating.Helpful, FeedbackCategory.TaskResult);
            await store.RecordSessionRemarkAsync(session.Id, "worth keeping");

            // The rewind drops MESSAGES. The feedback is not a message: it is
            // what the user said, and the store does not destroy a record
            // that a rewind never claimed to touch.
            var rewound = await store.DeleteMessagesAfterAsync(session.Id, kept.Id);
            await Assert.That(rewound.IsSuccess).IsTrue();
            await Assert.That(rewound.Value).IsEqualTo(1);

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.Value.Count).IsEqualTo(1);

            var feedback = await store.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.IsSuccess).IsTrue();
            await Assert.That(feedback.Value.Ratings.Count).IsEqualTo(1);
            await Assert.That(feedback.Value.RatingFor(kept.Id)).IsNotNull();
            await Assert.That(feedback.Value.Remarks.Count).IsEqualTo(1);
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task ABlankRemark_Fails()
    {
        var (store, logger) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;

            var recorded = await store.RecordSessionRemarkAsync(session.Id, "   ");
            await Assert.That(recorded.IsFailure).IsTrue();
            await Assert.That(recorded.Error).IsEqualTo($"A session remark in session '{session.Id}' must have text.");

            var feedback = await store.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.Value.Remarks).IsEmpty();

            // #199: an expected outcome is a plain failure — no throw, and no
            // error log for something the caller asked for and did not get.
            await Assert.That(logger.Errors).IsEmpty();
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task ARatingWithoutAMessage_Fails()
    {
        var (store, logger) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/feedback", "code", "anthropic", "claude-opus-4")).Value;

            var recorded = await store.RecordMessageRatingAsync(
                session.Id, "   ", FeedbackRating.Helpful, FeedbackCategory.TaskResult);

            await Assert.That(recorded.IsFailure).IsTrue();
            await Assert.That(recorded.Error)
                .IsEqualTo($"A message rating in session '{session.Id}' must name a message.");

            var feedback = await store.GetFeedbackAsync(session.Id);
            await Assert.That(feedback.Value.Ratings).IsEmpty();
            await Assert.That(logger.Errors).IsEmpty();
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task FeedbackOfAnUnknownSession_Fails()
    {
        var (store, _) = CreateStore();
        try
        {
            var read = await store.GetFeedbackAsync("no-such-session");
            await Assert.That(read.IsFailure).IsTrue();
            await Assert.That(read.Error).IsEqualTo("Session 'no-such-session' not found.");

            var remark = await store.RecordSessionRemarkAsync("no-such-session", "hello");
            await Assert.That(remark.IsFailure).IsTrue();
            await Assert.That(remark.Error).IsEqualTo("Session 'no-such-session' not found.");
        }
        finally
        {
            Delete(store);
        }
    }
}
