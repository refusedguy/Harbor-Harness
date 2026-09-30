using Harbor.Abstractions.Git;
using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="WorktreeJumpSeeder" /> — the pure jump-palette
///     seeding (slice 2): the session/worktree merge into
///     <see cref="WorktreeJumpEntry" /> rows.
///     Deterministic — pure list transforms only.
/// </summary>
/// <remarks>
/// <para>
///     #666: the porcelain <c>ParsePorcelain</c> tests left this file with the
///     parser, which is now <c>WorktreePorcelainParser</c> in
///     <c>Harbor.Application</c> and is tested there. What is left builds
///     <see cref="GitWorktreeInfo" /> records as literals on purpose: a merge
///     test should not break because git changed the shape of its output, and
///     this one only cares how sessions and worktrees are combined.
/// </para>
/// </remarks>
public class WorktreeJumpSeederTests
{
    /// <summary>The four records the fixture porcelain used to yield.</summary>
    private static readonly GitWorktreeInfo[] Worktrees =
    [
        new("/repo", "main", false),
        new("/repo/.worktrees/jump-palette", "feat/jump-palette", false),
        new("/repo/.worktrees/detached-wt", null, false),
        new("/repo/.git", null, true),
    ];

    /// <summary>
    ///     <see cref="WorktreeJumpSeeder.BuildEntries" /> prefers the session
    ///     branch, falls back to the worktree list by directory, marks dirty
    ///     sessions with <c>●</c>, and appends session-less worktrees
    ///     (path-sorted, empty session id) while dropping bare records.
    /// </summary>
    [Test]
    public async Task BuildEntries_MergesSessionsAndWorktrees()
    {
        var sessions = new List<SessionSeed>
        {
            new("s1", "Jump Palette", "/repo/.worktrees/jump-palette", null, "working", true),
            new("s2", "Main", "/repo", "main", "idle", false),
        };

        var entries = WorktreeJumpSeeder.BuildEntries(sessions, Worktrees);

        await Assert.That(entries.Count).IsEqualTo(3);
        await Assert.That(entries[0].SessionId).IsEqualTo("s1");
        await Assert.That(entries[0].Branch).IsEqualTo("feat/jump-palette");
        await Assert.That(entries[0].Status).IsEqualTo("working ●");
        await Assert.That(entries[1].SessionId).IsEqualTo("s2");
        await Assert.That(entries[1].Status).IsEqualTo("idle");
        await Assert.That(entries[2].SessionId).IsEqualTo(string.Empty);
        await Assert.That(entries[2].Path).IsEqualTo("/repo/.worktrees/detached-wt");
        await Assert.That(entries[2].Status).IsEqualTo(WorktreeJumpSeeder.NoSessionStatus);
    }

    /// <summary>Sessions keep their incoming order regardless of worktree order.</summary>
    [Test]
    public async Task BuildEntries_PreservesSessionOrder()
    {
        var sessions = new List<SessionSeed>
        {
            new("s2", "Second", "/b", "b1", "idle", false),
            new("s1", "First", "/a", "a1", "idle", false),
        };

        var entries = WorktreeJumpSeeder.BuildEntries(sessions, Array.Empty<GitWorktreeInfo>());

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0].SessionId).IsEqualTo("s2");
        await Assert.That(entries[1].SessionId).IsEqualTo("s1");
    }

    /// <summary>
    ///     Sub-agent seeds are hidden from the jump palette by default — they
    ///     live in the <c>subagents</c> panel instead.
    /// </summary>
    [Test]
    public async Task BuildEntries_HidesSubagentSeeds_ByDefault()
    {
        var sessions = new List<SessionSeed>
        {
            new("s1", "User Work", "/repo", "main", "idle", false),
            new("s2", "task(explore): dig", "/repo", "main", "working", false, true),
        };

        var entries = WorktreeJumpSeeder.BuildEntries(sessions, Array.Empty<GitWorktreeInfo>());

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].SessionId).IsEqualTo("s1");
    }

    /// <summary>Opt-in flag keeps sub-agent seeds (order preserved).</summary>
    [Test]
    public async Task BuildEntries_IncludeSubagents_ShowsThem()
    {
        var sessions = new List<SessionSeed>
        {
            new("s1", "User Work", "/repo", "main", "idle", false),
            new("s2", "task(explore): dig", "/repo", "main", "working", false, true),
        };

        var entries = WorktreeJumpSeeder.BuildEntries(sessions, Array.Empty<GitWorktreeInfo>(), includeSubagents: true);

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0].SessionId).IsEqualTo("s1");
        await Assert.That(entries[1].SessionId).IsEqualTo("s2");
    }
}
