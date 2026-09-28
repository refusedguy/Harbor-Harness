using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     Tests for [UX6] #266 — child cost propagation into the parent message +
///     stats (serialized per parent session) and the resumable-id trailer on
///     failures. The shared <c>FakeSessionStore</c> is single-session, so this
///     file carries its own multi-session fake.
/// </summary>
public class SubAgentCostPropagationTests
{
    private static AgentDefinition SubAgent(string name = "explore") => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Empty,
        20,
        IsSubAgent: true);

    private static AssistantMessage AssistantWithUsage(string text, Usage usage) => new(
        Guid.NewGuid().ToString("N"),
        "child",
        DateTimeOffset.UtcNow,
        [new TextPart(text)],
        StopReason.Stop,
        usage,
        "test-model");

    private static AssistantMessage ParentMessage(string sessionId, string id, Usage usage) => new(
        id,
        sessionId,
        DateTimeOffset.UtcNow,
        [new TextPart("thought"), ToolCallPart.Create("call-1", "task", System.Text.Json.JsonDocument.Parse("{}").RootElement)],
        StopReason.ToolUse,
        usage,
        "test-model");

    /// <summary>Loop fake appending scripted replies, then returning the outcome.</summary>
    private sealed class CostLoop : IAgentLoop
    {
        private readonly Result? _outcome;
        private readonly AgentMessage[] _replies;

        public CostLoop(Result? outcome, params AgentMessage[] replies)
        {
            _outcome = outcome;
            _replies = replies;
        }

        public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            foreach (AgentMessage message in _replies)
                await session.AppendMessageAsync(message, ct).ConfigureAwait(false);
            return _outcome ?? Result.Success();
        }
    }

    /// <summary>
    ///     Multi-session store fake: per-id message lists, persisted stats and
    ///     applied message updates, with call recording for assertions.
    /// </summary>
    private sealed class CostFakeStore : ISessionStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<AgentMessage>> _messages = new();
        private readonly Dictionary<string, Session> _sessions = new();
        private readonly Dictionary<string, SessionMetadata> _stats = new();

        public List<(string SessionId, AgentMessage Message)> UpdatedMessages { get; } = [];
        public List<(string SessionId, SessionMetadata Metadata)> UpdatedStats { get; } = [];
        public List<Session> CreatedSessions { get; } = [];

        public void Seed(Session session, params AgentMessage[] messages)
        {
            lock (_lock)
            {
                _sessions[session.Id] = session;
                _messages[session.Id] = [.. messages];
                _stats[session.Id] = SessionMetadata.Empty;
            }
        }

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        {
            var child = Session.Create(directory, agentName, providerId, modelId);
            lock (_lock)
            {
                _sessions[child.Id] = child;
                _messages[child.Id] = [];
                _stats[child.Id] = SessionMetadata.Empty;
                CreatedSessions.Add(child);
            }

            return Task.FromResult(Result.Success(child));
        }

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_sessions.TryGetValue(sessionId, out var session)
                    ? Result.Success(session)
                    : Result.Failure<Session>($"Session '{sessionId}' was not found."));
            }
        }

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(Result.Success<IReadOnlyList<Session>>([.. _sessions.Values]));
            }
        }

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_messages.TryGetValue(sessionId, out var list))
                    return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
                list.Add(message);
                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_messages.TryGetValue(sessionId, out var list))
                    return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Id == message.Id)
                    {
                        list[i] = message;
                        break;
                    }
                }

                UpdatedMessages.Add((sessionId, message));
                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_messages.TryGetValue(sessionId, out var list)
                    ? Result.Success<IReadOnlyList<AgentMessage>>([.. list])
                    : Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' was not found."));
            }
        }

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _sessions[session.Id] = session;
                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_stats.TryGetValue(sessionId, out var metadata)
                    ? Result.Success(metadata)
                    : Result.Failure<SessionMetadata>($"Session '{sessionId}' was not found."));
            }
        }

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_stats.ContainsKey(sessionId))
                    return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
                _stats[sessionId] = metadata;
                UpdatedStats.Add((sessionId, metadata));
                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<int>("DeleteMessagesAfter is not supported by this test fake."));
    }

    private static Session NewParent()
    {
        var parent = Session.Create(Harbor.TestKit.TestTempDirs.NewDirectory("harbor-ux6-parent"), "code", "test", "test-model");
        return parent;
    }

    [Test]
    public async Task RunAsync_Success_ReturnsAggregatedChildUsage()
    {
        var parent = NewParent();
        var store = new CostFakeStore();
        store.Seed(parent);
        var loop = new CostLoop(
            null,
            AssistantWithUsage("first", new Usage(100, 50)),
            AssistantWithUsage("second", new Usage(30, 20, ReasoningTokens: 7, CacheReadTokens: 11)));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go", ParentSessionId: parent.Id));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ChildUsage!).IsEqualTo(new Usage(130, 70, 7, 11, null));
        await Assert.That(result.Value.HasUsage).IsTrue();
    }

    [Test]
    public async Task RunAsync_Success_PropagatesDeltaIntoParentMessage()
    {
        var parent = NewParent();
        var parentMsg = ParentMessage(parent.Id, "parent-msg-1", new Usage(10, 5));
        var store = new CostFakeStore();
        store.Seed(parent, parentMsg);
        var loop = new CostLoop(null, AssistantWithUsage("done", new Usage(100, 50)));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(
            SubAgent(),
            new SubAgentRunRequest("go", ParentSessionId: parent.Id, ParentMessageId: parentMsg.Id));

        await Assert.That(result.IsSuccess).IsTrue();
        var reread = await store.GetMessagesAsync(parent.Id);
        AssistantMessage? updated = null;
        foreach (var message in reread.Value)
        {
            if (message is AssistantMessage assistant && assistant.Id == parentMsg.Id)
                updated = assistant;
        }

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Usage).IsEqualTo(new Usage(110, 55));
    }

    [Test]
    public async Task RunAsync_Success_PropagatesDeltaIntoParentStats()
    {
        var parent = NewParent();
        var store = new CostFakeStore();
        store.Seed(parent);
        var loop = new CostLoop(null, AssistantWithUsage("done", new Usage(100, 50, ReasoningTokens: 3)));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go", ParentSessionId: parent.Id));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(store.UpdatedStats.Count).IsEqualTo(1);
        await Assert.That(store.UpdatedStats[0].SessionId).IsEqualTo(parent.Id);
        await Assert.That(store.UpdatedStats[0].Metadata.TokensInput).IsEqualTo(100);
        await Assert.That(store.UpdatedStats[0].Metadata.TokensOutput).IsEqualTo(50);
        await Assert.That(store.UpdatedStats[0].Metadata.TokensReasoning).IsEqualTo(3);
    }

    [Test]
    public async Task RunAsync_LoopFailure_ReturnsResumeTrailerWithSessionId()
    {
        var parent = NewParent();
        var store = new CostFakeStore();
        store.Seed(parent);
        var loop = new CostLoop(Result.Failure("model exploded"));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go", ParentSessionId: parent.Id));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(store.CreatedSessions.Count).IsEqualTo(1);
        string childId = store.CreatedSessions[0].Id;
        await Assert.That(result.Error).Contains($"[resume-session:{childId}]");
        var hint = SubAgentFailureFormat.TryExtractResumeHint(result.Error, "explore");
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.SessionId).IsEqualTo(childId);
        await Assert.That(hint.AgentName).IsEqualTo("explore");
    }

    [Test]
    public async Task RunAsync_LoopFailure_PropagatesPartialUsage()
    {
        var parent = NewParent();
        var parentMsg = ParentMessage(parent.Id, "parent-msg-1", new Usage(10, 5));
        var store = new CostFakeStore();
        store.Seed(parent, parentMsg);
        var loop = new CostLoop(
            Result.Failure("model exploded"),
            AssistantWithUsage("partial work", new Usage(40, 10)));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(
            SubAgent(),
            new SubAgentRunRequest("go", ParentSessionId: parent.Id, ParentMessageId: parentMsg.Id));

        await Assert.That(result.IsFailure).IsTrue();
        var reread = await store.GetMessagesAsync(parent.Id);
        AssistantMessage? updated = null;
        foreach (var message in reread.Value)
        {
            if (message is AssistantMessage assistant && assistant.Id == parentMsg.Id)
                updated = assistant;
        }

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Usage).IsEqualTo(new Usage(50, 15));
    }

    [Test]
    public async Task RunAsync_ZeroUsage_SkipsParentWrites()
    {
        var parent = NewParent();
        var parentMsg = ParentMessage(parent.Id, "parent-msg-1", new Usage(10, 5));
        var store = new CostFakeStore();
        store.Seed(parent, parentMsg);
        var loop = new CostLoop(null, AssistantWithUsage("ok", new Usage(0, 0)));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(
            SubAgent(),
            new SubAgentRunRequest("go", ParentSessionId: parent.Id, ParentMessageId: parentMsg.Id));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.HasUsage).IsFalse();
        await Assert.That(store.UpdatedMessages.Count).IsEqualTo(0);
        await Assert.That(store.UpdatedStats.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_ParallelChildren_ParentTotalsAreExact()
    {
        const int children = 8;
        var parent = NewParent();
        var parentMsg = ParentMessage(parent.Id, "parent-msg-1", new Usage(0, 0));
        var store = new CostFakeStore();
        store.Seed(parent, parentMsg);
        var loop = new CostLoop(null, AssistantWithUsage("done", new Usage(10, 5)));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var tasks = new List<Task<Result<SubAgentRunResult>>>(children);
        for (int i = 0; i < children; i++)
            tasks.Add(runner.RunAsync(SubAgent(), new SubAgentRunRequest("go", ParentSessionId: parent.Id, ParentMessageId: parentMsg.Id)));
        Result<SubAgentRunResult>[] outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        for (int i = 0; i < outcomes.Length; i++)
            await Assert.That(outcomes[i].IsSuccess).IsTrue();
        var reread = await store.GetMessagesAsync(parent.Id);
        AssistantMessage? updated = null;
        foreach (var message in reread.Value)
        {
            if (message is AssistantMessage assistant && assistant.Id == parentMsg.Id)
                updated = assistant;
        }

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Usage).IsEqualTo(new Usage(10 * children, 5 * children));
        var stats = await store.GetStatsAsync(parent.Id);
        await Assert.That(stats.Value.TokensInput).IsEqualTo(10 * children);
        await Assert.That(stats.Value.TokensOutput).IsEqualTo(5 * children);
    }
}
