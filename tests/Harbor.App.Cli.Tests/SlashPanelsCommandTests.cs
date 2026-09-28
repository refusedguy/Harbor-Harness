using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Configuration;
using Harbor.Hosting.Rendering;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using System.Collections.Immutable;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Panel-level tests for the slash-panel commands: rows are human
///     (title first, hash dimmed), the live palette filters them, and Enter
///     commits (tree switches, providers opens the key prompt, plugins math
///     is covered by <see cref="PluginsPanelCommand.ToggleTarget" />).
///     The palette is real; only the host is faked. No disk writes.
/// </summary>
public class SlashPanelsCommandTests
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

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by panel tests.");
        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(Result.Success());
    }

    private sealed class FakeHost : IReplHost
    {
        public FakeHost(Session session, FakeStore store, AuthStore auth, UiState? initialState = null)
        {
            SessionModel = session;
            SessionStore = store;
            AuthStore = auth;
            Store = new UiStore(initialState);
        }

        public List<string> Switched { get; } = [];

        public IAgent Agent => null!;
        public Session SessionModel { get; set; }
        public ChatScreenBridge Bridge => null!;
        public UiStore Store { get; }
        public CommandPaletteView Palette { get; } = new();
        public StatusViewModel Status { get; } = new();
        public ChatScreen Screen => null!;
        public SelectionEngine Selection => null!;
        public VirtualizedChatTimeline Timeline => null!;
        public ComposerController Composer => null!;
        public IConfigStore ConfigStore => null!;
        public IProviderRegistry ProviderRegistry => null!;
        public IAgentRegistry AgentRegistry => null!;
        public AuthStore AuthStore { get; }
        public ISessionStore? SessionStore { get; }
        public IRendererPipeline? RendererPipeline => null;
        public Harbor.Hosting.PluginReloadService? PluginReload => null;
        public IProviderHealthCheck? HealthCheck => null;
        public Harbor.Ui.Framework.Panels.IPanelRegistry? PanelRegistry { get; set; }
        public Harbor.App.Cli.Repl.ImageAttachmentStash? Attachments => null;
        public void WakeUp() { }
        public void OpenSlashPalette() { }
        public void ToggleVimMode() { }
        public void ScrollTimelineToEnd() { }
        public void RequestQuit(int exitCode) { }
        public Task SwitchToSessionAsync(string sessionId, CancellationToken ct)
        {
            Switched.Add(sessionId);
            return Task.CompletedTask;
        }
        public Task ExecutePaletteItemAsync(CommandItem item, CancellationToken ct) => Task.CompletedTask;
        public Task ExecuteInfoAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task SyncSessionsToStoreAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> ResolveContextWindowAsync(string providerId, string modelId, CancellationToken ct) => Task.FromResult(0);
    }

    private static Session Make(string id, string title, DateTimeOffset at, string? parent = null) =>
        Session.Create("/panel-tests", "code", "kilocode", "kilo-auto", title) with
        {
            Id = id,
            CreatedAt = at,
            UpdatedAt = at,
            ParentSessionId = parent,
        };

    private static void Type(CommandPaletteView palette, string text)
    {
        foreach (char c in text)
        {
            _ = palette.HandleKey(KeyEvent.Char(new Rune(c)));
        }
    }

    [Test]
    public async Task Tree_RowsAreTitleFirst_EnterSwitches()
    {
        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new FakeStore();
        store.Add(Make("aaa11111bbbb2222", "Fix login bug", t0));
        store.Add(Make("ccc33333dddd4444", "Write docs", t0.AddMinutes(1), parent: "aaa11111bbbb2222"));
        var host = new FakeHost(store.Sessions["aaa11111bbbb2222"], store, new AuthStore(new JsonConfigStore()));

        await new SessionTreeCommand().ExecuteAsync(new ReplCommandContext(host, "tree"), CancellationToken.None);

        await Assert.That(host.Palette.Visible).IsTrue();
        await Assert.That(host.Palette.Results).Count().IsEqualTo(2);
        await Assert.That(host.Palette.Results[0].Title).Contains("Fix login bug");
        await Assert.That(host.Palette.Results[0].Title.Contains("aaa11111")).IsFalse();
        await Assert.That(host.Palette.Results[0].Detail).Contains("aaa11111");

        // Filter as you type, then Enter opens the filtered session.
        Type(host.Palette, "docs");
        await Assert.That(host.Palette.Results).Count().IsEqualTo(1);
        _ = host.Palette.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        var pending = host.Palette.TakePendingCommit();
        await Assert.That(pending).IsNotNull();
        await pending!.Value.Handler(pending.Value.Item, CancellationToken.None);

        await Assert.That(host.Switched).Contains("ccc33333dddd4444");
        await Assert.That(host.Palette.Visible).IsFalse();
    }

    private sealed class StubPanel(string id, string title) : IPanelProvider
    {
        public string Id => id;
        public string Title => title;
        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;
        public int DefaultSize => 10;
        public object? Build(PanelContext ctx) => "stub";
        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }

    private static PanelRegistry RegistryWith(params IPanelProvider[] panels)
    {
        var registry = new PanelRegistry();
        foreach (var p in panels)
        {
            _ = registry.Register(p);
        }

        return registry;
    }

    private static UiState SeededPanelsState() => new()
    {
        Ui = TerminalUiState.Empty with
        {
            RegisteredPanelIds = ["todo", "logs"],
            PanelStates = ImmutableDictionary<string, TuiPanelState>.Empty
                .SetItem("todo", TuiPanelState.Visible)
                .SetItem("logs", TuiPanelState.Hidden)
        }
    };

    private static FakeHost PanelsHost(FakeStore store, Session current)
    {
        var host = new FakeHost(current, store, new AuthStore(new JsonConfigStore()), SeededPanelsState())
        {
            PanelRegistry = RegistryWith(new StubPanel("todo", "Todo"), new StubPanel("logs", "Logs")),
        };
        return host;
    }

    [Test]
    public async Task Panels_ListsRegistryRows_WithLiveState()
    {
        var store = new FakeStore();
        var current = Make("cur00003", "current", DateTimeOffset.UtcNow);
        store.Add(current);
        var host = PanelsHost(store, current);

        await new PanelsCommand().ExecuteAsync(new ReplCommandContext(host, "panels"), CancellationToken.None);

        await Assert.That(host.Palette.Visible).IsTrue();
        await Assert.That(host.Palette.Results.Count).IsEqualTo(2);
        // Empty-query palette sorts by title: Logs before Todo.
        await Assert.That(host.Palette.Results[0].Title).IsEqualTo("Logs");
        await Assert.That(host.Palette.Results[0].Detail).Contains("hidden");
        await Assert.That(host.Palette.Results[1].Title).IsEqualTo("Todo");
        await Assert.That(host.Palette.Results[1].Detail).Contains("visible");
    }

    [Test]
    public async Task Panels_Enter_TogglesVisibilityAndCloses()
    {
        var store = new FakeStore();
        var current = Make("cur00004", "current", DateTimeOffset.UtcNow);
        store.Add(current);
        var host = PanelsHost(store, current);

        await new PanelsCommand().ExecuteAsync(new ReplCommandContext(host, "panels"), CancellationToken.None);

        string firstId = host.Palette.Results[0].Id;
        var before = host.Store.State.Ui.PanelStates[firstId];
        _ = host.Palette.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        var pending = host.Palette.TakePendingCommit();
        await Assert.That(pending).IsNotNull();
        await pending!.Value.Handler(pending.Value.Item, CancellationToken.None);

        await Assert.That(host.Store.State.Ui.PanelStates[firstId]).IsNotEqualTo(before);
        await Assert.That(host.Palette.Visible).IsFalse();
    }

    [Test]
    public async Task Panels_NoRegistry_FallsBackToText()
    {
        var store = new FakeStore();
        var current = Make("cur00005", "current", DateTimeOffset.UtcNow);
        store.Add(current);
        var host = new FakeHost(current, store, new AuthStore(new JsonConfigStore()));

        await new PanelsCommand().ExecuteAsync(new ReplCommandContext(host, "panels"), CancellationToken.None);

        await Assert.That(host.Palette.Visible).IsFalse();
    }

    [Test]
    public async Task Providers_ListsPresets_EnterOpensKeyPrompt()
    {
        var store = new FakeStore();
        var current = Make("cur00001", "current", DateTimeOffset.UtcNow);
        store.Add(current);
        var host = new FakeHost(current, store, new AuthStore(new JsonConfigStore()));

        await new ProvidersCommand().ExecuteAsync(new ReplCommandContext(host, "providers"), CancellationToken.None);

        await Assert.That(host.Palette.Visible).IsTrue();
        await Assert.That(host.Palette.Results).Count().IsEqualTo(ProviderPresets.All.Count);
        await Assert.That(host.Palette.Results.Any(i => i.Title.Contains("Kilo"))).IsTrue();

        // A key-based provider opens the key prompt (no key saved — no disk writes).
        Type(host.Palette, "kilocode");
        await Assert.That(host.Palette.Results).Count().IsEqualTo(1);
        _ = host.Palette.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        var pending = host.Palette.TakePendingCommit();
        await Assert.That(pending).IsNotNull();
        await pending!.Value.Handler(pending.Value.Item, CancellationToken.None);
        await Assert.That(host.Palette.CurrentBreadcrumb).IsEqualTo("providers / kilocode");
    }

    [Test]
    public async Task Providers_LocalProvider_SkipsKeyPrompt()
    {
        var store = new FakeStore();
        var current = Make("cur00002", "current", DateTimeOffset.UtcNow);
        store.Add(current);
        var host = new FakeHost(current, store, new AuthStore(new JsonConfigStore()));

        await new ProvidersCommand().ExecuteAsync(new ReplCommandContext(host, "providers"), CancellationToken.None);

        // Local providers probe straight away; with no health service the
        // panel just closes (wizard parity) — no prompt, no writes.
        Type(host.Palette, "ollama");
        await Assert.That(host.Palette.Results).Count().IsEqualTo(1);
        _ = host.Palette.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        var pending = host.Palette.TakePendingCommit();
        await Assert.That(pending).IsNotNull();
        await pending!.Value.Handler(pending.Value.Item, CancellationToken.None);
        await Assert.That(host.Palette.Visible).IsFalse();
    }

    [Test]
    public async Task Plugins_ToggleTarget_RenamesBothWays()
    {
        string disabled = PluginsPanelCommand.ToggleTarget("/p/Alpha.cs", out bool disabling);
        await Assert.That(disabling).IsTrue();
        await Assert.That(disabled).IsEqualTo("/p/Alpha.cs.disabled");

        string enabled = PluginsPanelCommand.ToggleTarget("/p/Alpha.cs.disabled", out bool enabling);
        await Assert.That(enabling).IsFalse();
        await Assert.That(enabled).IsEqualTo("/p/Alpha.cs");
    }
}
