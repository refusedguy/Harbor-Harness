using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Configuration;
using Harbor.Registries.Events;
using Harbor.App.Cli.Repl;
using Harbor.Application.Permissions;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
// #436: the art dump reads engine staging grids here (session.Back mirrors
// the flushed frame); painters elsewhere in this file stay Rendering-typed.
using EngineCells = Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// CE-4 E2E-smoke: полный цикл CellForge REPL без реального терминала —
/// scripted stdin (промпт + Enter), mock-агент на настоящем InMemoryEventBus,
/// кадровой цикл раннера, golden grid-dump финального кадра с ответом.
/// </summary>
public class CellForgeReplSmokeTests
{
    private const int Cols = 80;
    private const int Rows = 24;

    [Test]
    public async Task FullTurn_ScriptedInput_MockAgent_GoldenFrame()
    {
        var backend = new AsyncOnlyBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new ScreenSession(writer, Cols, Rows, sizeSource: () => (Cols, Rows));
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "mock/mock-model" };
        var screen = ChatScreen.Build(composer, status);
        // Golden frames must be phase-stable: entrance slide/fade is
        // frame-count dependent, so the smoke run renders settled blocks.
        screen.Timeline.Timeline.DisableEntranceFx();

        var bus = new InMemoryEventBus();
        var agentDef = new AgentDefinition(
            AgentName.Create("code"), "Code", "smoke agent",
            "mock-model", "mock", PermissionRuleset.Default);
        var sessionModel = new Session(
            "ce4-smoke", "proj", Directory.GetCurrentDirectory(), "smoke",
            "code", "mock-model", "mock",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SessionMetadata.Empty);
        using var agent = new ScriptedAgent(bus, sessionModel.Id);
        agent.Initialize(sessionModel, agentDef);

        using var bridge = new ChatScreenBridge(bus, screen.Timeline, status, autoSubscribe: false);

        var stdin = new MemoryStream(Encoding.UTF8.GetBytes("скажи привет\r"));
        using var input = new TerminalInputSource(stdin, new TerminalInputSourceOptions
        {
            SizeProvider = () => (Cols, Rows),
        });

