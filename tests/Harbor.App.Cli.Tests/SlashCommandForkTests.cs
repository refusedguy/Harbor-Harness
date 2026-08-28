using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     /fork REPL surface: argument guard + happy path through
///     <see cref="Harbor.App.Cli.Commands.SessionForkRunner" />.
/// </summary>
public class SlashCommandForkTests
{
    private sealed class FakeStore : ISessionStore
    {
        public readonly Dictionary<string, Session> Sessions = [];
        public readonly Dictionary<string, List<AgentMessage>> Messages = [];

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        {
            var session = Session.Create(directory, agentName, providerId, modelId);
            Sessions[session.Id] = session;
            Messages[session.Id] = [];
            return Task.FromResult(Result.Success(session));
        }

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Sessions.TryGetValue(sessionId, out var s)
                ? Result.Success(s)
                : Result.Failure<Session>($"Session '{sessionId}' not found."));

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Messages.TryGetValue(sessionId, out var list)
                ? Result.Success<IReadOnlyList<AgentMessage>>([.. list])
                : Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found."));

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            if (!Messages.TryGetValue(sessionId, out var list))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
            list.Add(message);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
            => Task.FromResult(Sessions.ContainsKey(session.Id)
                ? Result.Success()
                : Result.Failure("store rejected update"));

        // Unused by the fork flow — fail loudly if the runner starts touching them.
        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /fork tests.");
        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /fork tests.");
        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /fork tests.");
        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /fork tests.");
        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /fork tests.");
        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(Result.Success());
    }

    [Test]
    public async Task HandleAsync_ForkWithoutArgs_ReportsUsage()
    {
        var output = await DispatchAsync("/fork", new FakeStore());

        await Assert.That(output.Any(l => l.Contains("Usage: /fork"))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_ForkWithBoundary_CopiesPrefixAndReportsNewId()
    {
        var store = new FakeStore();
        var parent = (await store.CreateAsync("/tmp/harbor-fork-repl", "code", "test", "test-model")).Value;
        for (int i = 0; i < 3; i++)
        {
            await store.AppendMessageAsync(parent.Id,
                new UserMessage(Guid.NewGuid().ToString("N"), parent.Id, DateTimeOffset.UtcNow.AddSeconds(i), $"q{i}", "code", "test-model"));
        }
        var history = (await store.GetMessagesAsync(parent.Id)).Value;
        int sessionsBefore = store.Sessions.Count;

        var output = await DispatchAsync($"/fork {parent.Id} {history[1].Id}", store);

        await Assert.That(store.Sessions.Count).IsEqualTo(sessionsBefore + 1);
        await Assert.That(output.Any(l =>
            l.Contains("Forked →") && l.EndsWith(": copied 2 message(s)."))).IsTrue();
    }

    /// <summary>Dispatch through the CellForge renderer-free overload, capturing writer lines.</summary>
    private static async Task<List<string>> DispatchAsync(string input, FakeStore store)
    {
        using var sp = new ServiceCollection()
            .AddSingleton<ISessionStore>(store)
            .BuildServiceProvider();
        var dispatcher = new SlashCommandDispatcher(
            NullLoggerFactory.Instance.CreateLogger<SlashCommandDispatcher>());
        var lines = new List<string>();
        var outcome = await dispatcher.HandleCoreAsync(input, sp,
            writer: lines.Add,
            reader: _ => Task.FromResult(string.Empty),
            agent: null!, agentRegistry: null!, configStore: null!, authStore: null!,
            providers: null!, session: Session.Create("/tmp/harbor-fork-tests", "code", "t", "m"));
        await Assert.That(outcome.ShouldQuit).IsFalse();
        return lines;
    }
}
