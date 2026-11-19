// #3086 (S5): the single public stateful contract for engine primitives.
// A stateful node owns no map: it exposes a stable identity, copies its mutable
// state into a plain value snapshot on capture and back on restore. The one
// place that keeps snapshots between frames is ElementStateStore (#2436), which
// S3 (#3084) wires into the product path. Adding a dictionary here (or inside a
// widget) would split ownership of the same state, so this contract stops at
// capture/restore. BCL-only, no reflection, no AOT-hostile shapes.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Identity half of the stateful contract. A node at the same
/// <see cref="ElementId"/> in two consecutive frames reports the same value,
/// which lets the host keep one snapshot per node instead of inventing a
/// private cache in each widget.
/// </summary>
/// <remarks>
/// The node does not store the snapshot. <c>ElementStateStore</c> is the single
/// store for snapshots; this interface only supplies the key.
/// </remarks>
public interface IStatefulNode
{
    /// <summary>Stable key under which the host keeps this node's snapshot.</summary>
    ElementId StateId { get; }
}

/// <summary>
/// A stateful node with a typed, serializable snapshot.
/// </summary>
/// <typeparam name="TState">
/// A plain value snapshot, typically a <c>readonly record struct</c> made of
/// BCL primitives. Copying it uses no reflection, so the contract is safe under
/// NativeAOT and a host can serialize it with its own source-generated context.
/// </typeparam>
public interface IStatefulNode<TState> : IStatefulNode where TState : struct
{
    /// <summary>Copies the current interaction state into a value snapshot.</summary>
    TState CaptureState();

    /// <summary>Applies a snapshot taken on an earlier frame.</summary>
    void RestoreState(TState state);
}
