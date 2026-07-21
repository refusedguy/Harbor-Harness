using CommunityToolkit.Mvvm.ComponentModel;
namespace Harbor.Ui.Framework.State;
/// <summary>
///     Shell chrome state — layout dimensions and mode toggles shared across
///     all shell-based renderers (Avalonia, WPF, Blazor, SpectreTui).
/// </summary>
/// <remarks>
///     <para>
///         <b>TEA boundary respected:</b> this state lives in the renderer
///         layer. It is NOT in the shared <c>UiStore</c> / domain TEA model.
///         The shell-mode switch (rail width, right panel, active mode) is
///         pure view chrome — the underlying chat/session/agent state is
///         unchanged regardless of which shell the user picks.
///     </para>
///     <para>
///         Fields here are observable so renderers can bind directly
///         (e.g. <c>Width="{Binding ShellState.LeftRailWidth}"</c>).
///     </para>
/// </remarks>
public sealed partial class ShellState : ObservableObject
{

    /// <summary>Active main mode: Chat | Code.</summary>
    [ObservableProperty]
    private string _activeMode = "Chat";

    /// <summary>True when the user opts into the compact density.</summary>
    [ObservableProperty]
    private bool _isCompactDensity;

    /// <summary>True when the user collapsed the left rail.</summary>
    [ObservableProperty]
    private bool _leftRailCollapsed;

    /// <summary>Current left-rail width in px.</summary>
    [ObservableProperty]
    private double _leftRailWidth = 280;

    /// <summary>Active right-panel tab: None | Files | Diff | Usage.</summary>
    [ObservableProperty]
    private string _rightPanel = "None";

    /// <summary>Right-panel width in px.</summary>
    [ObservableProperty]
    private double _rightPanelWidth = 300;

    /// <summary>Free-text filter applied to the session list.</summary>
    [ObservableProperty]
    private string _sessionListFilter = string.Empty;
}
