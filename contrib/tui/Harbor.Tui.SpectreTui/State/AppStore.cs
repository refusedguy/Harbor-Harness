namespace Harbor.Tui.SpectreTui.State;

using Harbor.Abstractions.Events;
using Harbor.Ui.Framework.State;

/// <summary>
///     Adapter that bridges the legacy <see cref="UiStore" /> / <see cref="UiState" />
///     to the new <see cref="AppState" />-based architecture. Spectre TUI continues to
///     use <see cref="UiStore" /> internally while this adapter presents an
///     <see cref="AppState" /> view for external consumers.
/// </summary>
/// <remarks>
///     <para>
///         During the incremental migration from <see cref="UiState" /> to
///         <see cref="AppState" />, this adapter performs bidirectional field mapping.
///         The overlapping fields are copied directly; <see cref="AppState" />-only
///         fields receive default values on the forward pass and are discarded on the
///         reverse pass.
///     </para>
///     <para>
///         Once the migration is complete and <see cref="UiStore" /> is removed,
///         this adapter can be replaced with direct <see cref="AppState" /> usage.
///     </para>
/// </remarks>
public sealed class AppStore
{
    private readonly UiStore _uiStore;

    /// <summary>Current application state, adapted from the underlying <see cref="UiStore" />.</summary>
    public AppState CurrentState => AdaptForward(_uiStore.State);

    /// <summary>Construct an adapter around an existing <see cref="UiStore" />.</summary>
    public AppStore(UiStore uiStore)
    {
        _uiStore = uiStore;
    }

    /// <summary>Dispatch an agent event through the underlying store.</summary>
    public void Dispatch(AgentEvent @event)
    {
        _uiStore.Dispatch(@event);
    }

    /// <summary>Map <see cref="UiState" /> → <see cref="AppState" />.</summary>
    private static AppState AdaptForward(UiState uiState)
    {
        return new AppState
        {
            Lines = uiState.Lines,
            Active = uiState.Active,
            IsStreaming = uiState.IsStreaming,
            IsThinking = false,
            Status = uiState.Status,
            Cost = uiState.Cost,
            Model = uiState.Model,
            Provider = uiState.Provider,
            AgentName = uiState.AgentName,
            IsAgentRunning = uiState.IsAgentRunning,
            WasRunning = uiState.WasRunning,
            ShouldQuit = uiState.ShouldQuit,
            Input = uiState.Input,
            Focus = uiState.Focus,
            ScrollOffset = uiState.ScrollOffset,
            ViewportLines = uiState.ViewportLines,
            TotalLines = uiState.TotalLines,
            PanelStates = uiState.PanelStates,
            PanelSizes = uiState.PanelSizes,
            FocusedPanelId = uiState.FocusedPanelId,
            RegisteredPanelIds = uiState.RegisteredPanelIds,
            ActiveDrawerTab = "None",
            StreamingBuffer = string.Empty,
            ThinkingBuffer = string.Empty,
            Chrome = null
        };
    }
}

