// #2361: the value produced by the prepaint phase and consumed by Paint.
// Keeps the four things GPUI carries in a reused prepaint entry (bounds,
// style, accessed entities, display-list range) in one BCL-only value type.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Everything a node prepared between arrange and paint: the final bounds,
/// the resolved style, the entities the prepaint read, and the range of
/// <see cref="PrepaintContext.Scene"/> holding its ready-made paint operations.
/// A record is reusable across frames as long as the node keeps the same
/// <see cref="ElementId"/> and the same prepaint key; <see cref="INode.Paint"/>
/// only replays <see cref="SceneRange"/> and never prepares data again.
/// </summary>
public readonly record struct PrepaintRecord(
    Rect Bounds,
    CellStyle Style,
    SceneRange SceneRange,
    IReadOnlyList<string>? ReadEntities)
{
    /// <summary>Neutral value returned by nodes that do not participate in prepaint.</summary>
    public static PrepaintRecord Empty { get; } = new(default, default, default, null);
}
