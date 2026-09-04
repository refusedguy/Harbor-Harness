using System.Collections.Immutable;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     CF-E-002 contract tests for the 7 cell-native builtin panels: identity
///     (Id/Title/Placement/Size), <c>Build</c> on empty + populated + clipped +
///     null-Services states, and <c>OnKey</c> consumption. No Spectre, no
///     filesystem fixtures (file-tree reads the real CWD read-only).
/// </summary>
public class CellForgeBuiltinPanelsTests
{
    private sealed class FakeServices : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map = new();

        public FakeServices Add<T>(T instance)
            where T : class
        {
            _map[typeof(T)] = instance;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _map.TryGetValue(serviceType, out var value) ? value : null;
    }

    private sealed class StubPanel : IPanelProvider
    {
        public string Id => "stub-a";

        public string Title => "Stub A";

        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

        public int DefaultSize => 10;

        public object? Build(PanelContext ctx) => "stub";

        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }

    private static PanelContext Ctx(UiState state, int width = 80, int height = 24, IServiceProvider? services = null) =>
        new(state, width, height, services);

    private static UiState StateWithLines(params ChatLine[] lines) =>
        new UiState { Lines = ImmutableArray.Create(lines) };

    private static IReadOnlyList<string> Rows(object? widget) => widget switch
    {
        null => Array.Empty<string>(),
        string s => s.Split('\n'),
        IReadOnlyList<string> rows => rows,
        IEnumerable<string> lines => lines.ToArray(),
        _ => new[] { widget.ToString() ?? string.Empty },
    };

    private static string Joined(object? widget) => string.Join("\n", Rows(widget));

    private static UiStore SeededStore(string id, int size)
    {
        var store = new UiStore();
        _ = store.Dispatch(new UiMsg.SeedPanels(
            ImmutableArray.Create(id),
            ImmutableDictionary<string, TuiPanelState>.Empty.Add(id, TuiPanelState.Hidden),
            ImmutableDictionary<string, int>.Empty.Add(id, size)));
        return store;
    }

    private static IPanelProvider[] AllPanels() =>
    [
        new CellForgeHelpPanel(),
        new CellForgeTodoListPanel(),
        new CellForgeDiffPreviewPanel(),
        new CellForgeFileTreePanel(),
        new CellForgeTokenBreakdownPanel(),
        new CellForgeDiagnosticsPanel(),
        new CellForgeLogsPanel(),
    ];

    [Test]
    public async Task Contract_Ids_Titles_Placements_Sizes()
    {
        var help = new CellForgeHelpPanel();
        await Assert.That(help.Id).IsEqualTo("help");
        await Assert.That(help.Title).IsEqualTo("Help");
        await Assert.That(help.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Right);
        await Assert.That(help.DefaultSize).IsEqualTo(48);

        var todo = new CellForgeTodoListPanel();
        await Assert.That(todo.Id).IsEqualTo("todo-list");
        await Assert.That(todo.Title).IsEqualTo("Todo List");
        await Assert.That(todo.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Right);
        await Assert.That(todo.DefaultSize).IsEqualTo(40);

        var diff = new CellForgeDiffPreviewPanel();
        await Assert.That(diff.Id).IsEqualTo("diff-preview");
        await Assert.That(diff.Title).IsEqualTo("Diff Preview");
        await Assert.That(diff.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Bottom);
        await Assert.That(diff.DefaultSize).IsEqualTo(12);

        var tree = new CellForgeFileTreePanel();
        await Assert.That(tree.Id).IsEqualTo("file-tree");
        await Assert.That(tree.Title).IsEqualTo("File Tree");
        await Assert.That(tree.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Left);
        await Assert.That(tree.DefaultSize).IsEqualTo(32);

        var tokens = new CellForgeTokenBreakdownPanel();
        await Assert.That(tokens.Id).IsEqualTo("token-breakdown");
        await Assert.That(tokens.Title).IsEqualTo("Token Breakdown");
        await Assert.That(tokens.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Bottom);
        await Assert.That(tokens.DefaultSize).IsEqualTo(10);

        var diags = new CellForgeDiagnosticsPanel();
        await Assert.That(diags.Id).IsEqualTo("diagnostics");
        await Assert.That(diags.Title).IsEqualTo("Diagnostics");
        await Assert.That(diags.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Bottom);
        await Assert.That(diags.DefaultSize).IsEqualTo(10);

        var logs = new CellForgeLogsPanel();
        await Assert.That(logs.Id).IsEqualTo("logs");
        await Assert.That(logs.Title).IsEqualTo("Logs");
        await Assert.That(logs.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Bottom);
        await Assert.That(logs.DefaultSize).IsEqualTo(10);
    }

