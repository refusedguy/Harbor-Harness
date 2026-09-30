// ReplRunnerConfigLoadTests.cs — the #602 regression tests.
//
// THE BUG
// -------
// `IConfigStore.LoadAsync` genuinely fails. `JsonConfigStore.LoadCore` returns
// `Failure("config.json is corrupt: …")` when the file does not parse, and the
// ordinary way to get there is a config.json somebody hand-edited into invalid
// JSON (a trailing comma is enough). `Result<T>.Value` on a failure THROWS
// `ResultFailureException(Error)` — it does not return a default.
//
// `ReplRunner.RunAskAsync` opened by binding the loaded result straight to
// `.Value` — the `var config = (await _configStore.LoadAsync(…)).Value;` shape —
// so `harbor ask` — the single most-run command in the harness (AGENTS.md §E2E
// documents it as the smoke test) — died with an unhandled exception whose type
// is neither IOException nor JsonException, on stderr, with the parser's own
// message thrown away. `RunInteractiveAsync` had the same read after the
// onboarding wizard, and `DemoCommand` had it too.
//
// CFE0001 did not catch it, and that is structural rather than noise: the rule
// resolves the RECEIVER of `.Value` to an `IPropertySymbol` on Result, and the
// receiver of `(await …).Value` is an AwaitExpression with no symbol. See
// `UnguardedResultReadRules` for the guard that closes that hole.
//
// WHAT IS ASSERTED, AND WHY IT IS NOT JUST "it does not throw"
// ------------------------------------------------------------
// A `GetValueOrDefault(HarborConfig.Default)` would also stop the crash, and it
// would be WORSE: the session would then run on the default provider and model,
// silently, which is not the one the user configured. So these tests pin the
// whole contract —
//   * a corrupt config exits non-zero,
//   * the message the user sees is the one JsonConfigStore composed, and it
//     names the file,
//   * and the prompt is NEVER forwarded to the agent.
// The last one is the assertion that distinguishes "reported" from "silently
// defaulted", and it is the reason exit code alone is not enough.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Application.Permissions;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #602 — a config the harness cannot read must be reported, not thrown on and
///     not silently defaulted around.
/// </summary>
public class ReplRunnerConfigLoadTests
{
    /// <summary>
    ///     A trailing comma. This is byte-for-byte the shape a user produces by
    ///     hand-editing config.json, and it is what makes `JsonSerializer` throw.
    /// </summary>
    private const string CorruptJson = """
        {
          "provider": "kilocode",
          "model": "kilocode/kilo-auto/free",
        }
        """;

    // ── `harbor ask` ─────────────────────────────────────────────────────────

