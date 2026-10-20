using System.Collections.Immutable;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     O8 steal (#1177, epic #1155) tests: the Suggested section on an empty
///     filter, preserve-selection across re-seeds and wrap-around navigation
///     in <see cref="WorktreeJumpPaletteModel" />, the <c>Suggested</c>/<c>All</c>
///     rendering in <see cref="CellForgeJumpPalettePanel" />, and the palette
///     action hints in <see cref="CellForgeHelpPanel" />.
/// </summary>
public class JumpPaletteSuggestedTests
{
    private static readonly WorktreeJumpEntry[] Entries =
    [
        new("s1", "Jump Palette", "/repo/.worktrees/jump-palette", "feat/jump-palette", "idle"),
        new("s2", "Word Diff", "/repo/.worktrees/worddiff", "feat/word-diff", "working ●"),
    ];

    private sealed class FakeGateway(Dictionary<string, (string? Dir, string? Status, string? Branch, bool Dirty)> facts)
        : IPanelSessionGateway
    {
        public List<string> Opened { get; } = new();

        public string? GetDirectory(string sessionId) =>
            facts.TryGetValue(sessionId, out var f) ? f.Dir : null;

        public string? GetStatusText(string sessionId) =>
            facts.TryGetValue(sessionId, out var f) ? f.Status : null;

        public string? GetBranch(string sessionId) =>
            facts.TryGetValue(sessionId, out var f) ? f.Branch : null;

        public bool GetIsDirty(string sessionId) =>
            facts.TryGetValue(sessionId, out var f) && f.Dirty;

        public bool? GetIsSubagent(string sessionId) => false;

        public Task<bool> OpenPanelSessionAsync(string sessionId)
        {
            Opened.Add(sessionId);
            return Task.FromResult(true);
        }
    }

    private static PanelContext Ctx(UiState state, PanelServices? services = null) =>
        new(state, 120, 40, services);

    private static UiState StateWithSessions(params SessionInfo[] sessions) =>
        new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Sessions = ImmutableArray.Create(sessions)
            }
        };

    private static string Joined(object? widget) => widget switch
    {
        null => string.Empty,
        IReadOnlyList<string> rows => string.Join("\n", rows),
        _ => widget.ToString() ?? string.Empty,
    };

    private static HashSet<string> Suggested(params string[] ids) =>
        new(ids, StringComparer.Ordinal);

    [Test]
    public async Task Show_WithSuggested_EmptyQuery_Leads_With_Duplicates()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries, Suggested("s2"));

        await Assert.That(model.SuggestedCount).IsEqualTo(1);
        await Assert.That(model.Results.Count).IsEqualTo(3);
        await Assert.That(model.Results[0].SessionId).IsEqualTo("s2");
        await Assert.That(model.Results[1].SessionId).IsEqualTo("s1");
        await Assert.That(model.Results[2].SessionId).IsEqualTo("s2");
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Show_UnknownSuggestedId_Is_Ignored()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries, Suggested("nope"));

        await Assert.That(model.SuggestedCount).IsEqualTo(0);
        await Assert.That(model.Results.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SetQuery_Clears_Suggested_Block()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries, Suggested("s2"));
        model.SetQuery("word");

        await Assert.That(model.SuggestedCount).IsEqualTo(0);
        await Assert.That(model.Results.Count).IsEqualTo(1);
        await Assert.That(model.Results[0].SessionId).IsEqualTo("s2");
    }

    [Test]
    public async Task Move_Wraps_Around_Ends()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);

        model.MoveUp();
        await Assert.That(model.SelectedIndex).IsEqualTo(1);

        model.MoveDown();
        await Assert.That(model.SelectedIndex).IsEqualTo(0);

        model.Hide();
        model.MoveUp();
        model.MoveDown();
        await Assert.That(model.SelectedIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task Reseed_Preserves_Selection_By_Identity()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        model.MoveDown();
        await Assert.That(model.SelectedIndex).IsEqualTo(1);
        await Assert.That(model.Results[model.SelectedIndex].SessionId).IsEqualTo("s2");

        // Same entries, reversed order: the cursor follows s2, not the index.
        model.Reseed([Entries[1], Entries[0]]);

        await Assert.That(model.Query).IsEmpty();
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
        await Assert.That(model.Results[model.SelectedIndex].SessionId).IsEqualTo("s2");
    }

    [Test]
    public async Task Reseed_Dropped_Selection_Reconciles_Into_Range()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        model.MoveDown();

        model.Reseed([Entries[0]]);

        await Assert.That(model.SelectedIndex).IsEqualTo(0);
        await Assert.That(model.Results[model.SelectedIndex].SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task Reseed_Keeps_Query_And_Filter()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        model.SetQuery("word");

        model.Reseed([Entries[1], Entries[0]]);

        await Assert.That(model.Query).IsEqualTo("word");
        await Assert.That(model.Results.Count).IsEqualTo(1);
        await Assert.That(model.Results[0].SessionId).IsEqualTo("s2");
    }

    [Test]
    public async Task Panel_Build_Renders_Suggested_Then_All()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries, Suggested("s2"));
        var panel = new CellForgeJumpPalettePanel(model);

        string text = Joined(panel.Build(Ctx(new UiState())));

        await Assert.That(text).Contains("Jump (3)");
        await Assert.That(text).Contains("Suggested");
        await Assert.That(text).Contains("All");
        await Assert.That(text).Contains("▸ Word Diff");
    }

    [Test]
    public async Task Panel_Enter_On_Suggested_Row_Switches_Session()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries, Suggested("s2"));
        var panel = new CellForgeJumpPalettePanel(model);
        var gateway = new FakeGateway(new Dictionary<string, (string?, string?, string?, bool)>());
        var services = new PanelServices { Sessions = gateway };

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), Ctx(new UiState(), services))).IsTrue();

        await Assert.That(gateway.Opened).Contains("s2");
        await Assert.That(model.Visible).IsFalse();
    }

    [Test]
    public async Task Panel_Seed_Marks_Working_And_Dirty_Sessions_Suggested()
    {
        var state = StateWithSessions(WidgetFixtures.Info("s1", "Idle One"), WidgetFixtures.Info("s2", "Busy Two"), WidgetFixtures.Info("s3", "Dirty Three"));
        var gateway = new FakeGateway(new Dictionary<string, (string?, string?, string?, bool)>
        {
            ["s1"] = ("/repo/a", "idle", "main", false),
            ["s2"] = ("/repo/b", "working", "feat/x", false),
            ["s3"] = ("/repo/c", "idle", "feat/y", true),
        });
        var panel = new CellForgeJumpPalettePanel();

        string text = Joined(panel.Build(Ctx(state, new PanelServices { Sessions = gateway })));

        await Assert.That(text).Contains("Suggested");
        await Assert.That(text).Contains("Busy Two");
        await Assert.That(text).Contains("Dirty Three");
    }

    [Test]
    public async Task Panel_Seed_AllIdle_Renders_No_Suggested_Block()
    {
        var state = StateWithSessions(WidgetFixtures.Info("s1", "Idle One"));
        var gateway = new FakeGateway(new Dictionary<string, (string?, string?, string?, bool)>
        {
            ["s1"] = ("/repo/a", "idle", "main", false),
        });
        var panel = new CellForgeJumpPalettePanel();

        string text = Joined(panel.Build(Ctx(state, new PanelServices { Sessions = gateway })));

        await Assert.That(text).DoesNotContain("Suggested");
        await Assert.That(text).Contains("Idle One");
    }

    [Test]
    public async Task HelpPanel_Lists_Palette_Action_Hints()
    {
        string text = Joined(new CellForgeHelpPanel().Build(Ctx(new UiState(), services: null)));

        await Assert.That(text).Contains("Jump palette");
        await Assert.That(text).Contains(CellForgeJumpPalettePanel.HintFooter);
    }
}