    [Test]
    public async Task Build_Returns_CellRows_NotSpectreWidgets()
    {
        foreach (var panel in AllPanels())
        {
            object? widget = panel.Build(Ctx(new UiState()));
            await Assert.That(widget is IReadOnlyList<string>).IsTrue();
        }
    }

    [Test]
    public async Task Clipping_TinyViewport_NullServices_NeverThrows()
    {
        foreach (var panel in AllPanels())
        {
            var rows = Rows(panel.Build(Ctx(new UiState(), width: 10, height: 3, services: null)));
            await Assert.That(rows.Count <= 3).IsTrue();
            foreach (string line in rows)
            {
                await Assert.That(line.Length <= 10).IsTrue();
            }
        }
    }

    [Test]
    public async Task Clipping_ZeroGeometry_ReturnsEmpty()
    {
        var panel = new CellForgeHelpPanel();
        await Assert.That(Rows(panel.Build(Ctx(new UiState(), width: 0, height: 24))).Count).IsEqualTo(0);
        await Assert.That(Rows(panel.Build(Ctx(new UiState(), width: 80, height: 0))).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Todo_Empty_RendersPlaceholder()
    {
        string text = Joined(new CellForgeTodoListPanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("No todos yet.");
    }

    [Test]
    public async Task Todo_WithItems_RendersMarkerAndContent()
    {
        var state = StateWithLines(new ChatLine(
            ChatRole.ToolResult, "[ ] Write code\n[x] Done thing\n[~] Doing other"));
        string text = Joined(new CellForgeTodoListPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("[ ] Write code");
        await Assert.That(text).Contains("[x] Done thing");
        await Assert.That(text).Contains("[~] Doing other");
    }

    [Test]
    public async Task Diff_Empty_RendersPlaceholder()
    {
        string text = Joined(new CellForgeDiffPreviewPanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("No file edits yet.");
    }

    [Test]
    public async Task Diff_WithChange_RendersIconPathStatusAndBody()
    {
        var state = StateWithLines(
            new ChatLine(ChatRole.Tool, "→ edit {\"path\": \"src/a.cs\"}", "tc1"),
            new ChatLine(ChatRole.ToolResult, "✓ +added line\n-removed line\n context", "tc1"));
        string text = Joined(new CellForgeDiffPreviewPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("src/a.cs");
        await Assert.That(text).Contains("✓");
        await Assert.That(text).Contains("+added line");
    }

    [Test]
    public async Task Diagnostics_Empty_RendersPlaceholder()
    {
        string text = Joined(new CellForgeDiagnosticsPanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("No diagnostics detected.");
    }

    [Test]
    public async Task Diagnostics_WithError_RendersCrossIconAndMessage()
    {
        var state = StateWithLines(new ChatLine(ChatRole.Error, "error CS0001: something broke"));
        string text = Joined(new CellForgeDiagnosticsPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("✗");
        await Assert.That(text).Contains("CS0001");
    }

    [Test]
    public async Task Diagnostics_WithWarning_RendersTriangleIcon()
    {
        var state = StateWithLines(new ChatLine(ChatRole.ToolResult, "✓ warning: deprecated API used"));
        string text = Joined(new CellForgeDiagnosticsPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("▲");
    }

    [Test]
    public async Task TokenBreakdown_RendersBarsAndTotals()
    {
        var state = new UiState { Cost = new CostSnapshot(1500, 300, 0.0042m) };
        string text = Joined(new CellForgeTokenBreakdownPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("Token Breakdown");
        await Assert.That(text).Contains("1.5K");
        await Assert.That(text).Contains("300");
        await Assert.That(text).Contains("$0.0042");
        await Assert.That(text).Contains("█");
    }

    [Test]
    public async Task TokenBreakdown_ZeroCost_RendersWithoutThrowing()
    {
        string text = Joined(new CellForgeTokenBreakdownPanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("total");
    }

    [Test]
    public async Task Help_NullServices_RendersHotkeysSlashAndFallback()
    {
        string text = Joined(new CellForgeHelpPanel().Build(Ctx(new UiState(), services: null)));
        await Assert.That(text).Contains("Alt+1..9");
        await Assert.That(text).Contains("F12");
        await Assert.That(text).Contains("/help");
        await Assert.That(text).Contains("(no panels)");
    }

    [Test]
    public async Task Help_WithRegistry_ListsPanels()
    {
        var registry = new PanelRegistry();
        _ = registry.Register(new StubPanel());
        var services = new FakeServices().Add<IPanelRegistry>(registry);
        string text = Joined(new CellForgeHelpPanel().Build(Ctx(new UiState(), services: services)));
        await Assert.That(text).Contains("stub-a");
        await Assert.That(text).DoesNotContain("(no panels)");
    }

    [Test]
    public async Task Help_OnKey_QuestionMark_DispatchesToggle()
    {
        var store = SeededStore("help", 48);
        var services = new FakeServices().Add<UiStore>(store);
        bool consumed = new CellForgeHelpPanel().OnKey(UiKey.ForChar('?'), Ctx(store.State, services: services));
        await Assert.That(consumed).IsTrue();
        await Assert.That(store.State.PanelStates["help"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task Help_OnKey_WithoutStore_StillConsumed()
    {
        bool consumed = new CellForgeHelpPanel().OnKey(UiKey.ForChar('?'), Ctx(new UiState()));
        await Assert.That(consumed).IsTrue();
    }

    [Test]
    public async Task Help_OnKey_OtherKey_NotConsumed()
    {
        bool consumed = new CellForgeHelpPanel().OnKey(UiKey.ForChar('x'), Ctx(new UiState()));
        await Assert.That(consumed).IsFalse();
    }

    [Test]
    public async Task Logs_NullServices_RendersFallback()
    {
        string text = Joined(new CellForgeLogsPanel().Build(Ctx(new UiState(), services: null)));
        await Assert.That(text).Contains("not registered");
    }

    [Test]
    public async Task Logs_EmptyBuffer_RendersPlaceholder()
    {
        var services = new FakeServices().Add<IDiagnosticsPanel>(new InMemoryDiagnosticsPanel());
        string text = Joined(new CellForgeLogsPanel().Build(Ctx(new UiState(), services: services)));
        await Assert.That(text).Contains("No log entries yet.");
    }

    [Test]
    public async Task Logs_WithEntries_RendersLevelCategoryAndMessage()
    {
        var diagnostics = new InMemoryDiagnosticsPanel();
        diagnostics.Log(LogLevel.Warning, "Harbor.Core.AgentLoop", "hello world");
        var services = new FakeServices().Add<IDiagnosticsPanel>(diagnostics);
        string text = Joined(new CellForgeLogsPanel().Build(Ctx(new UiState(), services: services)));
        await Assert.That(text).Contains("WARN");
        await Assert.That(text).Contains("AgentLoop");
        await Assert.That(text).Contains("hello world");
    }

    [Test]
    public async Task Logs_OnKey_F12_DispatchesToggle()
    {
        var store = SeededStore("logs", 10);
        var services = new FakeServices().Add<UiStore>(store);
        bool consumed = new CellForgeLogsPanel().OnKey(new UiKey(UiKeyCode.F12), Ctx(store.State, services: services));
        await Assert.That(consumed).IsTrue();
        await Assert.That(store.State.PanelStates["logs"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task Logs_OnKey_OtherKey_NotConsumed()
    {
        bool consumed = new CellForgeLogsPanel().OnKey(UiKey.ForChar('x'), Ctx(new UiState()));
        await Assert.That(consumed).IsFalse();
    }

    [Test]
    public async Task PurePanels_OnKey_NeverConsumes()
    {
        IPanelProvider[] pure =
        [
            new CellForgeTodoListPanel(),
            new CellForgeDiffPreviewPanel(),
            new CellForgeDiagnosticsPanel(),
            new CellForgeTokenBreakdownPanel(),
        ];
        foreach (var panel in pure)
        {
            await Assert.That(panel.OnKey(UiKey.ForChar('j'), Ctx(new UiState()))).IsFalse();
            await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), Ctx(new UiState()))).IsFalse();
        }
    }

    [Test]
    public async Task FileTree_Build_RendersHeader()
    {
        string text = Joined(new CellForgeFileTreePanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("File Tree");
    }

    [Test]
    public async Task FileTree_OnKey_NavigationKeys_Consumed()
    {
        var panel = new CellForgeFileTreePanel();
        var ctx = Ctx(new UiState());
        await Assert.That(panel.OnKey(UiKey.ForChar('j'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('k'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('h'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('r'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), ctx)).IsTrue();
    }

    [Test]
    public async Task FileTree_OnKey_UnknownKey_NotConsumed()
    {
        bool consumed = new CellForgeFileTreePanel().OnKey(UiKey.ForChar('z'), Ctx(new UiState()));
        await Assert.That(consumed).IsFalse();
    }

    [Test]
    public async Task FileTree_BuildAfterNavigationKeys_StaysConsistent()
    {
        var panel = new CellForgeFileTreePanel();
        var ctx = Ctx(new UiState());
        _ = panel.OnKey(UiKey.ForChar('j'), ctx);
        _ = panel.OnKey(UiKey.ForChar('k'), ctx);
        string text = Joined(panel.Build(ctx));
        await Assert.That(text).Contains("File Tree");
    }
}