    /// <summary>
    ///     The literal repro from the issue, driven through the REAL
    ///     <see cref="JsonConfigStore" /> over a real file — not a fake that
    ///     returns a failure someone wrote by hand. Before the fix this threw
    ///     <see cref="ResultFailureException" /> out of the method, so the test
    ///     failed with that exception rather than with an assertion.
    /// </summary>
    [Test]
    public async Task RunAskAsync_CorruptConfigJson_ReportsTheParserErrorAndExitsOne()
    {
        string path = WriteCorruptConfig();
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var agent = new RecordingAgent();
            var logger = new RecordingLogger();
            ReplRunner runner = CreateRunner(new CapturingRenderer(), agent, new FakeAgentRegistry(TestAgents.AllowAll()), store, logger);

            int exitCode = await runner.RunAskAsync("hi").ConfigureAwait(false);

            await Assert.That(exitCode).IsEqualTo(1)
                .Because("a config the harness cannot read is a failed run, not a run on defaults");

            await Assert.That(logger.Errors.Any(m => m.Contains("config.json is corrupt"))).IsTrue()
                .Because(
                    "the diagnostic JsonConfigStore already composed — and logged — was thrown away by "
                    + "the crash. The user must see it instead of an exception type, and it names the "
                    + "file and carries the parser's own position (Path/LineNumber/BytePosition), "
                    + "which is what a hand-edit needs. Logged: " + string.Join(" | ", logger.Errors));

            await Assert.That(agent.Prompts).IsEmpty()
                .Because(
                    "This is the assertion that separates 'reported' from 'silently defaulted'. A "
                    + "GetValueOrDefault(HarborConfig.Default) would satisfy the exit code and this "
                    + "line would still be empty — while the prompt ran against a provider and model "
                    + "the user never chose.");
        }
        finally
        {
            DeleteTempFile(path);
        }
    }

    /// <summary>
    ///     Same file, interactive entry point. A bare `harbor` must not die either.
    /// </summary>
    [Test]
    public async Task RunInteractiveAsync_CorruptConfigJson_ReportsTheParserErrorAndExitsOne()
    {
        string path = WriteCorruptConfig();
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var renderer = new CapturingRenderer();
            var agent = new RecordingAgent();
            ReplRunner runner = CreateRunner(renderer, agent, new FakeAgentRegistry(TestAgents.AllowAll()), store);

            int exitCode = await runner.RunInteractiveAsync().ConfigureAwait(false);

            await Assert.That(exitCode).IsEqualTo(1)
                .Because("the interactive entry point reads the same file through the same store");

            await Assert.That(renderer.Lines.Any(l => l.Contains("config.json is corrupt"))).IsTrue()
                .Because(
                    "the diagnostic must reach the user. The interactive path has no stderr "
                    + "contract, so the renderer is the channel; the strings are captured above: "
                    + string.Join(" | ", renderer.Lines));

            await Assert.That(agent.Prompts).IsEmpty()
                .Because("no prompt is ever sent on this path, so nothing here should have run");
        }
        finally
        {
            DeleteTempFile(path);
        }
    }

    /// <summary>
    ///     The post-wizard reload. The wizard reports success only if its write
    ///     returned success, so a failure HERE means the file is unreadable even
    ///     after onboarding — and continuing would start the session on
    ///     <c>HarborConfig.Default</c>, discarding exactly what the user just
    ///     answered. This is the read that used to be a bare <c>.Value</c>.
    /// </summary>
    [Test]
    public async Task RunInteractiveAsync_ReloadAfterWizardFails_ReportsItAndExitsOne()
    {
        const string ReloadError = "config.json is corrupt: the post-onboarding write did not land";

        // First read: fine, but onboarding was never completed — which is what
        // sends the REPL into the wizard. Second read: broken.
        var store = new ScriptedConfigStore(
            Result.Success(Configured(onboarded: false)),
            Result.Failure<HarborConfig>(ReloadError));

        var renderer = new CapturingRenderer(
            // ollama (no API key needed), default model, first agent.
            Maybe.From("ollama"),
            Maybe.From(string.Empty),
            Maybe.From("1"));

        var agent = new RecordingAgent();
        ReplRunner runner = CreateRunner(renderer, agent, new FakeAgentRegistry(TestAgents.AllowAll()), store);

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            int exitCode = await runner.RunInteractiveAsync().ConfigureAwait(false);

            await Assert.That(exitCode).IsEqualTo(1)
                .Because("a config that is still unreadable after onboarding must not be papered over");

            await Assert.That(renderer.Lines.Any(l => l.Contains("the post-onboarding write did not land"))).IsTrue()
                .Because(
                    "the reload's own message is the one that explains what just happened. Captured: "
                    + string.Join(" | ", renderer.Lines));

            await Assert.That(agent.Prompts).IsEmpty()
                .Because("the run stops at the reload; no prompt may be sent");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        }
    }

    // ── The empty agent registry — the second half of #602 ───────────────────

    /// <summary>
    ///     <c>GetAllAgents()[0]</c> threw <see cref="ArgumentOutOfRangeException" />
    ///     on an empty registry. An empty registry is the state of a fresh install
    ///     and of a failed plugin-load pass, so this is reachable, not theoretical.
    /// </summary>
    [Test]
    public async Task RunAskAsync_EmptyAgentRegistry_ReportsAndExitsOneInsteadOfThrowing()
    {
        var store = new ScriptedConfigStore(Result.Success(Configured()), Result.Success(Configured()));
        var agent = new RecordingAgent();
        var logger = new RecordingLogger();
        ReplRunner runner = CreateRunner(new CapturingRenderer(), agent, new FakeAgentRegistry(), store, logger);

        int exitCode = await runner.RunAskAsync("hi").ConfigureAwait(false);

        await Assert.That(exitCode).IsEqualTo(1)
            .Because("nothing can run without an agent; that is a failure to report, not an index to throw on");

        await Assert.That(logger.Errors.Any(m => m.Contains("No agents are registered"))).IsTrue()
            .Because("an exception type is not a diagnosis. Logged: " + string.Join(" | ", logger.Errors));

        await Assert.That(agent.Prompts).IsEmpty();
    }

    [Test]
    public async Task RunInteractiveAsync_EmptyAgentRegistry_ReportsAndExitsOneInsteadOfThrowing()
    {
        var store = new ScriptedConfigStore(Result.Success(Configured()), Result.Success(Configured()));
        var renderer = new CapturingRenderer();
        var agent = new RecordingAgent();
        ReplRunner runner = CreateRunner(renderer, agent, new FakeAgentRegistry(), store);

        int exitCode = await runner.RunInteractiveAsync().ConfigureAwait(false);

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(renderer.Lines.Any(l => l.Contains("No agents are registered"))).IsTrue()
            .Because("captured: " + string.Join(" | ", renderer.Lines));
    }

    // ── What must NOT change: the paths that work today ──────────────────────

    /// <summary>
    ///     A missing config file is NOT a failure — <see cref="JsonConfigStore" />
    ///     answers <c>Success(HarborConfig.Default)</c> and the wizard takes over.
    ///     Hard-failing the load indiscriminately would break the first-run
    ///     experience, so this pins the distinction.
    /// </summary>
    [Test]
    public async Task RunAskAsync_MissingConfigFile_StillRunsOnDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-602-{Guid.NewGuid():N}.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var agent = new RecordingAgent();
        ReplRunner runner = CreateRunner(new CapturingRenderer(), agent, new FakeAgentRegistry(TestAgents.AllowAll()), store);

        int exitCode = await runner.RunAskAsync("hi").ConfigureAwait(false);

        await Assert.That(exitCode).IsEqualTo(0)
            .Because("absent is not corrupt. Defaults plus onboarding is the first-run path and must survive");
        await Assert.That(agent.Prompts.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     The happy path with a config that names a real agent — the guard
    ///     against the #602 fix turning every run into a failure.
    /// </summary>
    [Test]
    public async Task RunAskAsync_ConfigNamesARegisteredAgent_UsesItAndSucceeds()
    {
        var store = new ScriptedConfigStore(Result.Success(Configured(agent: "plan")), Result.Success(Configured(agent: "plan")));
        var agent = new RecordingAgent();
        ReplRunner runner = CreateRunner(
            new CapturingRenderer(),
            agent,
            new FakeAgentRegistry(TestAgents.AllowAll(name: "code"), TestAgents.AllowAll(name: "plan")),
            store);

        int exitCode = await runner.RunAskAsync("hi").ConfigureAwait(false);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(agent.Prompts.Count).IsEqualTo(1);
        await Assert.That(agent.InitializedAgentName).IsEqualTo("plan")
            .Because("the configured agent wins over the registry's first entry");
    }

    /// <summary>
    ///     The fallback arm, which the <c>Or</c> in the resolver carries: a config
    ///     naming an agent that is not registered falls back to the first one
    ///     rather than throwing or finding nothing.
    /// </summary>
    [Test]
    public async Task RunAskAsync_ConfigNamesAnUnregisteredAgent_FallsBackToTheFirstRegistered()
    {
        var store = new ScriptedConfigStore(Result.Success(Configured(agent: "does-not-exist")), Result.Success(Configured(agent: "does-not-exist")));
        var agent = new RecordingAgent();
        ReplRunner runner = CreateRunner(
            new CapturingRenderer(),
            agent,
            new FakeAgentRegistry(TestAgents.AllowAll(name: "code"), TestAgents.AllowAll(name: "plan")),
            store);

        int exitCode = await runner.RunAskAsync("hi").ConfigureAwait(false);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(agent.InitializedAgentName).IsEqualTo("code")
            .Because("absent means the registry's first agent — the behaviour GetAllAgents()[0] used to provide, minus the throw");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static ReplRunner CreateRunner(
        ITuiRenderer renderer,
        IAgent agent,
        IAgentRegistry agentRegistry,
        IConfigStore configStore,
        RecordingLogger? logger = null)
    {
        var authStore = new AuthStore(configStore);
        var wizard = new OnboardingWizard(configStore, authStore);

        // #486: the dispatcher is built by whoever would be the composition root.
        // None of these tests dispatch a slash command — the fakes satisfy the
        // dispatcher's required ctor params, which ReplRunner used to carry.
        var slashes = new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            new FakeSessionStore(),
            wizard,
            new PermissionService(agentRegistry, NullLogger<PermissionService>.Instance));

        return new ReplRunner(
            logger ?? (ILogger<ReplRunner>)NullLogger<ReplRunner>.Instance,
            configStore,
            authStore,
            wizard,
            renderer,
            new FakeEventBus(),
            agent,
            new FakeSessionStore(),
            agentRegistry,
            new FakeProviderRegistry(new ScriptedLlmClient()),
            slashes,
            NullLogger<CellForgeReplRunner>.Instance,
            pluginReload: null,
            rendererPipeline: null,
            tokens: null,
            // The CellForge screen graph is only built on the CellForge path, and
            // every test here runs with the default TUI mode. A throwing stub is
            // the honest assertion that it is not.
            cellForgeScreens: static () => throw new InvalidOperationException(
                "This test must not resolve the CellForge screen graph."),
            rendererHost: new EmptyServiceProvider());
    }

    /// <summary>
    ///     A config the stores accept, in whichever onboarding state the test needs.
    ///     <paramref name="onboarded" /> defaults to true so the REPL does not launch
    ///     the wizard; the post-wizard test is the one that asks for false.
    /// </summary>
    private static HarborConfig Configured(string agent = "code", bool onboarded = true)
    {
        HarborConfig config = HarborConfig.Default;
        config.Agent = agent;
        config.Onboarded = onboarded;
        return config;
    }

    private static string WriteCorruptConfig()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-602-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, CorruptJson);
        return path;
    }

    private static void DeleteTempFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A store whose loads are scripted in order and then repeat, and whose
    ///     writes always succeed. That split is deliberate: the post-wizard reload
    ///     case needs a write that reports success followed by a read that fails,
    ///     which a store modelled on <c>JsonConfigStore</c>'s
    ///     load-mutate-save would not produce.
    /// </summary>
    private sealed class ScriptedConfigStore : IConfigStore
    {
        private readonly Queue<Result<HarborConfig>> _loads;

        public ScriptedConfigStore(Result<HarborConfig> first, Result<HarborConfig> then)
        {
            // Enqueue, not a collection initializer: Queue<T> has no public Add,
            // so `{ first, then }` does not compile.
            _loads = new Queue<Result<HarborConfig>>();
            _loads.Enqueue(first);
            _loads.Enqueue(then);
        }

        public List<HarborConfig> Saved { get; } = [];

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(_loads.Count > 1 ? _loads.Dequeue() : _loads.Peek());

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
        {
            Saved.Add(config);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
        {
            Saved.Add(updater(HarborConfig.Default));
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<string>($"No API key for '{providerId}' in config.json"));
    }

    /// <summary>Records every line the REPL writes, and can script the answers it reads.</summary>
    private sealed class CapturingRenderer : ITuiRenderer
    {
        private readonly Queue<Maybe<string>> _answers;

        public CapturingRenderer(params Maybe<string>[] answers) => _answers = new Queue<Maybe<string>>(answers);

        public List<string> Lines { get; } = [];

        public ITuiRenderContext Context { get; } = new CaptureRenderContext();

        public ViewRegistry Views { get; } = new();

        public ViewModelRegistry ViewModels { get; } = new();

        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task RenderAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : Maybe<string>.None);

        public Task<Result> WriteAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
        {
            Lines.Add(text ?? string.Empty);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ClearAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public void Dispose() { }
    }

    /// <summary>Records what the REPL forwarded, and which agent it initialized.</summary>
    private sealed class RecordingAgent : IAgent
    {
        private readonly CancellationTokenSource _abortSource = new();

        public List<string> Prompts { get; } = [];

        public string? InitializedAgentName { get; private set; }

        public CancellationToken AbortToken => _abortSource.Token;

        public void RequestAbort() => _abortSource.Cancel();

        // #559: Maybe-shaped, like the interface. This double is never initialized
        // (the tests assert on Initialize calls, not on state), so it throws if
        // anything ever reads it — which is the honest signal.
        public Maybe<AgentState> State => throw new NotSupportedException("Not used in these tests.");

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NopDisposable();

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
        {
            Prompts.Add(message.Content);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> PromptAsync(string text, CancellationToken ct = default)
        {
            Prompts.Add(text);
            return Task.FromResult(Result.Success());
        }

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource() { }

        public void Initialize(Session session, AgentDefinition agent) => InitializedAgentName = agent.Name.Value;

        public void Steer(AgentMessage message) { }

        public void Dispose() => _abortSource.Dispose();

        private sealed class NopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>
    ///     Resolves nothing — the REPL only forwards the host to renderers. A plain
    ///     instance rather than a static singleton: it is stateless, and a static
    ///     <c>IServiceProvider</c> is DI006.
    /// </summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    ///     Captures the REPL's error log.
    /// </summary>
    /// <remarks>
    ///     This exists instead of redirecting <see cref="Console.Error" />. TUnit
    ///     runs test classes in parallel inside one process, so a global writer swap
    ///     races every other test's output for the duration of the call (TUnit0055
    ///     says exactly this) — the assertions below would be about a buffer that
    ///     other tests are also writing into. The stderr write itself is one line
    ///     next to an identical, pre-existing one for session-creation failure, so
    ///     what needs pinning here is that the message is composed and reported,
    ///     not which stream it lands on.
    /// </remarks>
    private sealed class RecordingLogger : ILogger<ReplRunner>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
