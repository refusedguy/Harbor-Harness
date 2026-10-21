using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Hosting;
using Harbor.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Issue #1118 — <c>maxSteps</c> in the home config is the step budget of
///     the run the REPL starts.
/// </summary>
/// <remarks>
///     <para>
///         <b>The defect.</b> The configured value was persisted, printed by
///         <c>harbor config</c> and settable through <c>/config maxsteps</c> —
///         and it reached no agent at all. The agents the product runs are
///         declared by the <c>AgentDefinition.*Default</c> factories at the
///         composition root, each carrying its own <c>MaxSteps</c>, and the
///         configured value was never projected onto them. The REPL therefore
///         ran to the AGENT's default (50 turns for <c>code</c>) whatever the
///         config said: the PTY scenarios of #1057 seeded
///         <c>"maxSteps": 1</c> into the home config and still watched the same
///         tool call execute dozens of times.
///     </para>
///     <para>
///         <b>What is asserted here.</b> The whole chain, in one place: the
///         config the CLI reads at startup → the agent the run starts with →
///         the number of turns the run takes. The last one is the assertion
///         that matters, and it is deterministic — the budget is a comparison
///         (<c>MaxStepsBehavior.IsExhausted</c>), not a deadline — so the run
///         ends at exactly the configured number of turns on any machine.
///     </para>
///     <para>
///         <b>No provider key, no network.</b> The LLM is a scripted client
///         that always asks for a tool, so only the budget can end the run —
///         the same shape <c>RunLimitStampingTests</c> uses at the loop level.
///     </para>
/// </remarks>
public class ReplMaxStepsBudgetTests
{
    /// <summary>A home config that names no budget — the default every install has.</summary>
    private const string ConfigWithoutBudget = """
        {
          "provider": "test",
          "model": "test/test-model",
          "agent": "code",
          "onboarded": true
        }
        """;

    /// <summary>
    ///     The home config of the #1057 PTY scenarios: one turn, please.
    /// </summary>
    private const string ConfigWithBudget = """
        {
          "provider": "test",
          "model": "test/test-model",
          "agent": "code",
          "onboarded": true,
          "maxSteps": 1
        }
        """;

    /// <summary>
    ///     A home config carrying the DEFAULT budget — the shape every config
    ///     save leaves behind (<c>HarborConfig.ToRaw</c> always writes it), and
    ///     therefore the shape most installs already have.
    /// </summary>
    private const string ConfigWithDefaultBudget = """
        {
          "provider": "test",
          "model": "test/test-model",
          "agent": "code",
          "onboarded": true,
          "maxSteps": 50
        }
        """;

    [Test]
    public async Task ConfiguredMaxSteps_CapsTheAgentTheRunStartsWith()
    {
        using ServiceProvider provider = ComposeHost(ConfigWithBudget);
        IAgentRegistry registry = provider.GetRequiredService<IAgentRegistry>();

        await Assert.That(AgentNamed(registry, "code").MaxSteps).IsEqualTo(1).Because(
            "the configured budget is the budget of the run the REPL starts: the agent it "
            + "resolves from the registry must carry it, or the key is a decoration.");
    }

