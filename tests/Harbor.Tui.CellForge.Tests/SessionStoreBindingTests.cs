using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Track D (#27): <see cref="CellForgeTuiRenderer" /> binds to the active
///     session's <see cref="SessionContext.Store" /> when an
///     <see cref="ISessionManager" /> is injected, rebinds (unsubscribe /
///     resubscribe + panel seeding) on session switch, and falls back to the
///     private store when no manager or no active context is present.
/// </summary>
public class SessionStoreBindingTests
{
    private sealed class FakeSessionManager : ISessionManager
    {
        public Session? Active => ActiveContext?.Session;

        public SessionContext? ActiveContext { get; set; }

        public SessionContext? GetContext(string sessionId) =>
            ActiveContext?.Session.Id == sessionId ? ActiveContext : null;

        public SessionStatus GetStatus(string sessionId) => SessionStatus.Idle;

        public void SetStatus(string sessionId, SessionStatus status)
        {
        }

        public void NotifyMessageCount(string sessionId, int count)
        {
        }

        public GitSessionInfo GetGitInfo(string sessionId) => GitSessionInfo.Empty;

        public void RefreshGitInfo(string sessionId, string directory)
        {
        }

        public Task EnsureDefaultSessionAsync() => Task.CompletedTask;

        public Task RebindFromCommonConfigAsync() => Task.CompletedTask;

        public Task<Result<Session>> NewSessionAsync(
            string? agentName = null,
            string? providerId = null,
            string? modelId = null,
            string? workingDirectory = null) =>
            Task.FromResult(Result.Failure<Session>("Not supported in tests."));

        public Task<bool> OpenSessionAsync(string sessionId) => Task.FromResult(true);

        public Task<Result<Session>> BranchActiveAsync() =>
            Task.FromResult(Result.Failure<Session>("Not supported in tests."));

        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(false);

        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(false);

        public event Action<string, SessionStatus>? StatusChanged;

        public event Action<string, int>? MessageCountChanged;
    }

    private static SessionContext ContextFor(string id) =>
        new(Session.Create("/tmp/" + id, "code", "kilocode", "kilo-auto", id) with { Id = id });

    private static AgentStartEvent StartWithUser(string sessionId, string text) =>
        new(sessionId, new AgentMessage[]
        {
            new UserMessage(Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow, text, "code", "model"),
        });

    private static CellForgeTuiRenderer Create(
        RecordingBackend backend,
        FakeSessionManager sessions,
        InputViewModel? inputVm = null) =>
        new(NullLogger<CellForgeTuiRenderer>.Instance, backend, inputVm: inputVm, sessions: sessions);

    [Test]
    public async Task RenderAsync_Dispatches_Into_Active_Session_Store()
    {
        var backend = new RecordingBackend();
        var sessions = new FakeSessionManager { ActiveContext = ContextFor("a") };
        using var renderer = Create(backend, sessions);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(StartWithUser("a", "hello session a"));

        await Assert.That(sessions.ActiveContext!.Store.State.Lines.Any(l => l.Text.Contains("hello session a"))).IsTrue();
        await Assert.That(renderer.Store.State.Lines.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Switch_Rebinds_Projection_And_Seeds_Panels()
    {
        var backend = new RecordingBackend();
        var inputVm = new InputViewModel();
        var sessions = new FakeSessionManager { ActiveContext = ContextFor("a") };
        using var renderer = Create(backend, sessions, inputVm);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(StartWithUser("a", "first"));
        await Assert.That(inputVm.Placeholder).IsEqualTo(CellForgeTuiRenderer.BusyPlaceholder);

        var ctxB = ContextFor("b");
        sessions.ActiveContext = ctxB;
        await renderer.RenderAsync(StartWithUser("b", "second"));

        await Assert.That(ctxB.Store.State.Lines.Any(l => l.Text.Contains("second"))).IsTrue();
        await Assert.That(sessions.GetContext("b")!.Store.State.RegisteredPanelIds.Length).IsGreaterThan(0);
        // The old session store sees nothing from the new session.
        await Assert.That(ctxB.Store.State.Lines.Any(l => l.Text.Contains("first"))).IsFalse();
        // Projection follows the switch: the new store's running state lands in the shared VM.
        await Assert.That(inputVm.Placeholder).IsEqualTo(CellForgeTuiRenderer.BusyPlaceholder);
    }

    [Test]
    public async Task Null_Manager_Falls_Back_To_Private_Store()
    {
        var backend = new RecordingBackend();
        using var renderer = new CellForgeTuiRenderer(NullLogger<CellForgeTuiRenderer>.Instance, backend);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(StartWithUser("s1", "fallback hello"));

        await Assert.That(renderer.Store.State.Lines.Any(l => l.Text.Contains("fallback hello"))).IsTrue();
    }

    [Test]
    public async Task Null_ActiveContext_Falls_Back_To_Private_Store()
    {
        var backend = new RecordingBackend();
        var sessions = new FakeSessionManager { ActiveContext = null };
        using var renderer = Create(backend, sessions);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(StartWithUser("s1", "no active ctx"));

        await Assert.That(renderer.Store.State.Lines.Any(l => l.Text.Contains("no active ctx"))).IsTrue();
    }
}
