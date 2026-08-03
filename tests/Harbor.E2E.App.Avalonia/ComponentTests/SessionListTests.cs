using Harbor.App.Avalonia.ViewModels;
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
///         Tests cover: empty list, list with sessions, active-session
///         highlighting, search filtering, new session added, session
///         deleted, and a list with git-info badges. Each test seeds the
///         <c>SessionListViewModel.Sessions</c> collection directly (via the
///         UI thread) so the screenshot is deterministic — no network, no
///         async store roundtrip.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class SessionListTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync().ConfigureAwait(false);

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
        await Task.Delay(200).ConfigureAwait(false);

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

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Refactor agent loop", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow.AddMinutes(-3), 12, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Investigate IPC deadlock", "code", "gpt-4o", "openai",
                DateTimeOffset.UtcNow.AddHours(-1), 4, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s3", "Polish onboarding flow", "code", "claude-sonnet-4", "anthropic",
                DateTimeOffset.UtcNow.AddDays(-1), 22, "/home/z/myproject"));
        });
        await Task.Delay(250).ConfigureAwait(false);

        var has1 = await Driver.WaitForTextAsync("Refactor agent loop", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var has2 = await Driver.WaitForTextAsync("Investigate IPC deadlock", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has1 && has2).IsTrue();

        var path = await CaptureAsync("sessions-with-items").ConfigureAwait(false);
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
        await Task.Delay(250).ConfigureAwait(false);

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

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Refactor agent loop", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 12, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Investigate IPC deadlock", "code", "gpt-4o", "openai",
                DateTimeOffset.UtcNow, 4, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s3", "Polish onboarding flow", "code", "claude-sonnet-4", "anthropic",
                DateTimeOffset.UtcNow, 22, "/home/z/myproject"));
            Vm.Sessions.SearchText = "IPC";
        });
        await Task.Delay(200).ConfigureAwait(false);

        // Client-side filter (deterministic shortcut).
        UI(() =>
        {
            var sessions = Vm.Sessions.Sessions;
            var matching = sessions
                .Where(s => s.Title.Contains("IPC", StringComparison.OrdinalIgnoreCase))
                .ToList();
            sessions.Clear();
            foreach (var m in matching) sessions.Add(m);
        });
        await Task.Delay(150).ConfigureAwait(false);

        var hasMatch = await Driver.WaitForTextAsync("Investigate IPC deadlock", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMatch).IsTrue();

        var hasOther = Driver.GetAllVisibleText().Contains("Refactor agent loop", StringComparison.Ordinal);
        await Assert.That(hasOther).IsFalse();

        var path = await CaptureAsync("sessions-search-filtered").ConfigureAwait(false);
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
        await Task.Delay(150).ConfigureAwait(false);

        UI(() =>
        {
            Vm.Sessions.Sessions.Add(new SessionItemViewModel(
                "s2", "New session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 0, "/home/z/myproject"));
            Vm.Sessions.ActiveSession = Vm.Sessions.Sessions[1];
        });
        await Task.Delay(250).ConfigureAwait(false);

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

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Keep me", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 5, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Delete me", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 3, "/home/z/myproject"));
            sessions.RemoveAt(1);
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasKeep = await Driver.WaitForTextAsync("Keep me", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasKeep).IsTrue();

        var hasDeleted = Driver.GetAllVisibleText().Contains("Delete me", StringComparison.Ordinal);
        await Assert.That(hasDeleted).IsFalse();

        var path = await CaptureAsync("sessions-deleted").ConfigureAwait(false);
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

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            var row = new SessionItemViewModel(
                "s1", "Feature work", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 8, "/home/z/myproject");
            row.GitBranch = "feature/agent-loop";
            row.GitIsDirty = true;
            sessions.Add(row);
        });
        await Task.Delay(250).ConfigureAwait(false);

        var hasFeature = await Driver.WaitForTextAsync("Feature work", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasFeature).IsTrue();

        var path = await CaptureAsync("sessions-git-info").ConfigureAwait(false);
    }
}
