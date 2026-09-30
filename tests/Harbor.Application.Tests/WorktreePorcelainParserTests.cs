namespace Harbor.Application.Tests;

using Harbor.Application.Git;
using TUnit.Assertions;

/// <summary>
///     <c>WorktreePorcelainParser</c> — the parser that used to be
///     <c>WorktreeJumpSeeder.ParsePorcelain</c> in <c>Harbor.Ui.Framework.Services</c>
///     and moved here in #666, next to the <c>Process.Start</c> whose output it
///     reads.
/// </summary>
/// <remarks>
/// <para>
///     These assertions MOVED with the code, they were not written for it. The
///     state machine is byte-for-byte the original, including its tolerance of
///     CRLF and of lines it does not recognise, and the two behaviours worth
///     pinning are unchanged: a detached worktree has a null branch rather than
///     the empty string, and a <c>bare</c> record is flagged rather than dropped —
///     dropping it is the caller's job, because only the caller knows whether a
///     bare entry should become a row.
/// </para>
/// <para>
///     The merge tests that used to share this fixture now build
///     <c>GitWorktreeInfo</c> literals directly, so a change to git's output
///     format can no longer break a test that only cares how sessions and
///     worktrees are combined.
/// </para>
/// </remarks>
public class WorktreePorcelainParserTests
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
    ///     Paths, branches, detached (null branch) and bare records all survive
    ///     the parse, in the order git emitted them.
    /// </summary>
    [Test]
    public async Task Parse_ExtractsPathBranchDetachedAndBare()
    {
        var infos = WorktreePorcelainParser.Parse(Porcelain);

        await Assert.That(infos.Count).IsEqualTo(4);
        await Assert.That(infos[0].Path).IsEqualTo("/repo");
        await Assert.That(infos[0].Branch).IsEqualTo("main");
        await Assert.That(infos[0].IsBare).IsFalse();
        await Assert.That(infos[1].Path).IsEqualTo("/repo/.worktrees/jump-palette");
        await Assert.That(infos[1].Branch).IsEqualTo("feat/jump-palette");
        await Assert.That(infos[2].Branch).IsNull();
        await Assert.That(infos[3].Path).IsEqualTo("/repo/.git");
        await Assert.That(infos[3].IsBare).IsTrue();
    }

    /// <summary>Null, empty and unrecognised input yield no records, and never throw.</summary>
    [Test]
    public async Task Parse_NullEmptyGarbage_YieldsNone()
    {
        await Assert.That(WorktreePorcelainParser.Parse(null)).IsEmpty();
        await Assert.That(WorktreePorcelainParser.Parse(string.Empty)).IsEmpty();
        await Assert.That(WorktreePorcelainParser.Parse("not a worktree listing\n")).IsEmpty();
    }

    /// <summary>
    ///     CRLF is what git emits on Windows and this runs on Windows CI, so the
    ///     tolerance is load-bearing rather than decorative: without the trim a
    ///     path would arrive with a trailing <c>\r</c> and match nothing.
    /// </summary>
    [Test]
    public async Task Parse_CrlfInput_DoesNotLeaveCarriageReturnsInPaths()
    {
        var infos = WorktreePorcelainParser.Parse(
            "worktree /repo\r\nHEAD abc123\r\nbranch refs/heads/main\r\n");

        await Assert.That(infos.Count).IsEqualTo(1);
        await Assert.That(infos[0].Path).IsEqualTo("/repo");
        await Assert.That(infos[0].Branch).IsEqualTo("main");
    }
}
