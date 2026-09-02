namespace Harbor.Ui.Framework.Rendering.Protocol;

using System.Collections.Immutable;

/// <summary>
///     An immutable, sequence-numbered frame delta produced by a cell-diff
///     encoder (renderer-unification sprint Phase 6.2). Consumers apply the
///     changes to their own render surface — DOM, remote terminal, native
///     widgets — without ever seeing the producing backend's internals.
/// </summary>
/// <param name="Version">Protocol version the batch was encoded with.</param>
/// <param name="Sequence">
///     Monotonic frame number; consumers detect lost/duplicated batches by
///     gaps in the sequence.
/// </param>
/// <param name="Cols">Frame width the batch was produced against.</param>
/// <param name="Rows">Frame height the batch was produced against.</param>
/// <param name="Changes">Changed cells, row-major order, no duplicates.</param>
/// <param name="FrameHints">
///     Damage rectangles the producer used (V2+); empty for V1 batches and
///     whenever the producer did not narrow the scan.
/// </param>
public sealed record CellDiffBatch(
    CellDiffProtocolVersion Version,
    long Sequence,
    int Cols,
    int Rows,
    ImmutableArray<CellDiffMessage> Changes,
    ImmutableArray<Rect> FrameHints)
{
    /// <summary>The shared empty batch (no changes, no hints).</summary>
    public static CellDiffBatch Empty { get; } = new(
        CellDiffProtocolVersion.Latest,
        0,
        0,
        0,
        ImmutableArray<CellDiffMessage>.Empty,
        ImmutableArray<Rect>.Empty);

    public bool IsEmpty => Changes.IsEmpty;
}