        var configStore = new StubConfigStore();
        var agentRegistry = new FakeAgentRegistry(agentDef);
        var authStore = new AuthStore(configStore);
        var providerRegistry = new FakeProviderRegistry(new ScriptedLlmClient());
        var legacySlash = new LegacySlashRunner(
            new SlashCommandDispatcher(
                NullLogger<SlashCommandDispatcher>.Instance,
                new FakeToolRegistry(),
                new FakeSessionStore(),
                new OnboardingWizard(configStore, authStore),
                new PermissionService(agentRegistry, NullLogger<PermissionService>.Instance)),
            agentRegistry,
            configStore,
            authStore,
            providerRegistry);
        var runner = new CellForgeReplRunner(
            new CellForgeCoreServices(
                configStore, providerRegistry, agentRegistry, authStore, bus, agent,
                legacySlash, NullLogger<CellForgeReplRunner>.Instance),
            new CellForgeOptionalServices(null, null, null, null, null, null, null, null),
            new CellForgeScreens(
                session, screen, bridge, input, backend,
                new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance)),
            sessionModel,
            new NullModeController());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int exitCode = await runner.RunAsync(cts.Token);

        // EOF после простоя — штатный выход.
        await Assert.That(exitCode).IsEqualTo(0);

        string art = Art(session.Back);
        await Assert.That(art).Contains("скажи привет");          // локальное эхо промпта
        await Assert.That(art).Contains("Готово: mock-ответ");     // committed ответ агента
        await Assert.That(art).Contains("mock/mock-model");       // статус-бар

        string expected = Golden.VerifyText(Golden.FixtureDir("celldiff"), "ce4-consoleex-repl", art);
        await Assert.That(art).IsEqualTo(expected);
    }

    /// <summary>
    /// #857: Ctrl+J through the real input path. The keystroke is fed as
    /// bytes on a scripted stdin — kitty CSI-u <c>106;3u</c>, which is what a
    /// terminal sends for Ctrl+J — and parsed by the production
    /// <c>EscapeSequenceParser</c>, so the assertion is on the painted frame,
    /// not on a hand-built <c>KeyEvent</c>.
    /// </summary>
    /// <remarks>
    /// Before this, the same chord produced no visible frame at all: it
    /// resolved to <c>ChatAction.JumpPalette</c>, which toggled a
    /// <c>Center</c>-placed panel that no dock or overlay paints. The test is
    /// therefore written to FAIL on the old behaviour — the breadcrumb the
    /// command pushes did not exist before it — and it is not a guard over a
    /// method it is the sole caller of: this is the product entry point.
    /// </remarks>
    [Test]
    public async Task CtrlJ_PaintsTheJumpPalette()
    {
        var backend = new AsyncOnlyBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new ScreenSession(writer, Cols, Rows, sizeSource: () => (Cols, Rows));
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "mock/mock-model" };
        var screen = ChatScreen.Build(composer, status);
        screen.Timeline.Timeline.DisableEntranceFx();

        var bus = new InMemoryEventBus();
        var agentDef = new AgentDefinition(
            AgentName.Create("code"), "Code", "smoke agent",
            "mock-model", "mock", PermissionRuleset.Default);
        var sessionModel = new Session(
            "ce4-jump", "proj", Directory.GetCurrentDirectory(), "jump smoke",
            "code", "mock-model", "mock",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SessionMetadata.Empty);
        using var agent = new ScriptedAgent(bus, sessionModel.Id);
        agent.Initialize(sessionModel, agentDef);

        using var bridge = new ChatScreenBridge(bus, screen.Timeline, status, autoSubscribe: false);

        // One stored session, so the frame has a row to paint. No git query on
        // this host, which is the documented degradation: sessions only.
        var store = new FakeSessionStore(sessionModel);
        var stdin = new MemoryStream(Encoding.UTF8.GetBytes("\x1b[106;3u"));
        using var input = new TerminalInputSource(stdin, new TerminalInputSourceOptions
        {
            SizeProvider = () => (Cols, Rows),
        });

        var configStore = new StubConfigStore();
        var agentRegistry = new FakeAgentRegistry(agentDef);
        var authStore = new AuthStore(configStore);
        var providerRegistry = new FakeProviderRegistry(new ScriptedLlmClient());
        var legacySlash = new LegacySlashRunner(
            new SlashCommandDispatcher(
                NullLogger<SlashCommandDispatcher>.Instance,
                new FakeToolRegistry(),
                store,
                new OnboardingWizard(configStore, authStore),
                new PermissionService(agentRegistry, NullLogger<PermissionService>.Instance)),
            agentRegistry,
            configStore,
            authStore,
            providerRegistry);
        var runner = new CellForgeReplRunner(
            new CellForgeCoreServices(
                configStore, providerRegistry, agentRegistry, authStore, bus, agent,
                legacySlash, NullLogger<CellForgeReplRunner>.Instance),
            new CellForgeOptionalServices(store, null, null, null, null, null, null, null),
            new CellForgeScreens(
                session, screen, bridge, input, backend,
                new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance)),
            sessionModel,
            new NullModeController());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int exitCode = await runner.RunAsync(cts.Token);

        await Assert.That(exitCode).IsEqualTo(0);
        string art = Art(session.Back);

        // The frame's own breadcrumb and hint footer — both painted by the
        // palette, neither reachable from any other surface in the tree.
        await Assert.That(art).Contains("worktrees / jump").Because(
            "Ctrl+J must push the /jump palette frame. On the pre-#857 path this "
            + "chord toggled a Center-placed panel that no dock or overlay paints, "
            + "so the press produced no visible change at all.");
        await Assert.That(art).Contains("jump smoke").Because(
            "the stored session must appear as a row, which is what makes the "
            + "palette a list rather than an empty box");
        await Assert.That(art).Contains("enter run").Because(
            "the palette's own hint footer, painted only while a frame is open");
    }

    /// <summary>
    ///     PX2 slice 2 (issue #1248): a first run (<c>Onboarded == false</c>)
    ///     enters CellForge with the setup overlay up — no legacy diversion,
    ///     no key press needed. Empty stdin closes input at once; the loop
    ///     then exits idle, and the flow is still active at exit, which is
    ///     the assertion: the overlay opened by itself.
    /// </summary>
    [Test]
    public async Task FirstRun_OpensSetupOverlayWithoutInput()
    {
        var backend = new AsyncOnlyBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new ScreenSession(writer, Cols, Rows, sizeSource: () => (Cols, Rows));
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "mock/mock-model" };
        var screen = ChatScreen.Build(composer, status);
        screen.Timeline.Timeline.DisableEntranceFx();

        var bus = new InMemoryEventBus();
        var agentDef = new AgentDefinition(
            AgentName.Create("code"), "Code", "smoke agent",
            "mock-model", "mock", PermissionRuleset.Default);
        var sessionModel = new Session(
            "ce4-firstrun", "proj", Directory.GetCurrentDirectory(), "first-run smoke",
            "code", "mock-model", "mock",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SessionMetadata.Empty);
        using var agent = new ScriptedAgent(bus, sessionModel.Id);
        agent.Initialize(sessionModel, agentDef);

        using var bridge = new ChatScreenBridge(bus, screen.Timeline, status, autoSubscribe: false);

        var stdin = new MemoryStream(Encoding.UTF8.GetBytes(string.Empty));
        using var input = new TerminalInputSource(stdin, new TerminalInputSourceOptions
        {
            SizeProvider = () => (Cols, Rows),
        });

        var configStore = new StubConfigStore(onboarded: false);
        var agentRegistry = new FakeAgentRegistry(agentDef);
        var authStore = new AuthStore(configStore);
        var providerRegistry = new FakeProviderRegistry(new ScriptedLlmClient());
        var legacySlash = new LegacySlashRunner(
            new SlashCommandDispatcher(
                NullLogger<SlashCommandDispatcher>.Instance,
                new FakeToolRegistry(),
                new FakeSessionStore(),
                new OnboardingWizard(configStore, authStore),
                new PermissionService(agentRegistry, NullLogger<PermissionService>.Instance)),
            agentRegistry,
            configStore,
            authStore,
            providerRegistry);
        var runner = new CellForgeReplRunner(
            new CellForgeCoreServices(
                configStore, providerRegistry, agentRegistry, authStore, bus, agent,
                legacySlash, NullLogger<CellForgeReplRunner>.Instance),
            new CellForgeOptionalServices(null, null, null, null, null, null, null, null),
            new CellForgeScreens(
                session, screen, bridge, input, backend,
                new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance)),
            sessionModel,
            new NullModeController());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int exitCode = await runner.RunAsync(cts.Token);

        await Assert.That(exitCode).IsEqualTo(0)
            .Because("an idle first run with closed stdin exits cleanly — the overlay must not trap shutdown");
        await Assert.That(runner.Onboarding.Active).IsTrue()
            .Because("nobody pressed a key, so an active flow means the lifecycle auto-opened it on first run");
        await Assert.That(runner.Screen.Dialog.Visible).IsTrue()
            .Because("the flow mirrors its dialog onto the screen for painting — active but unpainted is a dead overlay");
    }

    // ── Local test infrastructure (CellForge.Tests helpers are internal to that assembly) ──

    private static string Art(EngineCells.ScreenBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.Width == EngineCells.Cell.WSkip)
                {
                    continue;
                }

                sb.Append(cell.Rune is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFFFD
                          && !char.IsSurrogate((char)cell.Rune)
                    ? ((char)cell.Rune).ToString()
                    : "?");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
    private sealed class ScriptedAgent : IAgent
    {
        private readonly InMemoryEventBus _bus;
        private readonly string _sessionId;

        public ScriptedAgent(InMemoryEventBus bus, string sessionId)
        {
            _bus = bus;
            _sessionId = sessionId;
            var placeholderDef = new AgentDefinition(
                AgentName.Create("unbound"), "Unbound", "pre-init",
                "mock-model", "mock", PermissionRuleset.Default);
            _state = AgentState.Idle(sessionId, placeholderDef);
        }

        public CancellationToken AbortToken => _abortSource.Token;
        public void RequestAbort() => _abortSource.Cancel();
        private readonly CancellationTokenSource _abortSource = new();
        // #559: Maybe-shaped, like the interface. This double deliberately
        // pre-binds a placeholder definition (it drives the REPL smoke path, which
        // requires a bound agent), so the projection never yields None here.
        public Maybe<AgentState> State => Maybe.From(_state);

        private AgentState _state;

        public void Initialize(Session session, AgentDefinition agent)
        {
            _state = AgentState.Idle(session.Id, agent);
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) =>
            _bus.Subscribe(listener);

        public async Task<Result> PromptAsync(string text, CancellationToken ct = default)
        {
            _state = _state with { IsRunning = true };
            try
            {
                var user = TestMessages.User(text, sessionId: _sessionId, model: "mock-model");
                await _bus.PublishAsync(TestEvents.AgentStart(_sessionId, [user])).ConfigureAwait(false);

                var partial = AssistantMessage.Empty(_sessionId, "mock-model");
                await _bus.PublishAsync(TestEvents.MessageStart(partial)).ConfigureAwait(false);
                await _bus.PublishAsync(TestEvents.TextDeltaUpdate("0", "Готово: mock-ответ\n", partial)).ConfigureAwait(false);

                var final = TestMessages.Assistant("Готово: mock-ответ", sessionId: _sessionId, usage: new Usage(12, 5), model: "mock-model");
                await _bus.PublishAsync(TestEvents.MessageEnd(final)).ConfigureAwait(false);

                await _bus.PublishAsync(TestEvents.SessionStats(_sessionId, new SessionMetadata(Cost: 0.0001m, TokensInput: 12, TokensOutput: 5, TokensReasoning: 0, TokensCacheRead: 0, TokensCacheWrite: 0, MessageCount: 2, TimeCompacting: null))).ConfigureAwait(false);

                await _bus.PublishAsync(TestEvents.AgentEnd()).ConfigureAwait(false);
                return Result.Success();
            }
            finally
            {
                _state = _state with { IsRunning = false };
            }
        }

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) =>
            PromptAsync(message.Content, ct);

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource()
        {
        }

        public void Steer(AgentMessage message)
        {
        }

        public void Dispose() => _abortSource.Dispose();
    }

    private sealed class StubConfigStore : IConfigStore
    {
        // Onboarded by default (issue #1248, slice 2): the smoke runs pin
        // REPL turns, not first-run, and a first run now opens the setup
        // overlay — which would own the scripted stdin before the composer
        // ever sees it. The first-run overlay has its own test below, which
        // is the one caller that passes onboarded: false.
        private readonly bool _onboarded;

        public StubConfigStore(bool onboarded = true) => _onboarded = onboarded;

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
        {
            HarborConfig config = HarborConfig.Default;
            config.Onboarded = _onboarded;
            return Task.FromResult(Result.Success(config));
        }

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public async Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
        {
            _ = updater(HarborConfig.Default);
            await Task.CompletedTask.ConfigureAwait(false);
            return Result.Success();
        }

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<string>("not available in smoke harness"));
    }

    // ── Local test infrastructure (CellForge.Tests helpers are internal to that assembly) ──
}
