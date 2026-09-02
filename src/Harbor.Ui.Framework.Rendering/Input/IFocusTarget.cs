namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
/// Focusable target contract (moved out of CellForge's FocusRouter so
/// renderer-agnostic widgets like <see cref="Widgets.ApprovalGateView"/> can
/// participate in focus traversal without referencing a concrete backend).
/// </summary>
public interface IFocusTarget
{
    string Id { get; }

    /// <summary>Called when the target gains or loses focus.</summary>
    void OnFocusChanged(bool focused);
}
