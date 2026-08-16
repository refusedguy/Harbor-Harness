using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Reducers;

/// <summary>
///     Pure reducer: <c>(AgentEvent, ChromeViewState) → ChromeViewState</c>.
/// </summary>
/// <remarks>
///     <para>
///         Maps agent activity into chrome state (active session, navigation stack,
///         modals, toasts). Every interactive renderer funnels its events through this —
///         there is no per-renderer <c>switch (AgentEvent)</c> anywhere.
///     </para>
///     <para>
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///         responsibility of the host effect runner.
///     </para>
/// </remarks>
public static partial class ChromeReducer
{
    /// <summary>
    ///     Apply an agent event to the chrome view state, returning the next immutable snapshot.
    /// </summary>
    public static ChromeViewState Reduce(AgentEvent @event, ChromeViewState state) => @event switch
    {
        SessionChangedEvent sce => state with { ActiveSessionId = SessionId.Create(sce.SessionId) },
        // TODO: NavigationRequestedEvent does not yet exist in Harbor.Abstractions.Events.
        // Wire navigation push/pop here once the event is added.
        _ => state
    };
}
