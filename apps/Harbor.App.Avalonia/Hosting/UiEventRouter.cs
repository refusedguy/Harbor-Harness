using System.Diagnostics;
using Harbor.Abstractions.Events;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Avalonia.Hosting;

/// <summary>
///     Post-build cross-wirings that need a built service provider:
///     binding <see cref="UiStore" /> → <see cref="AvaloniaDispatcherAdapter" />
///     (exactly once, idempotent) and subscribing <see cref="IEventBus" /> →
///     per-session routing. Extracted from <see cref="AppHost.BuildAsync" />
///     so the composition root stays a thin orchestrator (di-design §7.3).
/// </summary>
internal static class UiEventRouter
{
    /// <summary>
    ///     Bind the DI-singleton <see cref="UiStore" /> as the INITIAL store the
    ///     dispatcher is bound to (<see cref="SessionManager.RebindChatViewModel" />
    ///     rebinds to per-session stores as sessions are opened/switched), then
    ///     route each agent event to the correct per-session store.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Without this routing, a background agent in session A would leak
    ///         its events into the active session B's chat transcript.
    ///     </para>
    ///     <para>
    ///         Routing logic:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             AgentStartEvent / CompactionStartedEvent / CompactionCompletedEvent
    ///             / SessionStatsEvent carry an explicit SessionId → route directly.
    ///         </item>
    ///         <item>
    ///             Other events (TurnStart, MessageUpdate, ToolExecution*, etc.)
    ///             don't carry a session id. They are matched to the session id
    ///             that the most recent AgentStartEvent declared. With a singleton
    ///             IAgent this is correct because only one PromptAsync can be in
    ///             flight at a time.
    ///         </item>
    ///         <item>
    ///             Fallback: route to the active session's store (or the DI
    ///             singleton store if there's no active session yet).
    ///         </item>
    ///     </list>
    /// </remarks>
    internal static void Bind(IServiceProvider services)
    {
        var uiStore = services.GetRequiredService<UiStore>();
        services.GetRequiredService<IDispatcherAdapter>().Bind(uiStore);

        var sessionManager = (SessionManager)services.GetRequiredService<ISessionManager>();
        var dispatcherAdapter = (AvaloniaDispatcherAdapter)services.GetRequiredService<IDispatcherAdapter>();
        var eventBus = services.GetRequiredService<IEventBus>();
        string? currentAgentSessionId = null;
        eventBus.Subscribe(async (evt, ct) =>
        {
            try
            {
                string? sessionId = ExtractSessionId(evt, ref currentAgentSessionId);
                UiStore? targetStore = null;
                if (sessionId is not null)
                {
                    targetStore = sessionManager.GetContext(sessionId)?.Store;
                }
                targetStore ??= sessionManager.ActiveContext?.Store
                                ?? dispatcherAdapter.BoundStore
                                ?? uiStore;
                targetStore.Dispatch(evt);
            }
            catch (Exception ex)
            {
                // Defensive: never let a subscriber exception crash the
                // event bus (which would silently drop all subsequent events).
                Debug.WriteLine($"EventBus subscriber crashed: {ex}");
            }
            await Task.CompletedTask;
        });
    }

    /// <summary>
    ///     Extract the session id from an agent event. For events that
    ///     carry an explicit SessionId (AgentStartEvent, CompactionStartedEvent,
    ///     CompactionCompletedEvent, SessionStatsEvent), returns that id and
    ///     (for AgentStartEvent) updates <paramref name="currentAgentSessionId" />.
    ///     For other events, returns the last-seen AgentStartEvent session id
    ///     so streaming events (MessageUpdate, ToolExecution*, etc.) route to
    ///     the same store as the run they belong to.
    /// </summary>
    /// <param name="evt">The agent event.</param>
    /// <param name="currentAgentSessionId">
    ///     Ref to the tracked current
    ///     running-session id (set by AgentStartEvent).
    /// </param>
    /// <returns>The session id for routing, or null if unknown.</returns>
    private static string? ExtractSessionId(AgentEvent evt, ref string? currentAgentSessionId)
    {
        switch (evt)
        {
            case AgentStartEvent start:
                currentAgentSessionId = start.SessionId;
                return start.SessionId;
            case CompactionStartedEvent cs:
                currentAgentSessionId = cs.SessionId;
                return cs.SessionId;
            case CompactionCompletedEvent cc:
                return cc.SessionId;
            case SessionStatsEvent ss:
                return ss.SessionId;
            case AgentEndEvent:
                currentAgentSessionId = null;
                return null;
            default:
                return currentAgentSessionId;
        }
    }
}
