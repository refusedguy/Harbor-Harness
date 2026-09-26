using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="WorktreeJumpPaletteModel" /> — the pure jump-palette
///     model (KILLER_FEATURES §2.7 Feature 3). Verifies open/close, substring +
///     fuzzy filtering across title/path/branch, ↑↓ selection clamping and the
///     Enter-confirm contract. Deterministic — pure state transitions only.
/// </summary>
public class WorktreeJumpPaletteTests
{
    private static readonly WorktreeJumpEntry[] Entries =
    [
        new("s1", "Word Diff", "/repo/.worktrees/worddiff", "feat/word-diff", "working ●"),
        new("s2", "Jump Palette", "/repo/.worktrees/jump-palette", "feat/jump-palette", "idle"),
        new("s3", "Docs Refresh", "/repo/.worktrees/docs-specs", "docs/specs-refresh", "idle"),
    ];

    private static WorktreeJumpPaletteModel Open()
    {
        var palette = new WorktreeJumpPaletteModel();
        palette.Show(Entries);
        return palette;
    }

    /// <summary>
    ///     <see cref="WorktreeJumpPaletteModel.Show" /> opens the palette, lists
    ///     every entry with an empty query and selects the first row.
    /// </summary>
    [Test]
    public async Task Show_ListsAll_AndSelectsFirst()
    {
        var palette = Open();

        await Assert.That(palette.Visible).IsTrue();
        await Assert.That(palette.Results).Count().IsEqualTo(Entries.Length);
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
        await Assert.That(palette.Selected!.SessionId).IsEqualTo("s1");
    }

    /// <summary>
    ///     <see cref="WorktreeJumpPaletteModel.Hide" /> closes the palette and
    ///     drops entries, query and selection.
    /// </summary>
    [Test]
    public async Task Hide_ResetsEverything()
    {
        var palette = Open();
        palette.SetQuery("jump");
        palette.Hide();

        await Assert.That(palette.Visible).IsFalse();
        await Assert.That(palette.Query).IsEqualTo(string.Empty);
        await Assert.That(palette.Results).Count().IsEqualTo(0);
        await Assert.That(palette.SelectedIndex).IsEqualTo(-1);
        await Assert.That(palette.Selected).IsNull();
        await Assert.That(palette.Confirm()).IsNull();
    }

    /// <summary>
    ///     Substring queries match title, path and branch: <c>"jump"</c> hits one
    ///     entry, <c>"worktrees"</c> hits all three via path, <c>"feat"</c> hits
    ///     the two <c>feat/*</c> branches.
    /// </summary>
    [Test]
    public async Task SetQuery_Substring_MatchesTitlePathAndBranch()
    {
        var palette = Open();

        palette.SetQuery("jump");
        await Assert.That(palette.Results).Count().IsEqualTo(1);
        await Assert.That(palette.Results[0].SessionId).IsEqualTo("s2");

        palette.SetQuery("worktrees");
        await Assert.That(palette.Results).Count().IsEqualTo(3);

        palette.SetQuery("feat");
        await Assert.That(palette.Results).Count().IsEqualTo(2);
    }

    /// <summary>
    ///     Non-contiguous queries fall back to subsequence fuzzy match:
    ///     <c>"wrdif"</c> still finds "Word Diff".
    /// </summary>
    [Test]
    public async Task SetQuery_FuzzySubsequence_Matches()
    {
        var palette = Open();

        palette.SetQuery("wrdif");

        await Assert.That(palette.Results).Count().IsEqualTo(1);
        await Assert.That(palette.Results[0].SessionId).IsEqualTo("s1");
    }

    /// <summary>
    ///     A query with no hits empties the results, clears the selection and
    ///     makes <see cref="WorktreeJumpPaletteModel.Confirm" /> return null.
    /// </summary>
    [Test]
    public async Task SetQuery_NoMatch_ClearsSelection()
    {
        var palette = Open();

        palette.SetQuery("zzz-no-such-worktree");

        await Assert.That(palette.Results).Count().IsEqualTo(0);
        await Assert.That(palette.SelectedIndex).IsEqualTo(-1);
        await Assert.That(palette.Selected).IsNull();
        await Assert.That(palette.Confirm()).IsNull();
    }

    /// <summary>
    ///     Re-querying resets the selection to the top hit even after navigation.
    /// </summary>
    [Test]
    public async Task SetQuery_ResetsSelectionToTop()
    {
        var palette = Open();
        palette.MoveDown();
        palette.MoveDown();

        palette.SetQuery("docs");

        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
        await Assert.That(palette.Selected!.SessionId).IsEqualTo("s3");
    }

    /// <summary>
    ///     <see cref="WorktreeJumpPaletteModel.MoveDown" /> /
    ///     <see cref="WorktreeJumpPaletteModel.MoveUp" /> clamp at the bounds
    ///     instead of wrapping or throwing.
    /// </summary>
    [Test]
    public async Task MoveDown_MoveUp_ClampAtBounds()
    {
        var palette = Open();

        palette.MoveUp();
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);

        palette.MoveDown();
        palette.MoveDown();
        palette.MoveDown();
        await Assert.That(palette.SelectedIndex).IsEqualTo(2);

        palette.MoveUp();
        await Assert.That(palette.SelectedIndex).IsEqualTo(1);
    }

    /// <summary>
    ///     <see cref="WorktreeJumpPaletteModel.Confirm" /> returns the navigated
    ///     row — the host switches to its <see cref="WorktreeJumpEntry.SessionId" />.
    /// </summary>
    [Test]
    public async Task Confirm_ReturnsSelected_AfterMove()
    {
        var palette = Open();
        palette.MoveDown();

        await Assert.That(palette.Confirm()!.SessionId).IsEqualTo("s2");
        await Assert.That(palette.Confirm()!.Path).IsEqualTo("/repo/.worktrees/jump-palette");
    }

    /// <summary>
    ///     <see cref="WorktreeJumpPaletteModel.Confirm" /> on a hidden palette
    ///     returns null (stale Enter after Esc is a noop).
    /// </summary>
    [Test]
    public async Task Confirm_Hidden_ReturnsNull()
    {
        var palette = new WorktreeJumpPaletteModel();

        await Assert.That(palette.Confirm()).IsNull();
    }

    /// <summary>
    ///     <see cref="WorktreeJumpEntry.RowText" /> renders title, branch,
    ///     status and path on one line; null branches show <c>"no-branch"</c>.
    /// </summary>
    [Test]
    public async Task RowText_FormatsTitleBranchStatusPath()
    {
        await Assert.That(Entries[0].RowText)
            .IsEqualTo("Word Diff  feat/word-diff  working ●  /repo/.worktrees/worddiff");

        var bare = new WorktreeJumpEntry("s9", "Scratch", "/tmp/scratch", null, "idle");
        await Assert.That(bare.BranchText).IsEqualTo("no-branch");
        await Assert.That(bare.RowText).IsEqualTo("Scratch  no-branch  idle  /tmp/scratch");
    }
}
