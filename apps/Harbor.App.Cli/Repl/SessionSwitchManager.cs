using System.Collections.Immutable;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Narrow target surface for the session-switch coordinator steps (#197):
///     steps talk to this contract, never directly to <see cref="IReplHost" />
///     (fixes the Feature Envy spread across Bridge/Timeline/Selection/Screen).
/// </summary>
internal interface ISessionSwitchTarget
{
    /// <summary>Denial message when the switch must not start, null when clear.</summary>
    string? CheckGuard(string sessionId);
    /// <summary>Load the session (announces store/load failures, null on failure).</summary>
    Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct);
    /// <summary>Resolve the session's agent (announces failures, null on failure).</summary>
    AgentDefinition? ResolveAgent(Session session);
    /// <summary>Bind the loaded session + agent definition to the host.</summary>
    void ApplySession(Session session, AgentDefinition definition);
    /// <summary>Full context swap + history replay; returns history on success, null otherwise.</summary>
    Task<IReadOnlyList<AgentMessage>?> ReplayHistoryAsync(Session session, CancellationToken ct);
    /// <summary>Recompute token totals + sidebar identity for the target session.</summary>
    Task RefreshSidebarAsync(Session session, IReadOnlyList<AgentMessage>? history, CancellationToken ct);
    /// <summary>Announce a status line.</summary>
    void Announce(string line);
}

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

    /// <summary>
    ///     Coordinator: guard → load → resolve → apply → replay → sidebar →
    ///     sync → announce. Each step lives on <see cref="ISessionSwitchTarget" />;
    ///     this method only sequences them (#197).
    /// </summary>
    public async Task SwitchToSessionAsync(string sessionId, CancellationToken ct)
    {
        ISessionSwitchTarget target = new HostSwitchTarget(host);

        if (target.CheckGuard(sessionId) is { } denial)
        {
            target.Announce(denial);
            return;
        }

        if (await target.LoadSessionAsync(sessionId, ct).ConfigureAwait(false) is not { } loaded)
        {
            return;
        }

        if (target.ResolveAgent(loaded) is not { } definition)
        {
            return;
        }

        target.ApplySession(loaded, definition);
        _quickSwitch.Push(loaded.Id);

        var history = await target.ReplayHistoryAsync(loaded, ct).ConfigureAwait(false);
        await target.RefreshSidebarAsync(loaded, history, ct).ConfigureAwait(false);

        await SyncSessionsToStoreAsync(ct).ConfigureAwait(false);
        target.Announce($"⇄ сессия → {loaded.Title} ({loaded.Id[..Math.Min(8, loaded.Id.Length)]})");
        // Issue #389 — a session switch is what opens a tab. Idempotent by
        // construction, since OpenTab activates an already-open session instead
        // of appending a duplicate, so this is safe on every switch.
        _ = host.Store.Dispatch(new AppMsg.OpenTab(
            new SessionTab(SessionId.Create(loaded.Id), loaded.Title)));
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

        _ = host.Store.Dispatch(new ChatAppMsg.SyncSessions(
            listed.Value
                .Select(s => new SessionInfo(SessionId.Create(s.Id), s.Title, s.CreatedAt, s.UpdatedAt, "active", s.IsSubagent()))
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

    /// <summary>
    ///     Opens the session-switch palette — the REPL's answer to the tab
    ///     strip's <c>Ctrl+T</c> open/switch action (#389). Lives here, next to
    ///     the switch coordinator, so the keyboard and the palette can never
    ///     disagree about what "switch session" opens: both go through
    ///     <see cref="SessionsCommand" />.
    /// </summary>
    public void OpenSessionsPalette() =>
        TaskFireAndForget.Forget(
            SessionsCommand.ShowSwitchListAsync(host, CancellationToken.None));

    /// <summary>
    ///     Default <see cref="ISessionSwitchTarget" /> over <see cref="IReplHost" />:
    ///     owns every host touchpoint (Bridge/Timeline/Selection/Screen/Sidebar/
    ///     Agent) so the coordinator steps stay envy-free (#197).
    /// </summary>
    private sealed class HostSwitchTarget(IReplHost host) : ISessionSwitchTarget
    {
        public string? CheckGuard(string sessionId)
        {
            if (sessionId == host.SessionModel.Id)
            {
                return "⇄ уже в этой сессии";
            }

            if (host.Agent.State.IsRunning)
            {
                return "⇄ агент занят — сессия не переключена";
            }

            return null;
        }

        public async Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct)
        {
            if (host.SessionStore is not { } store)
            {
                Announce("⇄ переключение недоступно: хост без хранилища сессий");
                return null;
            }

            var loaded = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                Announce("! " + loaded.Error);
                return null;
            }

            return loaded.Value;
        }

        public AgentDefinition? ResolveAgent(Session session)
        {
            var definition = host.AgentRegistry.GetAgent(AgentName.Create(session.Agent));
            if (definition.IsFailure)
            {
                Announce("! " + definition.Error);
                return null;
            }

            return definition.Value;
        }

        public void ApplySession(Session session, AgentDefinition definition)
        {
            host.Agent.Initialize(session, definition);
            host.SessionModel = session;
        }

        public async Task<IReadOnlyList<AgentMessage>?> ReplayHistoryAsync(Session session, CancellationToken ct)
        {
            // Full context swap: drop old blocks/selection/tracking, then replay
            // the target session's persisted history so the switch lands on a
            // live transcript instead of an empty feed.
            host.Bridge.ResetMessageTracking();
            host.Timeline.Clear();
            host.Selection.Clear();
            host.ScrollTimelineToEnd();

            if (host.SessionStore is not { } store)
            {
                return null;
            }

            var history = await store.GetMessagesAsync(session.Id, ct).ConfigureAwait(false);
            if (history.IsSuccess && history.Value.Count > 0)
            {
                host.Bridge.ReplayHistory(history.Value);
            }

            return history.IsSuccess ? history.Value : null;
        }

        public async Task RefreshSidebarAsync(Session session, IReadOnlyList<AgentMessage>? history, CancellationToken ct)
        {
            if (host.Screen.Sidebar is not { } sidebar)
            {
                return;
            }

            // Token totals belong to the target session: recompute from its
            // persisted history instead of keeping the previous session's.
            // (Inline recount stays here with the sidebar it feeds — moving it
            // out would split one write across two owners.)
            long tokensIn = 0, tokensOut = 0;
            if (history is not null)
            {
                foreach (var message in history)
                {
                    if (message is AssistantMessage assistant)
                    {
                        tokensIn += assistant.Usage.InputTokens;
                        tokensOut += assistant.Usage.OutputTokens;
                    }
                }
            }

            int window = await host.ResolveContextWindowAsync(
                session.ProviderId, session.Model, ct).ConfigureAwait(false);
            sidebar.State = sidebar.State with
            {
                SessionTitle = session.Title,
                SessionId = session.Id,
                Model = $"{session.ProviderId}/{session.Model}",
                Agent = session.Agent,
                MessageCount = host.Timeline.Count,
                ContextWindow = window,
                TokensIn = tokensIn,
                TokensOut = tokensOut,
            };
        }

        public void Announce(string line) => host.Bridge.AppendSystemLine(line);
    }
}
