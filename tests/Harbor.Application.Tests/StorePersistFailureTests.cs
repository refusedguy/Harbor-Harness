using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.Application.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     F14 (deep2-core): the session-store Result from AppendMessageAsync was
///     ignored in PromptAsync — a failed persist let the run continue on stale
///     context while memory/disk/model silently diverged. The run must now fail
///     up front with the storage error surfaced.
/// </summary>
public class StorePersistFailureTests
{
    private sealed class FailingAppendStore(Session session) : ISessionStore
    {
        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(session));

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(session));

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<Session>>([session]));

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Failure("disk full (simulated)"));

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([]));

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    [Test]
    public async Task PromptAsync_StoreAppendFails_RunFailsWithoutStarting()
    {
        var session = Session.Create("/tmp/harbor-persist-failure", "code", "test", "test-model");
        var loop = new FakeAgentLoop();
        var agent = new DefaultAgent(
            new FailingAppendStore(session),
            loop,
            new FakeEventBus(),
            NullLogger<DefaultAgent>.Instance);
        try
        {
            agent.Initialize(session, new AgentDefinition(
                AgentName.Create("code"),
                "Code",
                "persist-failure harness",
                "test-model",
                "test",
                new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) })));

            var result = await agent.PromptAsync("will not be persisted");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("Failed to persist");
            // The agent loop must never see a run whose history is missing its prompt.
            await Assert.That(loop.Runs).IsEqualTo(0);
        }
        finally
        {
            agent.Dispose();
        }
    }
}
