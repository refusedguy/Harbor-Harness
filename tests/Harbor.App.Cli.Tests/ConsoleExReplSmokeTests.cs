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
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// CE-4 E2E-smoke: полный цикл ConsoleEx REPL без реального терминала —
/// scripted stdin (промпт + Enter), mock-агент на настоящем InMemoryEventBus,
/// кадровой цикл раннера, golden grid-dump финального кадра с ответом.
/// </summary>
public class ConsoleExReplSmokeTests
{
    private const int Cols = 80;
    private const int Rows = 24;

    [Test]
    public async Task FullTurn_ScriptedInput_MockAgent_GoldenFrame()
    {
        var backend = new FrameCaptureBackend();
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

        var services = new MapServiceProvider
        {
            [typeof(IEventBus)] = bus,
            [typeof(IConfigStore)] = new StubConfigStore(),
        };
        var runner = new ConsoleExReplRunner(
            services, agent, sessionModel, session, screen, bridge, input,
            new NullModeController(), backend, NullLogger<ConsoleExReplRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int exitCode = await runner.RunAsync(cts.Token);

        // EOF после простоя — штатный выход.
        await Assert.That(exitCode).IsEqualTo(0);

        string art = Art(session.Back);
        await Assert.That(art).Contains("скажи привет");          // локальное эхо промпта
        await Assert.That(art).Contains("Готово: mock-ответ");     // committed ответ агента
        await Assert.That(art).Contains("mock/mock-model");       // статус-бар

        string expected = Golden.Verify("ce4-consoleex-repl", art);
        await Assert.That(art).IsEqualTo(expected);
    }

    // ── Local test infrastructure (ConsoleEx.Tests helpers are internal to that assembly) ──

    private static string Art(ScreenBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.Width == Cell.WSkip)
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

    private sealed class FrameCaptureBackend : ITerminalBackend
    {
        public List<byte[]> Writes { get; } = [];

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Writes.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        }
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
            State = AgentState.Idle(sessionId, placeholderDef);
        }

        public CancellationTokenSource AbortSource { get; } = new();
        public AgentState State { get; private set; }

        public void Initialize(Session session, AgentDefinition agent)
        {
            State = AgentState.Idle(session.Id, agent);
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) =>
            _bus.Subscribe(listener);

        public async Task<Result> PromptAsync(string text, CancellationToken ct = default)
        {
            State = State with { IsRunning = true };
            try
            {
                var user = new UserMessage(
                    Guid.NewGuid().ToString("N"), _sessionId, DateTimeOffset.UtcNow,
                    text, "code", "mock-model");
                await _bus.PublishAsync(new AgentStartEvent(_sessionId, [user])).ConfigureAwait(false);

                var partial = AssistantMessage.Empty(_sessionId, "mock-model");
                await _bus.PublishAsync(new MessageStartEvent(partial)).ConfigureAwait(false);
                await _bus.PublishAsync(new MessageUpdateEvent(
                    new TextDeltaEvent("0", "Готово: mock-ответ\n"), partial)).ConfigureAwait(false);

                var final = new AssistantMessage(
                    Guid.NewGuid().ToString("N"), _sessionId, DateTimeOffset.UtcNow,
                    [new TextPart("Готово: mock-ответ")], StopReason.Stop, new Usage(12, 5), "mock-model");
                await _bus.PublishAsync(new MessageEndEvent(final)).ConfigureAwait(false);

                await _bus.PublishAsync(new SessionStatsEvent(_sessionId, new SessionMetadata(
                    Cost: 0.0001m, TokensInput: 12, TokensOutput: 5,
                    TokensReasoning: 0, TokensCacheRead: 0, TokensCacheWrite: 0,
                    MessageCount: 2, TimeCompacting: null))).ConfigureAwait(false);

                await _bus.PublishAsync(new AgentEndEvent([])).ConfigureAwait(false);
                return Result.Success();
            }
            finally
            {
                State = State with { IsRunning = false };
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

        public void Dispose() => AbortSource.Dispose();
    }

    private sealed class StubConfigStore : IConfigStore
    {
        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(HarborConfig.Default));

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

    private sealed class MapServiceProvider : IServiceProvider
    {
        public Dictionary<Type, object> Map { get; } = [];

        public object? this[Type serviceType]
        {
            get => Map.TryGetValue(serviceType, out var value) ? value : null;
            set => Map[serviceType] = value!;
        }

        public object? GetService(Type serviceType) => this[serviceType];
    }
}

/// <summary>Fixture plumbing mirroring ConsoleEx.Tests' Golden helper (same fixtures dir).</summary>
internal static class Golden
{
    private static readonly Lazy<string> FixtureDir = new(ResolveFixtureDir);

    public static string Verify(string name, string actualContent)
    {
        string path = Path.Combine(FixtureDir.Value, name + ".golden.txt");
        if (Environment.GetEnvironmentVariable("HARBOR_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(FixtureDir.Value);
            File.WriteAllText(path, actualContent);
            return actualContent;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"golden fixture missing: {path} (run once with HARBOR_UPDATE_GOLDENS=1 to seed it)");
        }

        return File.ReadAllText(path);
    }

    private static string ResolveFixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("repo root (Harbor.slnx) not found from " + AppContext.BaseDirectory);
        }

        return Path.Combine(dir.FullName, "tests", "fixtures", "celldiff");
    }
}
