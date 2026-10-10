// #436: engine-owned port of src/Harbor.Ui.Framework.Rendering/Input/IFocusTarget.cs — verbatim except its namespace.
// The engine is a standalone leaf (zero Harbor references); this vocabulary lives here now.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Focusable target contract: renderer-agnostic widgets participate in focus
/// traversal without referencing a concrete backend.
/// </summary>
public interface IFocusTarget
{
    string Id { get; }

    /// <summary>Called when the target gains or loses focus.</summary>
    void OnFocusChanged(bool focused);
}
