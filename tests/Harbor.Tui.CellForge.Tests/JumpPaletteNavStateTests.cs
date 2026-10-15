using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Jump palette through the external navigation state (#1158, ratatui
/// <c>StatefulWidget</c> analog, same recipe as <c>DialogSelectList</c>
/// #1413): <see cref="CellForgeJumpPalettePanel.State" /> survives frames
/// and owns the selectable axis, the panel owns content (entries, query,
/// filter) and geometry. <c>Build(ctx, state)</c> renders any
/// count-compatible state, so two panels can share one; otherwise it falls
/// back to the model selection. The legacy <c>Build(ctx)</c> path is
/// unchanged (renders every row, <c>Clip</c> cuts the tail).
/// </summary>
public class JumpPaletteNavStateTests
{
    private static readonly WorktreeJumpEntry[] Entries =
    [
        new("s1", "Jump Palette", "/repo/.worktrees/jump-palette", "feat/jump-palette", "idle"),
        new("s2", "Word Diff", "/repo/.worktrees/worddiff", "feat/word-diff", "working ●"),
    ];

    private static CellForgeJumpPalettePanel WithModel(out WorktreeJumpPaletteModel model)
    {
        model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        return new CellForgeJumpPalettePanel(model);
    }

    private static CellForgeJumpPalettePanel WithMany(int count, out WorktreeJumpEntry[] entries)
    {
        var made = new WorktreeJumpEntry[count];
        for (int i = 0; i < made.Length; i++)
        {
            string tag = i.ToString("00");
            made[i] = new WorktreeJumpEntry("s" + tag, "Session " + tag, "/repo/wt-" + tag, "feat/wt-" + tag, "idle");
        }

        entries = made;
        var model = new WorktreeJumpPaletteModel();
        model.Show(made);
        return new CellForgeJumpPalettePanel(model);
    }

    private static PanelContext Ctx(int height = 40) => new(new UiState(), 120, height);

    private static string Joined(object? widget) => widget switch
    {
        null => string.Empty,
        IReadOnlyList<string> lines => string.Join("\n", lines),
        _ => widget.ToString() ?? string.Empty,
    };

    [Test]
    public async Task Panel_State_MirrorsResultCount()
    {
        var panel = WithModel(out var model);
        _ = panel.Build(Ctx());
        await Assert.That(panel.State.Count).IsEqualTo(Entries.Length);
        await Assert.That(panel.State.Selected).IsEqualTo(0);

        model.Hide();
        _ = panel.Build(Ctx());
        await Assert.That(panel.State.Count).IsEqualTo(0);
        await Assert.That(panel.State.Selected).IsEqualTo(-1);
    }

    [Test]
    public async Task Panel_ExternalState_DrivesHighlight()
    {
        var panel = WithModel(out _);
        _ = panel.Build(Ctx());
        panel.State.MoveTo(1);

        string text = Joined(panel.Build(Ctx(), panel.State));

        await Assert.That(text).Contains("▸ " + Entries[1].RowText);
        await Assert.That(text).Contains("  " + Entries[0].RowText);
    }

    [Test]
    public async Task Panel_SharedState_DrivesTwoPanels()
    {
        // The #1158 contract: state is external, so one state object can drive
        // two panels and both paint the same selection.
        var left = WithModel(out _);
        var right = WithModel(out var rightModel);
        _ = left.Build(Ctx());
        left.State.MoveTo(1);

        string text = Joined(right.Build(Ctx(), left.State));

        await Assert.That(text).Contains("▸ " + Entries[1].RowText);
        await Assert.That(text).DoesNotContain("▸ " + Entries[0].RowText);
        await Assert.That(rightModel.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Panel_ExternalState_ScrollsToSelection()
    {
        var panel = WithMany(20, out var entries);
        var ctx = Ctx(height: 8);
        _ = panel.Build(ctx);
        panel.State.SelectLast();

        // Height 8 → header + separator + 4 entry rows + separator + footer;
        // the selection drags the window to the tail.
        string text = Joined(panel.Build(ctx, panel.State));

        await Assert.That(text).Contains("▸ " + entries[19].RowText);
        await Assert.That(text).DoesNotContain(entries[0].RowText);
    }

    [Test]
    public async Task Panel_MismatchedState_FallsBackToLegacy()
    {
        var panel = WithModel(out var model);
        model.MoveDown();
        _ = panel.Build(Ctx());
        var foreign = new GenericNavState();
        foreign.SetContent(99);
        foreign.MoveTo(50);

        string text = Joined(panel.Build(Ctx(), foreign));

        await Assert.That(text).Contains("▸ " + Entries[1].RowText);
        await Assert.That(text).DoesNotContain("▸ " + Entries[0].RowText);
    }

    [Test]
    public async Task Panel_LegacyBuild_UnchangedByStatefulBuild()
    {
        var panel = WithModel(out var model);
        _ = panel.Build(Ctx());
        panel.State.MoveTo(1);

        // Stateful build leaves the legacy selection alone: the legacy path
        // still highlights the first entry.
        string stateful = Joined(panel.Build(Ctx(), panel.State));
        await Assert.That(stateful).Contains("▸ " + Entries[1].RowText);

        string legacy = Joined(panel.Build(Ctx()));
        await Assert.That(legacy).Contains("▸ " + Entries[0].RowText);
        await Assert.That(legacy).DoesNotContain("▸ " + Entries[1].RowText);
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
    }
}
