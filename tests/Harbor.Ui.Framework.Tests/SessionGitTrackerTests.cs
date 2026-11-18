using Harbor.Abstractions.Git;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #2617: <see cref="SessionGitTracker" /> is a singleton whose cache
///     is written on thread-pool continuations (via
///     <see cref="SessionLifecycleService.RefreshGitInfo" />) and read on the UI
///     thread (via <see cref="SessionManager.GetGitInfo" />). These tests pin the
///     observable contract: unknown sessions read as the empty sentinel, refresh
///     publishes the mapped status, and concurrent refresh/get never tears the
///     cache.
/// </summary>
public class SessionGitTrackerTests
{
    [Test]
    public async Task Get_UnknownSession_ReturnsEmpty()
    {
        var tracker = new SessionGitTracker();

        await Assert.That(tracker.Get("not-tracked")).IsEqualTo(GitSessionInfo.Empty);
    }

    [Test]
    public async Task Refresh_NullGitService_LeavesEmpty()
    {
        var tracker = new SessionGitTracker();

        tracker.Refresh("s1", "/any/dir", null);

        await Assert.That(tracker.Get("s1")).IsEqualTo(GitSessionInfo.Empty);
    }

    [Test]
    public async Task Refresh_PublishesMappedStatus()
    {
        var service = new GitService(
            new StubGitQuery(new GitWorkspaceStatus("main", true, 3, "2 hours ago")),
            NullLogger<GitService>.Instance);
        var tracker = new SessionGitTracker();

        tracker.Refresh("s1", "/any/dir", service);

        var info = tracker.Get("s1");
        await Assert.That(info.Branch).IsEqualTo("main");
        await Assert.That(info.IsDirty).IsTrue();
        await Assert.That(info.DirtyCount).IsEqualTo(3);
        await Assert.That(info.LastCommit).IsEqualTo("2 hours ago");
    }

    [Test]
    public async Task Refresh_ConcurrentWritesAndReads_DoesNotTearCache()
    {
        var service = new GitService(
            new StubGitQuery(new GitWorkspaceStatus("main", true, 1, null)),
            NullLogger<GitService>.Instance);
        var tracker = new SessionGitTracker();
        const int sessions = 500;

        await Task.WhenAll(Enumerable.Range(0, sessions).Select(i => Task.Run(() =>
        {
            var id = $"s{i}";
            tracker.Refresh(id, "/any/dir", service);
            _ = tracker.Get(id);
        })));

        for (var i = 0; i < sessions; i++)
            await Assert.That(tracker.Get($"s{i}").Branch).IsEqualTo("main");
    }

    private sealed class StubGitQuery(GitWorkspaceStatus result) : IGitQuery
    {
        public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default) => result;

        public IReadOnlyList<GitWorktreeInfo> ListWorktrees(
            string directory,
            CancellationToken cancellationToken = default) => Array.Empty<GitWorktreeInfo>();
    }
}
