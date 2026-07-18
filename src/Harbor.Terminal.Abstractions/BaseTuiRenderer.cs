using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging;
namespace Harbor.Terminal.Abstractions;
/// <summary>
///     Base TUI renderer implementing the common view-dispatch logic.
/// </summary>
/// <remarks>
///     <para>
///         Concrete renderers (<c>AnsiTuiRenderer</c>, <c>PlainTuiRenderer</c>,
///         <c>SpectreTuiRenderer</c>) inherit this class and only need to provide an
///         <see cref="ITuiRenderContext" />. The base class:
///     </para>
///     <list type="bullet">
///         <item>
///             Registers four builtin views (status bar, chat history, input, diff preview) on
///             <see cref="InitializeAsync" /> — unless a plugin has already registered a view with the
///             same id (override-before-builtin).
///         </item>
///         <item>
///             Auto-binds each view to its view model by matching
///             <see cref="ITuiView.Id" /> against <see cref="ITuiViewModel.Id" />.
///         </item>
///         <item>
///             On every <see cref="RenderAsync" />, fans the <see cref="AgentEvent" /> out to all
///             view models (<see cref="ITuiViewModel.UpdateFromEventAsync" />) and all views
///             (<see cref="ITuiView.OnEventAsync" />), then renders placements that are relevant to
///             the event type.
///         </item>
///     </list>
///     <para>
///         <b>Decoupling contract:</b> neither this class nor its subclasses may reference
///         <c>Harbor.Core</c>. All agent state flows in through <see cref="AgentEvent" /> from
///         <c>Harbor.Abstractions.Events</c>. There are no direct references to
///         <c>AgentLoop</c>, <c>SessionStore</c>, or <c>ProviderRegistry</c>.
///     </para>
/// </remarks>
public abstract class BaseTuiRenderer : ITuiRenderer
{
    protected readonly ILogger Logger;

    /// <summary>
    ///     Construct a <see cref="BaseTuiRenderer" /> with the supplied logger.
    /// </summary>
    /// <param name="logger">The logger.</param>
    protected BaseTuiRenderer(ILogger logger)
    {
        Logger = logger;
        Views = new ViewRegistry();
        ViewModels = new ViewModelRegistry();

        // Register default view models — these live for the lifetime of the renderer and
        // are the canonical state holders for the builtin views.
        ViewModels.Register(new StatusBarViewModel());
        ViewModels.Register(new ChatHistoryViewModel());
        ViewModels.Register(new InputViewModel());
        ViewModels.Register(new DiffPreviewViewModel());
    }
    public ViewRegistry Views { get; }
    public ViewModelRegistry ViewModels { get; }
    public abstract ITuiRenderContext Context { get; }

    /// <summary>
    ///     Registers the four builtin views (unless already overridden by a plugin) and binds
    ///     each view to its view model. Subclasses that override this MUST call
    ///     <see cref="RegisterBuiltinViews" />, <see cref="BindViewModelsToViews" />, and freeze
    ///     the registry, or simply call <c>base.InitializeAsync</c>.
    /// </summary>
    public virtual Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        RegisterBuiltinViews();
        BindViewModelsToViews();
        Views.Freeze();
        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Fans the event out to all view models and views, then renders placements that are
    ///     relevant to the event type.
    /// </summary>
    public virtual async Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Update all view models from event — this is the single subscription point that
        // keeps VM state in sync with agent activity. Views never reach into Core.
        foreach (var vm in ViewModels.GetAll())
        {
            try
            {
                await vm.UpdateFromEventAsync(@event, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ViewModel {Id} update failed", vm.Id);
            }
        }

        // Give every view a chance to react to the event (e.g. invalidate caches, update
        // spinners) before placement-driven rendering.
        foreach (var view in Views.GetAll())
        {
            try
            {
                await view.OnEventAsync(@event, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "View {Id} event handler failed", view.Id);
            }
        }

        // Render placement-specific views. Each placement is only rendered on events that
        // actually change its visible state — this keeps streaming renderers (Ansi, Plain)
        // from spamming the status bar after every token delta.
        if (ShouldRenderPlacement(TuiViewPlacement.StatusBar, @event))
        {
            await RenderPlacementAsync(TuiViewPlacement.StatusBar, @event, ct).ConfigureAwait(false);
        }

        if (ShouldRenderPlacement(TuiViewPlacement.ChatHistory, @event))
        {
            await RenderPlacementAsync(TuiViewPlacement.ChatHistory, @event, ct).ConfigureAwait(false);
        }

        if (ShouldRenderPlacement(TuiViewPlacement.Input, @event))
        {
            await RenderPlacementAsync(TuiViewPlacement.Input, @event, ct).ConfigureAwait(false);
        }

        if (ShouldRenderPlacement(TuiViewPlacement.Overlay, @event))
        {
            await RenderPlacementAsync(TuiViewPlacement.Overlay, @event, ct).ConfigureAwait(false);
        }
    }

