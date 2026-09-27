using Harbor.Abstractions.Events;

namespace Harbor.Terminal.Abstractions.Renderers;

/// <summary>
///     Visitor-style handler for a subset of <see cref="AgentEvent"/>s (issue #185).
///     Renderers register one handler per event group in
///     <see cref="BaseTuiRenderer"/> instead of duplicating a per-renderer
///     <c>switch (AgentEvent)</c>: a new event type means a new handler class
///     plus one registration line — no edits to existing render paths.
/// </summary>
public interface IAgentEventHandler
{
    /// <summary>Whether this handler renders the given event.</summary>
    bool CanHandle(AgentEvent @event);

    /// <summary>
    ///     Render the event. Only called after <see cref="CanHandle"/> returned
    ///     <c>true</c>; per-handler exceptions are isolated by the dispatcher.
    /// </summary>
    Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default);
}
