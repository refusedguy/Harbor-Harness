using System.Collections.Immutable;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Session lifecycle behind the REPL (SRP extraction from the runner):
///     full-context switches with history replay, quick-switch slots, session
///     list sync into the TEA store, and welcome-time chrome seeding (sidebar
///     identity, slot seeding). Collaborates through <see cref="IReplHost"/>.
///     The <c>onSwitched</c> hook lets the owner drop cross-cutting state
///     (queued prompts) without the manager knowing the prompt pipeline.
/// </summary>
internal sealed class SessionSwitchManager(IReplHost host, Action onSwitched)
{
    private readonly QuickSwitchSlots _quickSwitch = new();

    public async Task SwitchToSessionAsync(string sessionId, CancellationToken ct)
    {
        if (sessionId == host.SessionModel.Id)
        {
            host.Bridge.AppendSystemLine("⇄ уже в этой сессии");
            return;
        }

        if (host.Agent.State.IsRunning)
        {
            host.Bridge.AppendSystemLine("⇄ агент занят — сессия не переключена");
            return;
        }

        if (host.SessionStore is not { } store)
        {
            host.Bridge.AppendSystemLine("⇄ переключение недоступно: хост без хранилища сессий");
            return;
        }

        var loaded = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            host.Bridge.AppendSystemLine("! " + loaded.Error);
            return;
        }

        var definition = host.AgentRegistry
            .GetAgent(AgentName.Create(loaded.Value.Agent));
        if (definition.IsFailure)
        {
            host.Bridge.AppendSystemLine("! " + definition.Error);
            return;
        }

        host.Agent.Initialize(loaded.Value, definition.Value);
        host.SessionModel = loaded.Value;
        _quickSwitch.Push(loaded.Value.Id);

        // Full context swap: drop old blocks/selection/tracking, then replay
        // the target session's persisted history so the switch lands on a
        // live transcript instead of an empty feed.
        host.Bridge.ResetMessageTracking();
        host.Timeline.Clear();
        host.Selection.Clear();
        host.ScrollTimelineToEnd();
        var history = await store.GetMessagesAsync(loaded.Value.Id, ct).ConfigureAwait(false);
        if (history.IsSuccess && history.Value.Count > 0)
        {
            host.Bridge.ReplayHistory(history.Value);
        }

        if (host.Screen.Sidebar is { } sidebar)
        {
            int window = await host.ResolveContextWindowAsync(
                loaded.Value.ProviderId, loaded.Value.Model, ct).ConfigureAwait(false);
            sidebar.State = sidebar.State with
            {
                SessionTitle = loaded.Value.Title,
                SessionId = loaded.Value.Id,
                Model = $"{loaded.Value.ProviderId}/{loaded.Value.Model}",
                Agent = loaded.Value.Agent,
                MessageCount = host.Timeline.Count,
                ContextWindow = window,
            };
        }

        await SyncSessionsToStoreAsync(ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine($"⇄ сессия → {loaded.Value.Title} ({loaded.Value.Id[..Math.Min(8, loaded.Value.Id.Length)]})");
        onSwitched();
        host.WakeUp();
    }

    public async Task SwitchToSlotAsync(char chord, CancellationToken ct)
    {
        if (_quickSwitch.Resolve(chord) is not { } sessionId)
        {
            host.Bridge.AppendSystemLine($"⇄ slot {chord}: пусто");
            return;
        }

        await SwitchToSessionAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Session list sync (sprint UI-V2 P4): recent sessions from the store
    ///     go into the TEA store so the session-sidebar panel renders live.
    ///     The always-visible sidebar stays clean (session/model/agent/tokens
    ///     only). Best-effort — hosts without a session store skip silently.
    /// </summary>
    public async Task SyncSessionsToStoreAsync(CancellationToken ct)
    {
        if (host.SessionStore is not { } store)
        {
            return;
        }

        var listed = await store.ListAsync(ct: ct).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            return;
        }

        _ = host.Store.Dispatch(new UiMsg.SyncSessions(
            listed.Value
                .Select(s => new SessionInfo(SessionId.Create(s.Id), s.Title, s.CreatedAt, s.UpdatedAt, "active"))
                .ToImmutableArray(),
            SessionId.Create(host.SessionModel.Id)));
    }

    /// <summary>
    ///     Welcome-time chrome: sidebar identity + model before the first frame
    ///     paints, session list sync, quick-switch slot seeding (most-recent-
    ///     first, slot 1 = hottest). Best-effort — hosts without a session
    ///     store (smoke tests) skip slot seeding.
    /// </summary>
    public async Task SeedWelcomeChromeAsync(string model)
    {
        var ct = CancellationToken.None;
        if (host.Screen.Sidebar is { } sidebar)
        {
            int window = await host.ResolveContextWindowAsync(
                host.SessionModel.ProviderId, host.SessionModel.Model, ct).ConfigureAwait(false);
            sidebar.State = sidebar.State with
            {
                SessionTitle = host.SessionModel.Title,
                SessionId = host.SessionModel.Id,
                Model = model,
                Agent = host.SessionModel.Agent,
                MessageCount = 0,
                ContextWindow = window,
            };
        }

        await SyncSessionsToStoreAsync(ct).ConfigureAwait(false);

        if (host.SessionStore is { } store
            && await store.ListAsync(ct: ct).ConfigureAwait(false) is { IsSuccess: true } listed)
        {
            var recent = listed.Value;
            for (int i = 0; i < recent.Count && i < QuickSwitchSlots.Count; i++)
            {
                _quickSwitch.Assign(i + 1, recent[i].Id);
            }
        }

        host.WakeUp();
    }
}