    public abstract Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default);
    public abstract Task<Result> WriteAsync(string text, CancellationToken ct = default);
    public abstract Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default);
    public abstract Task<Result> ClearAsync(CancellationToken ct = default);

    public virtual void Dispose() { }

    /// <summary>
    ///     Registers the builtin views. Plugins that registered a view with the same id before
    ///     <see cref="InitializeAsync" /> is called take precedence (override-before-builtin).
    /// </summary>
    protected void RegisterBuiltinViews()
    {
        RegisterBuiltinViewIfMissing(new StatusBarView());
        RegisterBuiltinViewIfMissing(new ChatHistoryView());
        RegisterBuiltinViewIfMissing(new InputView());
        RegisterBuiltinViewIfMissing(new DiffPreviewView());
    }

    private void RegisterBuiltinViewIfMissing(ITuiView view)
    {
        if (Views.Get(view.Id) is null)
        {
            Views.Register(view);
        }
    }

    /// <summary>
    ///     Binds each registered view to its view model by matching
    ///     <see cref="ITuiView.Id" /> against <see cref="ITuiViewModel.Id" />. Views that already
    ///     have a ViewModel assigned (e.g. by a plugin) are skipped.
    /// </summary>
    protected void BindViewModelsToViews()
    {
        foreach (var view in Views.GetAll())
        {
            if (view.ViewModel is not null)
            {
                continue;
            }
            var vm = ViewModels.Get(view.Id);
            if (vm is not null)
            {
                view.ViewModel = vm;
            }
        }
    }

    /// <summary>
    ///     Decides whether a given placement should be repainted for this event. Subclasses
    ///     can override to suppress specific placements (e.g. a streaming renderer that emits
    ///     tokens directly may want to skip <see cref="TuiViewPlacement.ChatHistory" /> on
    ///     <see cref="MessageUpdateEvent" />).
    /// </summary>
    protected virtual bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
    {
        return placement switch
        {
            // Status bar changes on state transitions and on step finish (token counts).
            TuiViewPlacement.StatusBar => @event is AgentStartEvent
                or AgentEndEvent
                or AgentErrorEvent
                or CompactionStartedEvent
                or CompactionCompletedEvent
                or MessageUpdateEvent { LlmEvent: StepFinishEvent },

            // Chat history grows when a new entry is appended: user messages on agent
            // start, the final assistant message on message end, tool results on tool end.
            TuiViewPlacement.ChatHistory => @event is AgentStartEvent
                or MessageEndEvent
                or ToolExecutionEndEvent,

            // Input prompt is (re)shown when the agent goes idle and is waiting for input.
            TuiViewPlacement.Input => @event is AgentStartEvent or AgentEndEvent,

            // Diff overlay only appears when a tool execution may have produced a file
            // change (the view itself no-ops if no diffs are recorded).
            TuiViewPlacement.Overlay => @event is ToolExecutionEndEvent,

            _ => false
        };
    }

    protected async Task RenderPlacementAsync(TuiViewPlacement placement, AgentEvent @event, CancellationToken ct = default)
    {
        var views = Views.GetByPlacement(placement);
        if (views.Count == 0) return;

        foreach (var view in views)
        {
            try
            {
                await view.RenderAsync(Context, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "View {Id} render failed", view.Id);
            }
        }

        Context.Flush();
    }
}
