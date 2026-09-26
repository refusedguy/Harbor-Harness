using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="WorktreeJumpSeeder" /> — the pure jump-palette
///     seeding (slice 2): <c>git worktree list --porcelain</c> parsing plus the
///     session/worktree merge into <see cref="WorktreeJumpEntry" /> rows.
///     Deterministic — pure string/list transforms only.
/// </summary>
public class WorktreeJumpSeederTests
{
    private const string Porcelain =
        "worktree /repo\n" +
        "HEAD abc123\n" +
        "branch refs/heads/main\n" +
        "\n" +
        "worktree /repo/.worktrees/jump-palette\n" +
        "HEAD def456\n" +
        "branch refs/heads/feat/jump-palette\n" +
        "\n" +
        "worktree /repo/.worktrees/detached-wt\n" +
        "HEAD 789aaa\n" +
        "detached\n" +
        "\n" +
        "worktree /repo/.git\n" +
        "HEAD abc123\n" +
        "bare\n";

    /// <summary>
    ///     <see cref="WorktreeJumpSeeder.ParsePorcelain" /> extracts paths,
    ///     branches, detached (null branch) and bare records.
    /// </summary>
    [Test]
    public async Task ParsePorcelain_ExtractsPathBranchDetachedAndBare()
    {
        var infos = WorktreeJumpSeeder.ParsePorcelain(Porcelain);

        await Assert.That(infos.Count).IsEqualTo(4);
        await Assert.That(infos[0].Path).IsEqualTo("/repo");
        await Assert.That(infos[0].Branch).IsEqualTo("main");
        await Assert.That(infos[0].IsBare).IsFalse();
        await Assert.That(infos[1].Branch).IsEqualTo("feat/jump-palette");
        await Assert.That(infos[2].Branch).IsNull();
        await Assert.That(infos[3].IsBare).IsTrue();
    }

    /// <summary>Null, empty and garbage inputs yield no records (never throw).</summary>
    [Test]
    public async Task ParsePorcelain_NullEmptyGarbage_YieldsNone()
    {
        await Assert.That(WorktreeJumpSeeder.ParsePorcelain(null)).IsEmpty();
        await Assert.That(WorktreeJumpSeeder.ParsePorcelain(string.Empty)).IsEmpty();
        await Assert.That(WorktreeJumpSeeder.ParsePorcelain("not a worktree listing\n")).IsEmpty();
    }

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
        var worktrees = WorktreeJumpSeeder.ParsePorcelain(Porcelain);

        var entries = WorktreeJumpSeeder.BuildEntries(sessions, worktrees);

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

        var entries = WorktreeJumpSeeder.BuildEntries(sessions, Array.Empty<WorktreeInfo>());

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0].SessionId).IsEqualTo("s2");
        await Assert.That(entries[1].SessionId).IsEqualTo("s1");
    }
}
