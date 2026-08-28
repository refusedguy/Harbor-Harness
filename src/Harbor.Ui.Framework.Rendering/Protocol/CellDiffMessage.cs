namespace Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     One changed cell on the portable cell-diff wire (renderer-unification
///     sprint Phase 6.2). Immutable value — safe to hand across backend and
///     transport boundaries (SignalR to a Blazor DOM, UDS between processes,
///     Avalonia incremental invalidation, ...).
/// </summary>
/// <param name="X">Column of the cell in the frame.</param>
/// <param name="Y">Row of the cell in the frame.</param>
/// <param name="OldCell">Previous content (front buffer), for backends that diff downstream.</param>
/// <param name="NewCell">New content (back buffer) to apply.</param>
public readonly record struct CellDiffMessage(int X, int Y, Cell OldCell, Cell NewCell)
{
    /// <summary>Binary payload size of one message in the codec (see <see cref="CellDiffBatchCodec"/>).</summary>
    public const int EncodedSize = sizeof(int) /* X */
        + sizeof(int) /* Y */
        + (sizeof(int) + sizeof(uint) + sizeof(uint) + sizeof(ushort) + sizeof(byte)) /* OldCell */
        + (sizeof(int) + sizeof(uint) + sizeof(uint) + sizeof(ushort) + sizeof(byte)); /* NewCell */
}
