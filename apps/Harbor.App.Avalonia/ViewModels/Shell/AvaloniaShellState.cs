using CommunityToolkit.Mvvm.ComponentModel;
namespace Harbor.App.Avalonia.ViewModels.Shell;
/// <summary>
///     Local shell state for the experimental Orca shell — layout/chrome only.
/// </summary>
/// <remarks>
///     <para>
///         <b>TEA boundary respected:</b> this state lives entirely in the
///         Avalonia app layer. It is NOT in the shared <c>UiStore</c> /
///         <c>Harbor.Domain</c> TEA model. The shell-mode switch (rail width,
///         right panel, active mode, list filter) is pure view chrome — the
///         underlying chat/session/agent state is unchanged whether the user
///         runs the classic or Orca shell.
///     </para>
///     <para>
///         Fields here are observable so the Orca shell's XAML can bind
///         directly (e.g. <c>Width="{Binding ShellState.LeftRailWidth}"</c>).
///     </para>
/// </remarks>
public sealed partial class AvaloniaShellState : ObservableObject
{

    /// <summary>Active main mode: Chat | Code.</summary>
    [ObservableProperty]
    private string _activeMode = "Chat";

    /// <summary>True when the user opts into the compact density (Phase B toggle).</summary>
    [ObservableProperty]
    private bool _isCompactDensity;

    /// <summary>True when the user collapsed the left rail (Ctrl+B).</summary>
    [ObservableProperty]
    private bool _leftRailCollapsed;
    /// <summary>Current left-rail width in px (resizable in Phase D).</summary>
    [ObservableProperty]
    private double _leftRailWidth = 280;

    /// <summary>Active right-panel tab: None | Files | Diff | Usage.</summary>
    [ObservableProperty]
    private string _rightPanel = "None";

    /// <summary>Right-panel width in px (resizable in Phase D).</summary>
    [ObservableProperty]
    private double _rightPanelWidth = 300;

    /// <summary>Free-text filter applied to the session list.</summary>
    [ObservableProperty]
    private string _sessionListFilter = string.Empty;
}
