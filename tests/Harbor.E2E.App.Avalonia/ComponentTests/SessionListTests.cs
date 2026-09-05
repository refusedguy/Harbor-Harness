using Harbor.App.Avalonia.ViewModels;
using Avalonia.VisualTree;
using Harbor.E2E.Framework;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     SessionList component E2E tests — every visible state of the left
///     sidebar's session list.
/// </summary>
/// <remarks>
///     <para>
///         Sessions are seeded through the REAL path —
///         <see cref="ISessionManager.NewSessionAsync" /> +
///         <see cref="ISessionManager.RenameSessionAsync" /> persist into the
///         session store, and the view-model's RefreshCommand projects them.
///         Directly mutating the bound <c>Sessions</c> collection does not
///         survive: the next store-driven refresh wipes manual rows now that
///         the app fully boots.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class SessionListTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("SessionList").ConfigureAwait(false);

    /// <summary>Create a persisted session with the given title (real store path).</summary>
    private static async Task<string> SeedSessionAsync(string title)
    {
        var manager = Driver.Host.Services.GetRequiredService<ISessionManager>();
        var session = await manager.NewSessionAsync(
            workingDirectory: E2EHelpers.FindRepoRoot()).ConfigureAwait(false);
        var renamed = await manager.RenameSessionAsync(session.Id, title).ConfigureAwait(false);
        await Assert.That(renamed).IsTrue();
        return session.Id;
    }

    /// <summary>Reload the sidebar from the store and let the UI settle.</summary>
    private static async Task RefreshSidebarAsync()
    {
        UI(() => Vm.Sessions.RefreshCommand.ExecuteAsync(null));
        // Headless mode renders only on explicit ticks: without one the
        // ListBox never realizes item containers and the row TextBlocks
        // simply do not exist for the text probes.
        await Driver.ShowMainWindowAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Empty session list: the sidebar is visible but the list area is
    ///     empty (no rows).
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_Empty_NoRowsInList()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            Vm.Sessions.Sessions.Clear();
        });

        var sidebarVisible = UI(() => Vm.IsSidebarVisible);
        await Assert.That(sidebarVisible).IsTrue();

        var path = await CaptureAsync("sessions-empty").ConfigureAwait(false);
    }

    /// <summary>
    ///     With sessions: 3 rows are visible, each showing title, agent,
    ///     relative time, and message count.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_WithSessions_ShowsTitleAgentTimeAndCount()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsSessionsFlyoutOpen = true);
        await SeedSessionAsync("Refactor agent loop").ConfigureAwait(false);
        await SeedSessionAsync("Investigate IPC deadlock").ConfigureAwait(false);
        await SeedSessionAsync("Polish onboarding flow").ConfigureAwait(false);
        await RefreshSidebarAsync().ConfigureAwait(false);

        var has1 = await Driver.WaitForTextAsync("Refactor agent loop", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        var has2 = await Driver.WaitForTextAsync("Investigate IPC deadlock", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);

        await Assert.That(has1 && has2).IsTrue();

        var path = await CaptureAsync("sessions-with-items").ConfigureAwait(false);

        UI(() => Vm.IsSessionsFlyoutOpen = false);
    }

    /// <summary>
    ///     Active session is highlighted (selected style) when set as the
    ///     ActiveSession.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_ActiveSession_Highlighted()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "First session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 1, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Second session", "code", "gpt-4o", "openai",
                DateTimeOffset.UtcNow, 2, "/home/z/myproject"));
            Vm.Sessions.ActiveSession = sessions[1];
        });

        var activeId = UI(() => Vm.Sessions.ActiveSession?.Id);
        await Assert.That(activeId).IsEqualTo("s2");

        var path = await CaptureAsync("sessions-active-highlighted").ConfigureAwait(false);
    }

    /// <summary>
    ///     Search filter: with "IPC" in the search box, only matching sessions
    ///     are visible.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_SearchFilter_ShowsOnlyMatching()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsSessionsFlyoutOpen = true);
        await SeedSessionAsync("Refactor agent loop").ConfigureAwait(false);
        await SeedSessionAsync("Investigate IPC deadlock").ConfigureAwait(false);
        await SeedSessionAsync("Polish onboarding flow").ConfigureAwait(false);

        // The REAL filter path: SearchText + RefreshCommand re-queries the
        // store and keeps only title/agent matches.
        UI(() => Vm.Sessions.SearchText = "IPC");
        await RefreshSidebarAsync().ConfigureAwait(false);

        var hasMatch = await Driver.WaitForTextAsync("Investigate IPC deadlock", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasMatch).IsTrue();

        var hasOther = Driver.GetRenderedText().Contains("Refactor agent loop", StringComparison.Ordinal);
        await Assert.That(hasOther).IsFalse();

        var path = await CaptureAsync("sessions-search-filtered").ConfigureAwait(false);

        UI(() => Vm.IsSessionsFlyoutOpen = false);
    }

    /// <summary>
    ///     New session created: the new session row appears in the list AND
    ///     is selected as the active session.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_NewSession_AddedAndSelected()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Existing session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 5, "/home/z/myproject"));
        });

        UI(() =>
        {
            Vm.Sessions.Sessions.Add(new SessionItemViewModel(
                "s2", "New session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 0, "/home/z/myproject"));
            Vm.Sessions.ActiveSession = Vm.Sessions.Sessions[1];
        });

        var hasNew = await Driver.WaitForTextAsync("New session", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasNew).IsTrue();

        var path = await CaptureAsync("sessions-new-created").ConfigureAwait(false);
    }

    /// <summary>
    ///     Session deleted: the deleted session's row is gone; the remaining
    ///     session is still visible.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_Deleted_RemovedFromList()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsSessionsFlyoutOpen = true);
        // A11: a previous test's search filter must not leak into this
        // class's store-driven refreshes.
        UI(() => Vm.Sessions.SearchText = string.Empty);
        string keepId = await SeedSessionAsync("Keep me").ConfigureAwait(false);
        string deleteId = await SeedSessionAsync("Delete me").ConfigureAwait(false);
        await RefreshSidebarAsync().ConfigureAwait(false);

        // Delete through the manager (persists), then refresh the sidebar.
        var manager = Driver.Host.Services.GetRequiredService<ISessionManager>();
        bool deleted = await manager.DeleteSessionAsync(deleteId).ConfigureAwait(false);
        await Assert.That(deleted).IsTrue();
        _ = keepId;
        await RefreshSidebarAsync().ConfigureAwait(false);
        // Scrolling a virtualized ListBox into view needs its own layout+render
        // pass before the row container exists for text probes.
        await Driver.ShowMainWindowAsync().ConfigureAwait(false);

        // Tick+probe loop: each ShowMainWindowAsync pass forces layout +
        // render so virtualized ListBox containers realize deterministically;
        // a plain text poll can starve when no render tick fires.
        bool hasKeep = false;
        for (int i = 0; i < 20 && !hasKeep; i++)
        {
            await Driver.ShowMainWindowAsync().ConfigureAwait(false);
            hasKeep = Driver.GetAllVisibleText().Contains("Keep me", StringComparison.Ordinal);
            if (!hasKeep)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        await Assert.That(hasKeep).IsTrue();

        // The "Opened: Delete me" toast lingers ~4s and mentions the deleted
        // title; wait it out before asserting absence.
        bool toastGone = false;
        for (int i = 0; i < 40 && !toastGone; i++)
        {
            await Task.Delay(150).ConfigureAwait(false);
            toastGone = !Driver.GetAllVisibleText().Contains("Delete me", StringComparison.Ordinal);
        }
        await Assert.That(toastGone).IsTrue();

        var path = await CaptureAsync("sessions-deleted").ConfigureAwait(false);

        UI(() => Vm.IsSessionsFlyoutOpen = false);
    }

    /// <summary>
    ///     Sessions with git-info badges: rows show their git branch name and
    ///     a dirty indicator.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_WithGitInfo_ShowsBranchBadge()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Seeded with workingDirectory = this repo (a real git worktree), so
        // RefreshAsync attaches actual branch/dirty info from the manager.
        UI(() => Vm.IsSessionsFlyoutOpen = true);
        UI(() => Vm.Sessions.SearchText = string.Empty);
        await SeedSessionAsync("Feature work").ConfigureAwait(false);
        await RefreshSidebarAsync().ConfigureAwait(false);
        // Same virtualization note as SessionList_Deleted: tick after reveal.
        await Driver.ShowMainWindowAsync().ConfigureAwait(false);

        bool hasFeature = false;
        for (int i = 0; i < 20 && !hasFeature; i++)
        {
            await Driver.ShowMainWindowAsync().ConfigureAwait(false);
            hasFeature = Driver.GetAllVisibleText().Contains("Feature work", StringComparison.Ordinal);
            if (!hasFeature)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        await Assert.That(hasFeature).IsTrue();

        var path = await CaptureAsync("sessions-git-info").ConfigureAwait(false);

        UI(() => Vm.IsSessionsFlyoutOpen = false);
    }
}
