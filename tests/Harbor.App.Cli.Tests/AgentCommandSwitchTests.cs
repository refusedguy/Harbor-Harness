using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.App.Cli.Commands;
using Harbor.Application.Configuration;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     PX5 slice 1 (#1251) — <c>/agent</c> (<c>/mode</c>) is a real switch:
///     the name validates against the registry, the live agent rebinds without
///     a restart (the <c>/model</c> pattern), and the host is told so the
///     status-bar indicator can move. Unknown names are refused, never persisted.
/// </summary>
public class AgentCommandSwitchTests
{
    private static readonly Session BaseSession = new(
        Id: "sess-1",
        ProjectId: "proj",
        Directory: "workdir",
        Title: "t",
        Agent: "code",
        Model: "model-a",
        ProviderId: "prov-a",
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Metadata: new SessionMetadata(0m, 0, 0, 0, 0, 0, 0, null));

    private static FakeAgentRegistry Registry() => new(
        AgentDefinition.CodeDefault("model", "provider"),
        AgentDefinition.AskDefault("model", "provider"));

    private static AgentDefinition CodeDef() => AgentDefinition.CodeDefault("model-a", "prov-a");

    [Test]
    public async Task SwitchToKnownMode_RebindsLiveAgentAndNotifiesIndicator()
    {
        var agent = new FakeAgent(CodeDef());
        var config = new InMemoryConfigStore();
        string? switched = null;
        var lines = new List<string>();
        var cmd = new AgentCommand(config, Registry(), lines.Add, agent, BaseSession, name => switched = name);

        var result = await cmd.ExecuteAsync(["ask"], MakeCtx(), default);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(config.Current.Agent).IsEqualTo("ask");
        await Assert.That(agent.InitializeCalls.Count).IsEqualTo(1);

        var (session, def) = agent.InitializeCalls[0];
        await Assert.That(session.Agent).IsEqualTo("ask");
        await Assert.That(session.Id).IsEqualTo(BaseSession.Id);
        await Assert.That(def.Name).IsEqualTo(AgentName.Create("ask"));
        await Assert.That(switched).IsEqualTo("ask");
        await Assert.That(lines.Any(l => l.Contains("Switched to agent: ask"))).IsTrue();
    }

    [Test]
    public async Task SwitchToUnknownMode_RefusesAndKeepsEverything()
    {
        var agent = new FakeAgent(CodeDef());
        var config = new InMemoryConfigStore();
        string before = config.Current.Agent;
        string? switched = null;
        var lines = new List<string>();
        var cmd = new AgentCommand(config, Registry(), lines.Add, agent, BaseSession, name => switched = name);

        var result = await cmd.ExecuteAsync(["nope"], MakeCtx(), default);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(config.Current.Agent).IsEqualTo(before);
        await Assert.That(agent.InitializeCalls.Count).IsEqualTo(0);
        await Assert.That(switched).IsNull();
        await Assert.That(lines.Any(l => l.Contains("Unknown agent"))).IsTrue();
    }

    [Test]
    public async Task NoArgs_ListsFromRegistry_DoesNotTouchAgent()
    {
        var agent = new FakeAgent(CodeDef());
        var lines = new List<string>();
        var cmd = new AgentCommand(new InMemoryConfigStore(), Registry(), lines.Add, agent, BaseSession, null);

        var result = await cmd.ExecuteAsync([], MakeCtx(), default);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("Available agents:"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("ask"))).IsTrue();
        await Assert.That(agent.InitializeCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SwitchWithoutLiveAgent_PersistsConfigOnly()
    {
        var config = new InMemoryConfigStore();
        var cmd = new AgentCommand(config, Registry(), _ => { });

        var result = await cmd.ExecuteAsync(["ask"], MakeCtx(), default);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(config.Current.Agent).IsEqualTo("ask");
    }

    private static SimpleCtx MakeCtx() => new();

    private sealed class SimpleCtx : ICommandContext
    {
        public Harbor.Abstractions.Sessions.ISessionContext Session { get; } = null!;
        public IAgent Agent { get; } = null!;
        public IProviderRegistry Providers { get; } = null!;
        public IToolRegistry Tools { get; } = null!;
        public Action<string> Output { get; } = _ => { };
        public Func<string, Task<string>> Prompt { get; } = _ => Task.FromResult(string.Empty);
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        public HarborConfig Current { get; set; } = HarborConfig.Default;

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(Current));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
        {
            Current = config;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
        {
            Current = updater(Current);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<string>("not set"));
    }

    private sealed class FakeAgent(AgentDefinition initialDef) : IAgent
    {
        public List<(Session Session, AgentDefinition Definition)> InitializeCalls { get; } = [];

        public Maybe<AgentState> State { get; private set; } = Maybe.From(AgentState.Idle("sess-1", initialDef));

        public CancellationToken AbortToken => _abortSource.Token;
        public void RequestAbort() => _abortSource.Cancel();
        private readonly CancellationTokenSource _abortSource = new();

        public void Initialize(Session session, AgentDefinition agent)
        {
            InitializeCalls.Add((session, agent));
            State = Maybe.From(AgentState.Idle(session.Id, agent));
        }

        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) =>
            new DummySubscription();

        public void Steer(AgentMessage message) { }

        public void ResetAbortSource() { }

        public void Dispose() => _abortSource.Dispose();

        private sealed class DummySubscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
