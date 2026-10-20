using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The keyless session-log tier (#1740): a log that is meant to be
///     committed carries no provider credential, and says on its face that it
///     was written in that tier.
/// </summary>
/// <remarks>
///     <para>
///         The assertions are made against the CONTENT OF THE FILE, not
///         against the store's return value: the whole point of the tier is
///         what the file holds, and a redactor that worked on the in-memory
///         message would leave the file dirty.
///     </para>
/// </remarks>
public class SessionLogTierTests
{
    /// <summary>
    ///     A credential whose SHAPE is public knowledge — the shape rules have
    ///     to catch it on their own.
    /// </summary>
    private const string ShapedKey = "sk-testkey-0123456789abcdef";

    /// <summary>
    ///     A credential whose shape is NOT public knowledge — only the value
    ///     the process holds can catch it. This is the case a shape rule
    ///     cannot reach.
    /// </summary>
    private const string OpaqueKey = "provider-secret-value-4242";

    private static JsonlSessionStore CreateStore(SessionLogTier tier, params string[] literalSecrets)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-tier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance, tier, literalSecrets);
    }

    private static async Task<(string SessionId, string FilePath)> SeedAsync(JsonlSessionStore store)
    {
        var session = (await store.CreateAsync("/test/dir", "code", "anthropic", "claude-opus-4")).Value;
        string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
        return (session.Id, path);
    }

    private static async Task AppendTheWholeTranscriptAsync(JsonlSessionStore store, string sessionId)
    {
        var user = new UserMessage(
            "msg-user",
            sessionId,
            DateTimeOffset.UtcNow,
            $"my key is {ShapedKey} and the other one is {OpaqueKey}",
            "code",
            "claude-opus-4");
        await Assert.That((await store.AppendMessageAsync(sessionId, user)).IsSuccess).IsTrue();

        using var args = JsonDocument.Parse(
            $$"""
            {"command":"curl -H 'Authorization: Bearer {{ShapedKey}}' https://api.example.com"}
            """);
        var assistant = new AssistantMessage(
            "msg-assistant",
            sessionId,
            DateTimeOffset.UtcNow,
            [new TextPart($"using {OpaqueKey} now"), new ToolCallPart.Create("call-1", "bash", args.RootElement)],
            StopReason.ToolUse,
            new Usage(10, 5),
            "claude-opus-4");
        await Assert.That((await store.AppendMessageAsync(sessionId, assistant)).IsSuccess).IsTrue();

        var toolResult = new ToolResultMessage(
            "msg-tool",
            sessionId,
            DateTimeOffset.UtcNow,
            [new ToolResultEntry("call-1", "bash", $"the file holds {ShapedKey}", false)]);
        await Assert.That((await store.AppendMessageAsync(sessionId, toolResult)).IsSuccess).IsTrue();
    }

    [Test]
    public async Task KeylessTier_TheFileHoldsNoCredential()
    {
        var store = CreateStore(SessionLogTier.Keyless, OpaqueKey);
        try
        {
            var (sessionId, path) = await SeedAsync(store);
            await AppendTheWholeTranscriptAsync(store, sessionId);

            string raw = await File.ReadAllTextAsync(path);

            // The two shapes of credential: the one a rule can recognise, and
            // the one only the process's own value can.
            await Assert.That(raw).DoesNotContain(ShapedKey);
            await Assert.That(raw).DoesNotContain(OpaqueKey);

            // And the log says what it is: both placeholders are visible.
            await Assert.That(raw).Contains(SecretRedactor.ApiKeyPlaceholder);
            await Assert.That(raw).Contains(SecretRedactor.SecretPlaceholder);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task KeylessTier_EveryTextBearingFieldIsScrubbed()
    {
        var store = CreateStore(SessionLogTier.Keyless, OpaqueKey);
        try
        {
            var (sessionId, path) = await SeedAsync(store);
            await AppendTheWholeTranscriptAsync(store, sessionId);

            string[] lines = (await File.ReadAllTextAsync(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // The USER message, the ASSISTANT text, the TOOL CALL arguments and
            // the TOOL RESULT are four different fields, and a rule that only
            // covered one of them would leave the credential in the log.
            string user = lines[1];
            string assistant = lines[2];
            string tool = lines[3];

            await Assert.That(user).DoesNotContain(ShapedKey);
            await Assert.That(user).DoesNotContain(OpaqueKey);
            await Assert.That(assistant).DoesNotContain(OpaqueKey);
            await Assert.That(tool).DoesNotContain(ShapedKey);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task KeylessTier_TheHeaderSaysTheLogIsShareable()
    {
        var store = CreateStore(SessionLogTier.Keyless);
        try
        {
            var (_, path) = await SeedAsync(store);

            string header = (await File.ReadAllTextAsync(path)).Split('\n')[0];

            await Assert.That(header).Contains("\"sharing\":\"keyless\"");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task LocalTier_TheFileKeepsTheTextAndTheHeaderStaysAsItWas()
    {
        var store = CreateStore(SessionLogTier.Local);
        try
        {
            var (sessionId, path) = await SeedAsync(store);
            await AppendTheWholeTranscriptAsync(store, sessionId);

            string raw = await File.ReadAllTextAsync(path);

            // The local tier is the DEFAULT and must change nothing: the
            // credential is there (the log never leaves the machine) and the
            // header carries no tier marker at all.
            await Assert.That(raw).Contains(ShapedKey);
            await Assert.That(raw).DoesNotContain("\"sharing\"");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task KeylessTier_ReadingTheLogBackDoesNotFail()
    {
        var store = CreateStore(SessionLogTier.Keyless, OpaqueKey);
        try
        {
            var (sessionId, _) = await SeedAsync(store);
            await AppendTheWholeTranscriptAsync(store, sessionId);

            // The ordinary read path: a session log written in the keyless tier
            // is a session log like any other. Reading it must not fail, and
            // what comes back is the SCRUBBED text — which is what a replay
            // needs, because the replay sees exactly what the file holds.
            var session = await store.GetAsync(sessionId);
            await Assert.That(session.IsSuccess).IsTrue();
            await Assert.That(session.Value.Model).IsEqualTo("claude-opus-4");

            var messages = await store.GetMessagesAsync(sessionId);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(3);

            var user = (UserMessage)messages.Value[0];
            await Assert.That(user.Content).DoesNotContain(ShapedKey);
            await Assert.That(user.Content).DoesNotContain(OpaqueKey);
            await Assert.That(user.Content).Contains(SecretRedactor.ApiKeyPlaceholder);

            // The provider and the model are NOT credentials: a log that does
            // not say which model produced it cannot be replayed against
            // anything.
            await Assert.That(session.Value.ProviderId).IsEqualTo("anthropic");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task KeylessTier_AnEditedMessageIsScrubbedToo()
    {
        var store = CreateStore(SessionLogTier.Keyless);
        try
        {
            var (sessionId, path) = await SeedAsync(store);

            var edited = new UserMessage(
                "msg-user",
                sessionId,
                DateTimeOffset.UtcNow,
                $"the key is {ShapedKey}",
                "code",
                "claude-opus-4");
            await Assert.That((await store.AppendMessageAsync(sessionId, edited)).IsSuccess).IsTrue();

            // An edit is a second write of the same record: it must be scrubbed
            // on the way out too, or the rewrite would put the credential back
            // into a log that had already removed it.
            var compactionSummary = new UserMessage(
                "msg-user",
                sessionId,
                DateTimeOffset.UtcNow,
                $"summary: the key was {ShapedKey}",
                "code",
                "claude-opus-4");
            await Assert.That((await store.UpdateMessageAsync(sessionId, compactionSummary)).IsSuccess).IsTrue();

            string raw = await File.ReadAllTextAsync(path);
            await Assert.That(raw).DoesNotContain(ShapedKey);
            await Assert.That(raw).Contains(SecretRedactor.ApiKeyPlaceholder);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task KeylessTier_TheTitleAndTheFeedbackNoteAreScrubbedToo()
    {
        var store = CreateStore(SessionLogTier.Keyless);
        try
        {
            var (sessionId, path) = await SeedAsync(store);
            await Assert.That((await store.AppendMessageAsync(sessionId, new UserMessage(
                "msg-user",
                sessionId,
                DateTimeOffset.UtcNow,
                "hello",
                "code",
                "claude-opus-4"))).IsSuccess).IsTrue();

            // The title is the caller's text, and it travels with every
            // rewrite of the header.
            var session = await store.GetAsync(sessionId);
            await Assert.That(session.IsSuccess).IsTrue();
            await Assert.That((await store.UpdateAsync(
                session.Value with { Title = $"the key is {ShapedKey}" })).IsSuccess).IsTrue();

            // The feedback note is free text the user typed, and it leaves the
            // machine with the rest of the log.
            var rated = await store.RecordMessageRatingAsync(
                sessionId,
                "msg-user",
                FeedbackRating.Helpful,
                FeedbackCategory.TaskResult,
                $"the key is {ShapedKey}");
            await Assert.That(rated.IsSuccess).IsTrue();

            string raw = await File.ReadAllTextAsync(path);
            await Assert.That(raw).DoesNotContain(ShapedKey);
            await Assert.That(raw).Contains(SecretRedactor.ApiKeyPlaceholder);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task KeylessTier_CountsAndPathsSurviveTheScrub()
    {
        var store = CreateStore(SessionLogTier.Keyless);
        try
        {
            var (sessionId, path) = await SeedAsync(store);

            var message = new UserMessage(
                "msg-user",
                sessionId,
                DateTimeOffset.UtcNow,
                "max_tokens=4096 prompt_tokens=12 token_count=3 the file is src/Harbor.Storage.Jsonl/SessionLogTier.cs",
                "code",
                "claude-opus-4");
            await Assert.That((await store.AppendMessageAsync(sessionId, message)).IsSuccess).IsTrue();

            string raw = await File.ReadAllTextAsync(path);

            // A count is not a credential: the names that merely CONTAIN a
            // credential word are the common case in a transcript, and
            // redacting them would make the log unreadable.
            await Assert.That(raw).Contains("max_tokens=4096");
            await Assert.That(raw).Contains("token_count=3");
            await Assert.That(raw).Contains("src/Harbor.Storage.Jsonl/SessionLogTier.cs");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
