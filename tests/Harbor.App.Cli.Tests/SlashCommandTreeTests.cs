using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Repl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using Harbor.TestKit;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     /tree REPL surface: forest rendering over
///     <see cref="Session.ParentSessionId" /> lineage.
/// </summary>
public class SlashCommandTreeTests
{
    private sealed class FakeStore : ISessionStore
    {
        public readonly Dictionary<string, Session> Sessions = [];

        public void Add(Session session) => Sessions[session.Id] = session;

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Session>>([.. Sessions.Values]));

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Sessions.TryGetValue(sessionId, out var s)
                ? Result.Success(s)
                : Result.Failure<Session>($"Session '{sessionId}' not found."));

        // Unused by the tree flow — fail loudly if the runner starts touching them.
        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by /tree tests.");
        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(Result.Success());
    }

    private static Session Make(string id, string title, DateTimeOffset createdAt, string? parent = null) =>
        Session.Create("/harbor-tree-tests", "code", "t", "m") with
        {
            Id = id,
            Title = title,
            CreatedAt = createdAt,
            ParentSessionId = parent,
        };

    [Test]
    public async Task HandleAsync_TreeEmpty_ReportsNoSessions()
    {
        var output = await DispatchAsync("/tree", new FakeStore(), currentSessionId: null);

        await Assert.That(output.Any(l => l.Contains("No sessions."))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_TreeChain_RendersIndentedForestAndMarksCurrent()
    {
        var store = new FakeStore();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.Add(Make("root", "root session", t0));
        store.Add(Make("child", "forked work", t0.AddMinutes(1), parent: "root"));
        store.Add(Make("grandchild", "deep dive", t0.AddMinutes(2), parent: "child"));
        store.Add(Make("sibling", "second fork", t0.AddMinutes(3), parent: "root"));

        var output = await DispatchAsync("/tree", store, currentSessionId: "child");

        await Assert.That(output.Count).IsEqualTo(4);
        await Assert.That(output[0].StartsWith("* root")).IsTrue();
        await Assert.That(output[1]).Contains("child");
        await Assert.That(output[1]).Contains("(current)");
        // Grandchild is nested deeper than its parent.
        int childIndent = output[1].IndexOf("child", StringComparison.Ordinal);
        int grandchildIndent = output[2].IndexOf("grandchild", StringComparison.Ordinal);
        await Assert.That(grandchildIndent).IsGreaterThan(childIndent);
        await Assert.That(output[3]).Contains("sibling");
    }

    [Test]
    public async Task HandleAsync_TreeOrphan_ShownAsRootWithNote()
    {
        var store = new FakeStore();
        store.Add(Make("orphan", "lost branch", DateTimeOffset.UtcNow, parent: "missing-parent"));

        var output = await DispatchAsync("/tree", store, currentSessionId: null);

        await Assert.That(output.Count).IsEqualTo(1);
        await Assert.That(output[0]).Contains("orphan");
        await Assert.That(output[0]).Contains("missing-parent");
    }

    /// <summary>Dispatch through the CellForge renderer-free overload, capturing writer lines.</summary>
    private static async Task<List<string>> DispatchAsync(string input, FakeStore store, string? currentSessionId)
    {
        using var sp = new ServiceCollection()
            .AddSingleton<ISessionStore>(store)
            .AddSingleton<IToolRegistry>(new FakeToolRegistry())
            .BuildServiceProvider();
        var dispatcher = new SlashCommandDispatcher(
            NullLoggerFactory.Instance.CreateLogger<SlashCommandDispatcher>());
        var lines = new List<string>();
        var current = currentSessionId is null
            ? Session.Create("/harbor-tree-tests", "code", "t", "m")
            : Make(currentSessionId, "current", DateTimeOffset.UtcNow);
        var outcome = await dispatcher.HandleCoreAsync(input, sp,
            writer: lines.Add,
            reader: _ => Task.FromResult(string.Empty),
            agent: null!, agentRegistry: null!, configStore: null!, authStore: null!,
            providers: null!, session: current);
        await Assert.That(outcome.ShouldQuit).IsFalse();
        return lines;
    }
}
