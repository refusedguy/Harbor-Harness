namespace Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     Version of the portable cell-diff protocol (renderer-unification
///     sprint Phase 6.2).
/// </summary>
/// <remarks>
///     <para>
///         <b>V1</b> — baseline: a batch carries only the changed-cell list.
///         <b>V2</b> — adds <c>FrameHint</c> damage rects, so receiving
///         backends can drive their own hint-based repaint machinery (e.g.
///         SpectreTui's partial LiveUpdate, WPF/Avalonia incremental
///         invalidation) instead of re-scanning the whole frame.
///     </para>
///     <para>
///         Decoders must accept every version &lt;= their own max supported
///         version (backward compatibility is enforced by
///         <see cref="CellDiffBatchCodec"/> tests).
///     </para>
/// </remarks>
public enum CellDiffProtocolVersion : byte
{
    /// <summary>Baseline — changed cells only.</summary>
    V1 = 1,

    /// <summary>Baseline + FrameHint damage rects.</summary>
    V2 = 2,

    /// <summary>Latest version this assembly emits.</summary>
    Latest = V2,
}
