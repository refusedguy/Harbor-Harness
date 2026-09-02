using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.App.Cli.Commands;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Tests;
/// <summary>
///     PROD-UI-0 З.3 — <c>/model provider/model</c> must rebind the ACTIVE
///     session (IAgent.Initialize with a fresh session + WithModel definition)
///     so the next turn goes through the new model without a REPL restart.
/// </summary>
public class ModelCommandRebindTests
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

    private static AgentDefinition InitialDef() => new(
        Name: AgentName.Create("code"),
        DisplayName: "Code",
        Description: "default coding agent",
        Model: "model-a",
        ProviderId: "prov-a",
        Permission: Harbor.Abstractions.Permissions.PermissionRuleset.Default);

    private static (ModelCommand cmd, FakeAgent agent, InMemoryConfigStore config) Create(
        IAgent? agent, Session? session)
    {
        var config = new InMemoryConfigStore();
        var cmd = new ModelCommand(config, new FakeProviders(), _ => { }, agent, session);
        return (cmd, (FakeAgent)agent!, config);
    }

    [Test]
    public async Task SetModel_WithProviderPrefix_RebindsSessionAndDefinition()
    {
        var agent = new FakeAgent(InitialDef());
        var (cmd, _, config) = Create(agent, BaseSession);

        var result = await cmd.ExecuteAsync(["prov-b/model-b"], MakeCtx(), default);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(config.Current.Model).IsEqualTo("prov-b/model-b");
        await Assert.That(agent.InitializeCalls.Count).IsEqualTo(1);

        var (session, def) = agent.InitializeCalls[0];
        await Assert.That(session.ProviderId).IsEqualTo("prov-b");
        await Assert.That(session.Model).IsEqualTo("model-b");
        await Assert.That(session.Id).IsEqualTo(BaseSession.Id);
        await Assert.That(def.ProviderId).IsEqualTo("prov-b");
        await Assert.That(def.Model).IsEqualTo("model-b");
    }

    [Test]
    public async Task SetModel_BareModelId_KeepsConfiguredProvider()
    {
        var agent = new FakeAgent(InitialDef());
        var config = new InMemoryConfigStore();
        config.Current.Provider = "anthropic";
        var cmd = new ModelCommand(config, new FakeProviders(), _ => { }, agent, BaseSession);

        var result = await cmd.ExecuteAsync(["claude-opus-4"], MakeCtx(), default);

        await Assert.That(result.IsSuccess).IsTrue();
        var (session, def) = agent.InitializeCalls[0];
        await Assert.That(session.ProviderId).IsEqualTo("anthropic");
        await Assert.That(session.Model).IsEqualTo("claude-opus-4");
        await Assert.That(def.Model).IsEqualTo("claude-opus-4");
    }

    [Test]
    public async Task SetModel_SecondRebind_BuildsOnLatestValues()
    {
        var agent = new FakeAgent(InitialDef());
        var (cmd, _, _) = Create(agent, BaseSession);

        await cmd.ExecuteAsync(["prov-b/model-b"], MakeCtx(), default);
        await cmd.ExecuteAsync(["prov-c/model-c"], MakeCtx(), default);

        await Assert.That(agent.InitializeCalls.Count).IsEqualTo(2);
        // The command holds the REPL-start snapshot but only rewrites the two
        // fields it owns — the second rebind still lands on the final values.
        var (session, def) = agent.InitializeCalls[1];
        await Assert.That(session.ProviderId).IsEqualTo("prov-c");
        await Assert.That(def.ProviderId).IsEqualTo("prov-c");
        await Assert.That(def.Model).IsEqualTo("model-c");
    }

    [Test]
    public async Task SetModel_WithoutSessionContext_ConfigOnlyStillSucceeds()
    {
        var cmd = new ModelCommand(new InMemoryConfigStore(), new FakeProviders(), _ => { });

        var result = await cmd.ExecuteAsync(["openai/gpt-4o"], MakeCtx(), default);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task ListPath_DoesNotTouchAgent()
    {
        var agent = new FakeAgent(InitialDef());
        var (cmd, _, _) = Create(agent, BaseSession);

        var result = await cmd.ExecuteAsync(["list"], MakeCtx(), default);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(agent.InitializeCalls.Count).IsEqualTo(0);
    }

    private static SimpleCtx MakeCtx() => new();

    private sealed class SimpleCtx : Harbor.Abstractions.Tui.ICommandContext
    {
        public Harbor.Abstractions.Sessions.ISessionContext Session { get; } = null!;
        public IAgent Agent { get; } = null!;
        public IProviderRegistry Providers { get; } = new FakeProviders();
        public IToolRegistry Tools { get; } = null!;
        public Action<string> Output { get; } = _ => { };
        public Func<string, Task<string>> Prompt { get; } = _ => Task.FromResult(string.Empty);
    }

    /// <summary>Minimal in-memory <see cref="IConfigStore" /> mirroring JsonConfigStore semantics.</summary>
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

    private sealed class FakeProviders : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [];
        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            Result.Failure<ILlmClient>("not registered");
        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));
        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));
        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }
        public Result Unregister(ProviderId providerId) => Result.Failure("n/a");
    }

    /// <summary>Records every Initialize call; otherwise a pass-through shell.</summary>
    private sealed class FakeAgent(AgentDefinition initialDef) : IAgent
    {
        public List<(Session Session, AgentDefinition Definition)> InitializeCalls { get; } = [];

        public AgentState State { get; private set; } = AgentState.Idle("sess-1", initialDef);

        public CancellationTokenSource AbortSource { get; } = new();

        public void Initialize(Session session, AgentDefinition agent)
        {
            InitializeCalls.Add((session, agent));
            State = AgentState.Idle(session.Id, agent);
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

        public void Dispose() => AbortSource.Dispose();

        private sealed class DummySubscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