    [Test]
    public async Task ConfiguredMaxSteps_StopsTheRunAfterTheConfiguredNumberOfTurns()
    {
        using ServiceProvider provider = ComposeHost(ConfigWithBudget);
        IAgentRegistry registry = provider.GetRequiredService<IAgentRegistry>();
        AgentDefinition agent = AgentNamed(registry, "code");

        // A provider that never stops asking for tools, so only the budget can
        // end the run.
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: """{"n":1}"""),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
            }
        ]);
        var bus = new FakeEventBus();
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(new CountingTool()),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);

        var session = new LoopSessionContext(Session.Create("/tmp/harbor-1118", "code", "test", "test-model"));
        var result = await loop.RunAsync(session, agent);

        // `Result` here means "the loop reached a terminal event without a
        // provider failure" — NOT "the work was finished". A run cut off by the
        // budget is a limit stop, and it says so on the terminal event.
        await Assert.That(result.IsSuccess).IsTrue();

        // ONE turn. Before the fix the same run took the agent's own 50 turns,
        // which is the flood the #1057 PTY scenarios recorded.
        await Assert.That(client.StreamCalls).IsEqualTo(1).Because(
            "the configured budget is the budget of the run: with maxSteps 1 the loop asks "
            + "the model once and stops. It used to keep asking until the agent's own "
            + "default (50) ran out.");

        AgentEndEvent end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.MaxSteps).Because(
            "a run stopped by the step budget must say so — a limit that leaves no trace is "
            + "indistinguishable from a run that finished.");
    }

    [Test]
    public async Task NoConfiguredBudget_LeavesEveryAgentItsOwnBudget()
    {
        using ServiceProvider provider = ComposeHost(ConfigWithoutBudget);
        IAgentRegistry registry = provider.GetRequiredService<IAgentRegistry>();

        await Assert.That(AgentNamed(registry, "code").MaxSteps).IsEqualTo(50);
        await Assert.That(AgentNamed(registry, "plan").MaxSteps).IsEqualTo(100);
        await Assert.That(AgentNamed(registry, "explore").MaxSteps).IsEqualTo(20);
    }

    [Test]
    public async Task TheDefaultBudgetInTheFile_LeavesEveryAgentItsOwnBudget()
    {
        // The value every config save writes into config.json. Honouring it as
        // an override would silently halve the 100-step agents for every user
        // who never touched the key — which is why the default reads as "not
        // configured".
        using ServiceProvider provider = ComposeHost(ConfigWithDefaultBudget);
        IAgentRegistry registry = provider.GetRequiredService<IAgentRegistry>();

        await Assert.That(AgentNamed(registry, "code").MaxSteps).IsEqualTo(50);
        await Assert.That(AgentNamed(registry, "plan").MaxSteps).IsEqualTo(100);
        await Assert.That(AgentNamed(registry, "explore").MaxSteps).IsEqualTo(20);
    }

    /// <summary>
    ///     The agent <paramref name="name" /> names in the registry.
    /// </summary>
    private static AgentDefinition AgentNamed(IAgentRegistry registry, string name) =>
        registry.GetAllAgents().First(a => a.Name.Value == name);

    /// <summary>
    ///     Compose the real host against a home config, the way the CLI does.
    /// </summary>
    private static ServiceProvider ComposeHost(string homeConfig)
    {
        string harborDir = Path.Combine(Path.GetTempPath(), "harbor-1118-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(harborDir);
        string configPath = Path.Combine(harborDir, "config.json");
        File.WriteAllText(configPath, homeConfig);

        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = harborDir,
            ConfigPath = configPath,
            DefaultStorageBackend = "memory",
        });
        return services.BuildServiceProvider();
    }

    /// <summary>The session the loop runs against.</summary>
    private sealed class LoopSessionContext(Session session) : ISessionContext
    {
        private readonly List<AgentMessage> _messages = [];

        public Session Session { get; } = session;

        public IReadOnlyList<AgentMessage> Messages => _messages;

        public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) =>
            Task.CompletedTask;

        /// <summary>
        ///     #1765: the durability checkpoint. This double keeps every record
        ///     in memory and loses none of them, so its state survives as far
        ///     as the double is concerned.
        /// </summary>
        public Task<Result> CheckpointAsync(string reason, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    /// <summary>A token tracker that never compacts and never counts.</summary>
    private sealed class FakeTokenTracker : ITokenTracker
    {
        public void RecordTurnUsage(Usage usage)
        {
        }

        public int Estimate(string text) => 0;

        public int EstimateMessage(AgentMessage message) => 0;

        public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;

        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

        public TokenStats GetStats() => new(0, 0, null, null, null);
    }

    /// <summary>A compaction service this test never exercises.</summary>
    private sealed class FakeCompactionService : ICompactionService
    {
        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

        public Task<Result<CompactionResult>> CompactAsync(
            string sessionId,
            IReadOnlyList<AgentMessage> messages,
            ModelInfo model,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<CompactionResult>("compaction is not exercised by this test"));
    }
}
