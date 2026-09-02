namespace Harbor.Ui.Framework.Rendering.Protocol;

using System.Collections.Immutable;

/// <summary>
///     Produces portable <see cref="CellDiffBatch"/>es from consecutive frame
///     buffers (renderer-unification sprint Phase 6.2).
/// </summary>
/// <remarks>
///     Implementations live in backend adapter assemblies (e.g.
///     <c>Harbor.Tui.CellForge.Rendering.CellForgeDiffEncoder</c>) so the
///     backend's own optimizations remain untouched behind the adapter.
/// </remarks>
public interface ICellDiffEncoder
{
    /// <summary>
    ///     Encodes the delta between <paramref name="prev"/> (previous frame)
    ///     and <paramref name="next"/> (current frame). Both buffers must have
    ///     equal dimensions. <paramref name="hints"/> may be null or empty —
    ///     the encoder then emits a V1-equivalent batch (full-scan, no hints).
    /// </summary>
    CellDiffBatch Encode(
        ScreenBuffer prev,
        ScreenBuffer next,
        IReadOnlyList<Rect>? hints,
        long sequence);
}

/// <summary>
///     Applies a <see cref="CellDiffBatch"/> to a render surface. Decoders
///     must accept batches of any version &lt;= their declared max version —
///     this is the protocol's backward-compatibility contract.
/// </summary>
public interface ICellDiffDecoder
{
    /// <summary>Highest protocol version this decoder understands.</summary>
    CellDiffProtocolVersion MaxVersion { get; }

    /// <summary>Applies every change of <paramref name="batch"/> to the surface.</summary>
    void Apply(in CellDiffBatch batch);
}

/// <summary>
///     Receive end of the differential render pipeline: backends subscribe as
///     sinks and translate batches into their own output primitives.
/// </summary>
public interface ICellDiffSink
{
    void Accept(in CellDiffBatch batch);
}
