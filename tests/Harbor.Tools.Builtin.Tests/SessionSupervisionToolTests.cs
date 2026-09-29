using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for the peer-supervision tools (#165): <see cref="SessionReadTool" />
///     (read-only snapshot) and <see cref="SessionSteerTool" /> (approved
///     message/redirect/restart delivery with self + depth-1 guards).
/// </summary>
public class SessionSupervisionToolTests
{
    private static Session Peer(string id, SessionStatus status = SessionStatus.Idle) =>
        Session.Create("/tmp/harbor-supervision", "code", "test", "test-model") with
        {
            Id = id,
            Title = "Peer " + id,
            Status = status
        };

    private static UserMessage User(string sessionId, string content) => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    private static AssistantMessage Assistant(string sessionId, string text, StopReason reason = StopReason.Stop) => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        new ContentPart[] { new TextPart(text) },
        reason,
        new Usage(1, 1),
        "test-model");

    private static ToolContext Context(string sessionId, IReadOnlyList<AgentMessage>? messages = null) => new(
        sessionId,
        "message-1",
        "call-1",
        "code",
        CancellationToken.None,
        messages ?? Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));

    private static JsonElement Args(Dictionary<string, object?> values) =>
        JsonDocument.Parse(JsonSerializer.Serialize(values)).RootElement.Clone();

    [Test]
    public async Task Read_Name_IsSessionRead()
    {
        var tool = new SessionReadTool(new FakeSessionStore(), NullLogger<SessionReadTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("session_read");
        await Assert.That(tool.ExecutionMode == ExecutionMode.Parallel).IsTrue();
    }

    [Test]
    public async Task Steer_Name_IsSessionSteer()
    {
        var tool = new SessionSteerTool(new FakeSessionStore(), NullLogger<SessionSteerTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("session_steer");
        await Assert.That(tool.ExecutionMode == ExecutionMode.Sequential).IsTrue();
    }

    [Test]
    public async Task Read_Validate_MissingId_Fails()
    {
        var tool = new SessionReadTool(new FakeSessionStore(), NullLogger<SessionReadTool>.Instance);
        await Assert.That(tool.ValidateArguments(Args(new())).IsFailure).IsTrue();
        await Assert.That(tool.ValidateArguments(Args(new() { ["limit"] = 99 })).IsFailure).IsTrue();
    }

    [Test]
    public async Task Steer_Validate_BadArgs_Fail()
    {
        var tool = new SessionSteerTool(new FakeSessionStore(), NullLogger<SessionSteerTool>.Instance);
        await Assert.That(tool.ValidateArguments(Args(new())).IsFailure).IsTrue();
        await Assert.That(tool.ValidateArguments(Args(new() { ["id"] = "p", ["instruction"] = "  " })).IsFailure).IsTrue();
        await Assert.That(tool.ValidateArguments(
            Args(new() { ["id"] = "p", ["instruction"] = "go", ["operation"] = "nuke" })).IsFailure).IsTrue();
        await Assert.That(tool.ValidateArguments(
            Args(new() { ["id"] = "p", ["instruction"] = "go", ["operation"] = "restart" })).IsSuccess).IsTrue();
    }

    [Test]
    public async Task Read_UnknownSession_Error()
    {
        var tool = new SessionReadTool(new FakeSessionStore(), NullLogger<SessionReadTool>.Instance);
        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "nope" }), Context("caller-1"));
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }

    [Test]
    public async Task Read_KnownSession_ReturnsStatusOutcomeTranscript()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1"));
        store.SeedMessage("peer-1", User("peer-1", "implement feature X"));
        store.SeedMessage("peer-1", Assistant("peer-1", "done, all green"));
        var tool = new SessionReadTool(store, NullLogger<SessionReadTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1" }), Context("caller-1"));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("status: idle");
        await Assert.That(result.Output).Contains("outcome: succeeded");
        await Assert.That(result.Output).Contains("[user] implement feature X");
        await Assert.That(result.Output).Contains("done, all green");
    }

    [Test]
    public async Task Read_EmptyHistory_OutcomeUnknown()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1"));
        var tool = new SessionReadTool(store, NullLogger<SessionReadTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1" }), Context("caller-1"));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("outcome: unknown");
        await Assert.That(result.Output).Contains("(empty transcript)");
    }

    [Test]
    public async Task Read_FailedRun_OutcomeFailed()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1", SessionStatus.Error));
        store.SeedMessage("peer-1", Assistant("peer-1", "boom", StopReason.Error));
        var tool = new SessionReadTool(store, NullLogger<SessionReadTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1" }), Context("caller-1"));

        await Assert.That(result.Output).Contains("outcome: failed");
        await Assert.That(result.Output).Contains("status: error");
    }

    [Test]
    public async Task Read_Limit_TruncatesTail()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1"));
        store.SeedMessage("peer-1", User("peer-1", "first-msg"));
        store.SeedMessage("peer-1", User("peer-1", "second-msg"));
        var tool = new SessionReadTool(store, NullLogger<SessionReadTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1", ["limit"] = 1 }), Context("caller-1"));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("second-msg");
        await Assert.That(result.Output.Contains("first-msg")).IsFalse();
    }

    [Test]
    public async Task Read_ShowsSteerAuthors()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1"));
        store.SeedMessage("peer-1", User("peer-1", "[peer message from boss-9]: keep going [steer-from:boss-9]"));
        var tool = new SessionReadTool(store, NullLogger<SessionReadTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1" }), Context("caller-1"));

        await Assert.That(result.Output).Contains("steered by: boss-9");
    }

    [Test]
    public async Task Read_DetachedStore_HonestError()
    {
        var tool = new SessionReadTool(new DeferredSessionStore(), NullLogger<SessionReadTool>.Instance);
        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1" }), Context("caller-1"));
        await Assert.That(result.IsError).IsTrue();
    }

    [Test]
    public async Task Steer_SelfSteer_Refused()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("me"));
        var tool = new SessionSteerTool(store, NullLogger<SessionSteerTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "me", ["instruction"] = "do better" }), Context("me"));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("own session");
        await Assert.That(store.Appended.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Steer_SupervisorSteer_Refused_Depth1()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("boss-9"));
        store.Seed(Peer("peer-2"));
        var tool = new SessionSteerTool(store, NullLogger<SessionSteerTool>.Instance);
        var callerHistory = new AgentMessage[]
        {
            User("caller-1", "[peer message from boss-9]: scout ahead [steer-from:boss-9]")
        };

        ToolResult refused = await tool.ExecuteAsync(
            Args(new() { ["id"] = "boss-9", ["instruction"] = "stop" }), Context("caller-1", callerHistory));

        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(refused.Output).Contains("depth is 1");

        ToolResult allowed = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-2", ["instruction"] = "keep going" }), Context("caller-1", callerHistory));

        await Assert.That(allowed.IsError).IsFalse();
    }

    [Test]
    public async Task Steer_UnknownSession_Error()
    {
        var tool = new SessionSteerTool(new FakeSessionStore(), NullLogger<SessionSteerTool>.Instance);
        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "ghost", ["instruction"] = "hi" }), Context("caller-1"));
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }

    [Test]
    public async Task Steer_Message_AppendsWithTrailer()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1"));
        var tool = new SessionSteerTool(store, NullLogger<SessionSteerTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1", ["instruction"] = "tests are green" }), Context("caller-1"));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("delivered");
        await Assert.That(store.Appended.Count).IsEqualTo(1);
        UserMessage delivered = store.Appended[0];
        await Assert.That(delivered.SessionId).IsEqualTo("peer-1");
        await Assert.That(delivered.Content).Contains("tests are green");
        await Assert.That(delivered.Content).Contains("[steer-from:caller-1]");
    }

    [Test]
    public async Task Steer_Redirect_And_Restart_Prefixes()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1"));
        var tool = new SessionSteerTool(store, NullLogger<SessionSteerTool>.Instance);

        ToolResult redirect = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1", ["instruction"] = "switch to Y", ["operation"] = "redirect" }),
            Context("caller-1"));
        await Assert.That(redirect.IsError).IsFalse();
        await Assert.That(store.Appended[0].Content).Contains("peer redirect");

        ToolResult restart = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1", ["instruction"] = "run again", ["operation"] = "restart" }),
            Context("caller-1"));
        await Assert.That(restart.IsError).IsFalse();
        await Assert.That(restart.Output).Contains("nothing was aborted");
        await Assert.That(store.Appended[1].Content).Contains("peer restart");
    }

    [Test]
    public async Task Steer_WorkingPeer_NotesNoInterrupt()
    {
        var store = new FakeSessionStore();
        store.Seed(Peer("peer-1", SessionStatus.Working));
        var tool = new SessionSteerTool(store, NullLogger<SessionSteerTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args(new() { ["id"] = "peer-1", ["instruction"] = "heads up" }), Context("caller-1"));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("not interrupted");
    }

    [Test]
    public async Task DefaultRules_ReadAllow_SteerAsk()
    {
        await Assert.That(PermissionRuleset.Default.Evaluate("session_read", "peer-1") == PermissionAction.Allow).IsTrue();
        await Assert.That(PermissionRuleset.Default.Evaluate("session_steer", "peer-1") == PermissionAction.Ask).IsTrue();
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AgentMessage>> _messages = new(StringComparer.Ordinal);

        public List<UserMessage> Appended { get; } = new();

        public void Seed(Session session) => _sessions[session.Id] = session;

        public void SeedMessage(string sessionId, AgentMessage message)
        {
            if (!_messages.TryGetValue(sessionId, out List<AgentMessage>? list))
            {
                list = new();
                _messages[sessionId] = list;
            }

            list.Add(message);
        }

        public Task<CSharpFunctionalExtensions.Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_sessions.TryGetValue(sessionId, out Session? s)
                ? CSharpFunctionalExtensions.Result.Success(s)
                : CSharpFunctionalExtensions.Result.Failure<Session>($"Session '{sessionId}' was not found."));

        public Task<CSharpFunctionalExtensions.Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(
            string sessionId, CancellationToken ct = default)
        {
            if (!_sessions.ContainsKey(sessionId))
                return Task.FromResult(CSharpFunctionalExtensions.Result.Failure<IReadOnlyList<AgentMessage>>(
                    $"Session '{sessionId}' was not found."));
            _messages.TryGetValue(sessionId, out List<AgentMessage>? list);
            IReadOnlyList<AgentMessage> snapshot = list is null
                ? Array.Empty<AgentMessage>()
                : list.ToArray();
            return Task.FromResult(CSharpFunctionalExtensions.Result.Success(snapshot));
        }

        public Task<CSharpFunctionalExtensions.Result> AppendMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            if (!_sessions.ContainsKey(sessionId))
                return Task.FromResult(CSharpFunctionalExtensions.Result.Failure($"Session '{sessionId}' was not found."));
            SeedMessage(sessionId, message);
            if (message is UserMessage user)
                Appended.Add(user);
            return Task.FromResult(CSharpFunctionalExtensions.Result.Success());
        }

        public Task<CSharpFunctionalExtensions.Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Failure<Session>("Not supported by fake."));

        public Task<CSharpFunctionalExtensions.Result<IReadOnlyList<Session>>> ListAsync(
            string? projectId = null, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Success<IReadOnlyList<Session>>(
                new List<Session>(_sessions.Values)));

        public Task<CSharpFunctionalExtensions.Result> UpdateMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Success());

        public Task<CSharpFunctionalExtensions.Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Success());

        public Task<CSharpFunctionalExtensions.Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Success());

        public Task<CSharpFunctionalExtensions.Result<SessionMetadata>> GetStatsAsync(
            string sessionId, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Success(SessionMetadata.Empty));

        public Task<CSharpFunctionalExtensions.Result> UpdateStatsAsync(
            string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Success());

        public Task<CSharpFunctionalExtensions.Result<int>> DeleteMessagesAfterAsync(
            string sessionId, string messageId, CancellationToken ct = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Failure<int>("Not supported by fake."));
    }
}
