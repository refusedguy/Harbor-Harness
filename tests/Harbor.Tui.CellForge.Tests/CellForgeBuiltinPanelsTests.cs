using System.Collections.Immutable;
using System.IO;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     CF-E-002 contract tests for the 9 cell-native builtin panels: identity
///     (Id/Title/Placement/Size), <c>Build</c> on empty + populated + clipped +
///     dependency-free states, and <c>OnKey</c> consumption. No Spectre, no
///     filesystem fixtures — since #667 the file-tree panel reads its listing
///     from state and never touches the disk, so it has a fixture to assert
///     against for the first time.
/// </summary>
public class CellForgeBuiltinPanelsTests
{
    private sealed class StubPanel : IPanelProvider
    {
        public string Id => "stub-a";

        public string Title => "Stub A";

        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

        public int DefaultSize => 10;

        public object? Build(PanelContext ctx) => "stub";

        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }

    // #470: panels read typed dependencies, never a container.
    private static PanelContext Ctx(UiState state, int width = 80, int height = 24, PanelServices? services = null) =>
        new(state, width, height, services);

    private static UiState StateWithLines(params ChatLine[] lines) =>
        new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Lines = ImmutableArray.Create(lines)
            }
        };

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
        _ = store.Dispatch(new AppMsg.SeedPanels(
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
        new CellForgeJumpPalettePanel(),
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

        var jump = new CellForgeJumpPalettePanel();
        await Assert.That(jump.Id).IsEqualTo("jump");
        await Assert.That(jump.Title).IsEqualTo("Jump");

        // #381: centred modal overlay (CellForgeJumpPaletteOverlayLayer), not a
        // Right dock leaf — the dock slot it used to hold is released.
        await Assert.That(jump.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Center);
        await Assert.That(jump.DefaultSize).IsEqualTo(48);
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
    public async Task Todo_WithItems_RendersSpectreParityIconsAndContent()
    {
        var state = StateWithLines(new ChatLine(
            ChatRole.ToolResult, "[ ] Write code\n[x] Done thing\n[~] Doing other"));
        string text = Joined(new CellForgeTodoListPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("○");
        await Assert.That(text).Contains("Write code");
        await Assert.That(text).Contains("✓");
        await Assert.That(text).Contains("Done thing");
        await Assert.That(text).Contains("→");
        await Assert.That(text).Contains("Doing other");
        await Assert.That(text).Contains("✓ 1  → 1  ○ 1");
        await Assert.That(text).DoesNotContain("[ ]");
        await Assert.That(text).DoesNotContain("[x]");
        await Assert.That(text).DoesNotContain("[~]");
    }

    [Test]
    public async Task Todo_LongContent_TruncatedToWidth()
    {
        var state = StateWithLines(new ChatLine(
            ChatRole.ToolResult, "[ ] " + new string('a', 60)));
        var rows = Rows(new CellForgeTodoListPanel().Build(Ctx(state, width: 30, height: 24)));
        await Assert.That(string.Join("\n", rows)).Contains("…");
        foreach (string line in rows)
        {
            await Assert.That(line.Length <= 30).IsTrue();
        }
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
    public async Task Diff_LongPath_KeepsFileNameWithEllipsis()
    {
        const string file = "very-long-file-name.cs";
        var state = StateWithLines(
            new ChatLine(ChatRole.Tool, "→ edit {\"path\": \"src/a/very/deep/dir/" + file + "\"}", "tc1"),
            new ChatLine(ChatRole.ToolResult, "✓ ok", "tc1"));
        string text = Joined(new CellForgeDiffPreviewPanel().Build(Ctx(state, width: 40, height: 24)));
        await Assert.That(text).Contains(file);
        await Assert.That(text).Contains("…");
    }

    [Test]
    public async Task Diff_LongBodyLine_TruncatedToWidth()
    {
        var state = StateWithLines(
            new ChatLine(ChatRole.Tool, "→ edit {\"path\": \"src/a.cs\"}", "tc1"),
            new ChatLine(ChatRole.ToolResult, "✓ " + new string('b', 100), "tc1"));
        var rows = Rows(new CellForgeDiffPreviewPanel().Build(Ctx(state, width: 40, height: 24)));
        await Assert.That(string.Join("\n", rows)).Contains("…");
        foreach (string line in rows)
        {
            await Assert.That(line.Length <= 40).IsTrue();
        }
    }

    /// <summary>
    ///     Issues arrive already classified from the headless core (#674), so the
    ///     panel's fixtures seed <see cref="ChatDomainState.Diagnostics" /> rather
    ///     than pasting build-log text into the transcript. A transcript that
    ///     merely LOOKS like errors no longer produces any.
    /// </summary>
    private static UiState StateWithDiagnostics(params DiagnosticIssue[] issues) =>
        new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics = ImmutableArray.Create(issues),
            },
        };

    private static DiagnosticIssue Issue(
        DiagnosticIssueSource source,
        DiagnosticIssueSeverity severity,
        string producer,
        string? filePath,
        int line,
        string message) => new(source, severity, producer, filePath, line, message);

    [Test]
    public async Task Diagnostics_Empty_RendersPlaceholder()
    {
        string text = Joined(new CellForgeDiagnosticsPanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("No diagnostics reported.");
    }

    [Test]
    public async Task Diagnostics_TranscriptTextAloneProducesNothing()
    {
        // The #674 shape: a build log that every old detector regex would have
        // matched, sitting in the transcript and NOT in the core's snapshot.
        var state = StateWithLines(
            new ChatLine(ChatRole.ToolResult, "✗ error CS0001: something broke"),
            new ChatLine(ChatRole.ToolResult, "✗ warning: deprecated API used"),
            new ChatLine(ChatRole.Error, "error MSB3021: could not copy"));

        string text = Joined(new CellForgeDiagnosticsPanel().Build(Ctx(state)));

        await Assert.That(text).Contains("No diagnostics reported.")
            .Because("the panel draws what the core classified. Re-deriving it from transcript text "
                   + "is the leak #674 closed, and a panel that still does it is indistinguishable "
                   + "from one that never worked.");
    }

    [Test]
    public async Task Diagnostics_WithError_RendersCrossIconAndMessage()
    {
        var state = StateWithDiagnostics(
            Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Error,
                "csharp", "src/a.cs", 3, "CS0001: something broke"));
        string text = Joined(new CellForgeDiagnosticsPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("✗");
        await Assert.That(text).Contains("CS0001");
    }

    [Test]
    public async Task Diagnostics_WithWarning_RendersTriangleIcon()
    {
        var state = StateWithDiagnostics(
            Issue(DiagnosticIssueSource.ToolOutput, DiagnosticIssueSeverity.Warning,
                "node", null, 0, "deprecated API used"));
        string text = Joined(new CellForgeDiagnosticsPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("▲");
    }

    [Test]
    public async Task Diagnostics_OnKey_JK_MovesCursor()
    {
        var state = StateWithDiagnostics(
            Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Error, "csharp", null, 0, "first broke"),
            Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Error, "csharp", null, 0, "second broke"),
            Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Error, "csharp", null, 0, "third broke"));
        var panel = new CellForgeDiagnosticsPanel();
        var ctx = Ctx(state);

        IReadOnlyList<string> initial = Rows(panel.Build(ctx));
        await Assert.That(initial[2]).StartsWith(">");

        await Assert.That(panel.OnKey(UiKey.ForChar('j'), ctx)).IsTrue();
        IReadOnlyList<string> moved = Rows(panel.Build(ctx));
        await Assert.That(moved[2]).StartsWith(" ");
        await Assert.That(moved[3]).StartsWith(">");
        await Assert.That(moved[3]).Contains("second broke");

        await Assert.That(panel.OnKey(UiKey.ForChar('k'), ctx)).IsTrue();
        IReadOnlyList<string> back = Rows(panel.Build(ctx));
        await Assert.That(back[2]).StartsWith(">");
    }

    [Test]
    public async Task Diagnostics_OnKey_CursorClampsAtEnds()
    {
        var state = StateWithDiagnostics(
            Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Error, "csharp", null, 0, "only"));
        var panel = new CellForgeDiagnosticsPanel();
        var ctx = Ctx(state);

        await Assert.That(panel.OnKey(UiKey.ForChar('k'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('j'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('j'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('J'), ctx)).IsTrue();
        IReadOnlyList<string> rows = Rows(panel.Build(ctx));
        await Assert.That(rows[2]).StartsWith(">");
        await Assert.That(rows[2]).Contains("only");
    }

    [Test]
    public async Task Diagnostics_OnKey_UnknownKey_NotConsumed()
    {
        var panel = new CellForgeDiagnosticsPanel();
        await Assert.That(panel.OnKey(UiKey.ForChar('z'), Ctx(new UiState()))).IsFalse();
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), Ctx(new UiState()))).IsFalse();
    }

    [Test]
    public async Task TokenBreakdown_RendersBarsAndTotals()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Cost = new CostSnapshot(1500, 300, 0.0042m)
            }
        };
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
    public async Task Help_RendersEverySharedKeymapRow()
    {
        string text = Joined(new CellForgeHelpPanel().Build(Ctx(new UiState(), services: null)));
        foreach (HelpKeymap.Entry hotkey in HelpKeymap.Rows)
        {
            await Assert.That(text).Contains(hotkey.Key);
            await Assert.That(text).Contains(hotkey.Description);
        }
    }

    [Test]
    public async Task HelpKeymap_HasTenUniqueKeys()
    {
        await Assert.That(HelpKeymap.Rows.Count).IsEqualTo(10);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (HelpKeymap.Entry hotkey in HelpKeymap.Rows)
        {
            await Assert.That(hotkey.Key.Length).IsGreaterThan(0);
            await Assert.That(hotkey.Description.Length).IsGreaterThan(0);
            await Assert.That(keys.Add(hotkey.Key)).IsTrue();
        }
    }

    [Test]
    public async Task Help_WithRegistry_ListsPanels()
    {
        var registry = new PanelRegistry();
        _ = registry.Register(new StubPanel());
        var services = new PanelServices { PanelRegistry = registry };
        string text = Joined(new CellForgeHelpPanel().Build(Ctx(new UiState(), services: services)));
        await Assert.That(text).Contains("stub-a");
        await Assert.That(text).DoesNotContain("(no panels)");
    }

    [Test]
    public async Task Help_OnKey_QuestionMark_DispatchesToggle()
    {
        var store = SeededStore("help", 48);
        var services = new PanelServices { Store = store };
        bool consumed = new CellForgeHelpPanel().OnKey(UiKey.ForChar('?'), Ctx(store.State, services: services));
        await Assert.That(consumed).IsTrue();
        await Assert.That(store.State.Ui.PanelStates["help"]).IsEqualTo(TuiPanelState.Visible);
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
        var services = new PanelServices { Diagnostics = new InMemoryDiagnosticsPanel() };
        string text = Joined(new CellForgeLogsPanel().Build(Ctx(new UiState(), services: services)));
        await Assert.That(text).Contains("No log entries yet.");
    }

    [Test]
    public async Task Logs_WithEntries_RendersLevelCategoryAndMessage()
    {
        var diagnostics = new InMemoryDiagnosticsPanel();
        diagnostics.Log(LogLevel.Warning, "Harbor.Core.AgentLoop", "hello world");
        var services = new PanelServices { Diagnostics = diagnostics };
        string text = Joined(new CellForgeLogsPanel().Build(Ctx(new UiState(), services: services)));
        await Assert.That(text).Contains("WARN");
        await Assert.That(text).Contains("AgentLoop");
        await Assert.That(text).Contains("hello world");
    }

    [Test]
    public async Task Logs_OnKey_F12_DispatchesToggle()
    {
        var store = SeededStore("logs", 10);
        var services = new PanelServices { Store = store };
        bool consumed = new CellForgeLogsPanel().OnKey(new UiKey(UiKeyCode.F12), Ctx(store.State, services: services));
        await Assert.That(consumed).IsTrue();
        await Assert.That(store.State.Ui.PanelStates["logs"]).IsEqualTo(TuiPanelState.Visible);
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

    // ── #667: the panel is now a pure reader of state ─────────────────────
    //
    // Before this issue the panel listed the real working directory from Build.
    // There was no fixture to assert against, because there was nothing to
    // assert: the only observable was "it did not crash". These tests give the
    // panel an observable — the state it renders — and pin the one thing that
    // must be true of a view with a demand signal: the signal is a REQUEST, and
    // what gets drawn comes from the store and nowhere else.

    [Test]
    public async Task FileTree_RendersEntriesFromState_NotFromDisk()
    {
        // A directory name that cannot exist on any test machine, so if the panel
        // were still reading the disk this row could not appear.
        var snapshot = FileTreeSnapshot.Completed(
            "/nowhere/loaded-from-state",
            [new FileTreeEntry("alpha.txt", "/nowhere/loaded-from-state/alpha.txt", false, false)]);

        var state = new UiState
        {
            Ui = new TerminalUiState
            {
                PanelDirs = ImmutableDictionary<string, string>.Empty.Add("file-tree", "/nowhere/loaded-from-state"),
                FileTrees = ImmutableDictionary<string, FileTreeSnapshot>.Empty.Add("file-tree", snapshot),
            },
        };

        string text = Joined(new CellForgeFileTreePanel().Build(Ctx(state)));
        await Assert.That(text).Contains("alpha.txt");
    }

    [Test]
    public async Task FileTree_AsksTheLoader_AndRendersNothingUntilItAnswers()
    {
        var loader = new RecordingLoader();
        var store = new UiStore();
        string dir = Path.Combine(Path.GetTempPath(), "harbor-667-no-such-dir");
        var state = new UiState
        {
            Ui = new TerminalUiState
            {
                PanelDirs = ImmutableDictionary<string, string>.Empty.Add("file-tree", dir),
            },
        };

        string text = Joined(new CellForgeFileTreePanel().Build(
            Ctx(state, services: new PanelServices { Store = store, FileTrees = loader })));

        await Assert.That(loader.Requests.Count).IsEqualTo(1);
        await Assert.That(loader.Requests[0].PanelId).IsEqualTo("file-tree");
        await Assert.That(loader.Requests[0].Directory).IsEqualTo(dir);
        await Assert.That(text).Contains("File Tree")
            .Because("the panel must still draw its frame; an unloaded tree is a state, not a blank");
    }

    [Test]
    public async Task FileTree_WithoutALoader_DegradesInsteadOfReadingTheDisk()
    {
        // No IFileTreeLoader in the bag: the panel must still render, and must
        // not have quietly become a filesystem reader again.
        var store = new UiStore();
        string text = Joined(new CellForgeFileTreePanel().Build(
            Ctx(new UiState(), services: new PanelServices { Store = store })));
        await Assert.That(text).Contains("File Tree");
    }

    [Test]
    public async Task FileTree_Build_DoesNotRaiseOnAStoreWithNoLoader()
    {
        // The degraded path has no loader to call, so nothing about the request
        // branch may dereference null. Two frames, to be sure it is stable and
        // not just survivable once.
        var panel = new CellForgeFileTreePanel();
        var store = new UiStore();
        var services = new PanelServices { Store = store };
        _ = Joined(panel.Build(Ctx(new UiState(), services: services)));
        string second = Joined(panel.Build(Ctx(store.State, services: services)));
        await Assert.That(second).Contains("File Tree");
    }

    [Test]
    public async Task FileTree_Refresh_InvalidatesTheListing()
    {
        // `r` used to clear a private cache field. It now clears STATE, so the
        // reload is observable: the snapshot is gone and the next frame asks
        // again.
        var store = new UiStore();
        string dir = Path.Combine(Path.GetTempPath(), "harbor-667-refresh");
        var state = new UiState
        {
            Ui = new TerminalUiState
            {
                PanelDirs = ImmutableDictionary<string, string>.Empty.Add("file-tree", dir),
                FileTrees = ImmutableDictionary<string, FileTreeSnapshot>.Empty.Add(
                    "file-tree",
                    FileTreeSnapshot.Completed(dir, [new FileTreeEntry("x", dir + "/x", false, false)])),
            },
        };

        var services = new PanelServices { Store = store, FileTrees = new RecordingLoader() };
        var panel = new CellForgeFileTreePanel();
        var ctx = Ctx(state, services: services);

        await Assert.That(Joined(panel.Build(ctx))).Contains("x");

        await Assert.That(panel.OnKey(UiKey.ForChar('r'), ctx)).IsTrue();

        await Assert.That(store.State.Ui.FileTreeFor("file-tree", dir).Status).IsEqualTo(AsyncStatus.Idle)
            .Because("`r` must drop the listing so the next demand re-loads; a refresh "
                   + "that left the old rows in state would be a no-op with a keypress attached");
    }

    [Test]
    public async Task FileTree_Enter_DescendsIntoADirectoryFromState()
    {
        var store = new UiStore();
        string dir = Path.Combine(Path.GetTempPath(), "harbor-667-descend");
        string child = Path.Combine(dir, "child");
        var state = new UiState
        {
            Ui = new TerminalUiState
            {
                PanelDirs = ImmutableDictionary<string, string>.Empty.Add("file-tree", dir),
                FileTrees = ImmutableDictionary<string, FileTreeSnapshot>.Empty.Add(
                    "file-tree",
                    FileTreeSnapshot.Completed(dir, [new FileTreeEntry("child", child, true, false)])),
            },
        };

        var services = new PanelServices { Store = store };
        bool consumed = new CellForgeFileTreePanel().OnKey(
            new UiKey(UiKeyCode.Enter),
            Ctx(state, services: services));

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.State.Ui.PanelDirs["file-tree"]).IsEqualTo(child);
    }

    [Test]
    public async Task FileTree_IgnoresASnapshotForADirectoryThePanelLeft()
    {
        // The staleness guard, seen from the panel: state holds a perfectly good
        // listing for a directory the panel is no longer pointed at, and none for
        // the one it is. The panel must render the current directory as
        // unloaded, not the stale entries.
        var state = new UiState
        {
            Ui = new TerminalUiState
            {
                PanelDirs = ImmutableDictionary<string, string>.Empty.Add("file-tree", "/nowhere/current"),
                FileTrees = ImmutableDictionary<string, FileTreeSnapshot>.Empty.Add(
                    "file-tree",
                    FileTreeSnapshot.Completed(
                        "/nowhere/stale",
                        [new FileTreeEntry("ghost.txt", "/nowhere/stale/ghost.txt", false, false)])),
            },
        };

        string text = Joined(new CellForgeFileTreePanel().Build(Ctx(state)));
        await Assert.That(text).DoesNotContain("ghost.txt");
    }

    private sealed class RecordingLoader : IFileTreeLoader
    {
        public List<(string PanelId, string Directory)> Requests { get; } = [];

        public List<string> Cancels { get; } = [];

        public void Request(string panelId, string directory, UiStore? store) =>
            Requests.Add((panelId, directory));

        public void CancelPanelLoad(string panelId) => Cancels.Add(panelId);
    }
}
