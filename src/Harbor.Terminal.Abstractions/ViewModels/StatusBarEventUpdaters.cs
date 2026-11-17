using Harbor.Abstractions.Events;

namespace Harbor.Terminal.Abstractions.ViewModels;

/// <summary>
///     Visitor-style, per-event status updaters for <see cref="StatusBarViewModel"/> (issue #1493).
///     Mirrors the renderer-side <c>IAgentEventHandler</c> registry (#185): the event family is a
///     CLOSED set owned by this assembly, each event group has one updater class, and the dispatch
///     lives here — not in <see cref="StatusBarViewModel.UpdateFromEventAsync"/>. A new lifecycle
///     stage adds an updater plus one registration line in <see cref="Updaters"/>; the view model's
///     event entry point never changes. <c>ITuiPlugin</c> is not an extension axis here (#555).
/// </summary>
/// <remarks>
///     This is a second partial declaration of the view model on purpose: the updaters are nested
///     types, so they can reach the private pricing field directly without widening the public
///     surface with an internal accessor.
/// </remarks>
public sealed partial class StatusBarViewModel
{
    /// <summary>
    ///     One visit over a single <see cref="AgentEvent"/> kind. Stateless singletons live in
    ///     <see cref="Updaters"/>; all state they touch belongs to the view model passed in.
    /// </summary>
    private interface IStatusBarEventUpdater
    {
        /// <summary>Whether this updater owns the given event.</summary>
        bool CanHandle(AgentEvent @event);

        /// <summary>Apply the event to the view model.</summary>
        void Apply(StatusBarViewModel vm, AgentEvent @event);
    }

    /// <summary>
    ///     The closed registry — the only place the event-to-updater mapping is written down.
    ///     Order matters only for overlap; today each event kind has exactly one owner.
    /// </summary>
    private static readonly IStatusBarEventUpdater[] Updaters =
    [
        new AgentStartUpdater(),
        new AgentEndUpdater(),
        new StepFinishUpdater(),
        new ToolExecutionStartUpdater(),
        new ToolExecutionEndUpdater(),
        new SessionStatsUpdater(),
        new AgentErrorUpdater(),
        new CompactionStartedUpdater(),
        new CompactionCompletedUpdater(),
        new CompactionFailedUpdater(),
    ];

    /// <summary>
    ///     Dispatch one event to its updater. Unknown events are ignored, exactly as the old
    ///     switch with no default arm did. Index-based on purpose: this runs on the event hot path.
    /// </summary>
    private static void ApplyUpdater(StatusBarViewModel vm, AgentEvent @event)
    {
        for (int i = 0; i < Updaters.Length; i++)
        {
            IStatusBarEventUpdater updater = Updaters[i];
            if (updater.CanHandle(@event))
            {
                updater.Apply(vm, @event);
                return;
            }
        }
    }

    private sealed class AgentStartUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is AgentStartEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event)
        {
            vm.Status = "running";
            vm.SetModel(((AgentStartEvent)@event).Model);
        }
    }

    private sealed class AgentEndUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is AgentEndEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event)
        {
            vm.Status = "idle";
            vm.QueuedCount = 0;
        }
    }

    private sealed class StepFinishUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) =>
            @event is MessageUpdateEvent { LlmEvent: StepFinishEvent { Usage: not null } };

        public void Apply(StatusBarViewModel vm, AgentEvent @event)
        {
            var step = (StepFinishEvent)((MessageUpdateEvent)@event).LlmEvent;
            var usage = step.Usage!;
            vm.TokensIn += usage.InputTokens;
            vm.TokensOut += usage.OutputTokens;
            // The one event that carries the size of the request just sent —
            // the ctx cell's occupancy reading (#623). SessionStatsEvent is
            // deliberately not a source: its totals are session-cumulative.
            vm.RequestTokens = usage.InputTokens;
            var pricing = vm._pricing;
            if (pricing is not null)
                vm.Cost += pricing.CalculateCost(usage);
        }
    }

    private sealed class ToolExecutionStartUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is ToolExecutionStartEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event) => vm.QueuedCount++;
    }

    private sealed class ToolExecutionEndUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is ToolExecutionEndEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event)
        {
            if (vm.QueuedCount > 0)
                vm.QueuedCount--;
        }
    }

    private sealed class SessionStatsUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is SessionStatsEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event)
        {
            var stats = (SessionStatsEvent)@event;
            // #653: the core owns the number — assign it, never re-derive it.
            // These totals are session-cumulative spend; they feed the
            // cost/token cells, not the ctx cell (#623).
            vm.Cost = stats.Metadata.Cost;
            vm.IsCostKnown = stats.Metadata.IsCostKnown;
            vm.TokensIn = stats.Metadata.TokensInput;
            vm.TokensOut = stats.Metadata.TokensOutput;
        }
    }

    private sealed class AgentErrorUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is AgentErrorEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event) => vm.Status = "error";
    }

    private sealed class CompactionStartedUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is CompactionStartedEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event) => vm.Status = "compacting";
    }

    private sealed class CompactionCompletedUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is CompactionCompletedEvent;

        public void Apply(StatusBarViewModel vm, AgentEvent @event) => vm.Status = "running";
    }

    private sealed class CompactionFailedUpdater : IStatusBarEventUpdater
    {
        public bool CanHandle(AgentEvent @event) => @event is CompactionFailedEvent;

        // #773: the third arm of the lifecycle. This VM has no MessageStartEvent
        // arm, so unlike the store projection nothing would have cleared a stale
        // "compacting" — the cell would have read "compacting" from the failure
        // until the AgentEndEvent updater above reset it to "idle". The run
        // continues on truncated history, so "running" is the truth, and the
        // degradation itself is narrated by the renderers that have a transcript.
        //
        // Not "error", for two reasons: the turn is not failing, and this VM's
        // AgentEndEvent updater resets to "idle" unconditionally — so an "error"
        // would not even survive to the end of the run, and would disagree with
        // the store projection, which deliberately preserves it (#687).
        public void Apply(StatusBarViewModel vm, AgentEvent @event) => vm.Status = "running";
    }
}
