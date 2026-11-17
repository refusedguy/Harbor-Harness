namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     The damage source half of the frame damage-tracking contract (#2220).
///     A widget's <see cref="DirtyRect"/> and any other invalidation source
///     expose the same two facts: where the damage is and how large it is.
///     <see cref="DiffEngine.FrameHint(IDamageRegion, int, int)"/> consumes
///     this contract, so damage no longer needs a second, unrelated path into
///     the diff (the historical FrameHint/OverlayStack split).
/// </summary>
/// <remarks>
///     Coordinates are source-local. The caller translates them into screen
///     coordinates when the source lives inside a viewport; clipping to the
///     screen is the hint ledger's job, not the source's.
/// </remarks>
public interface IDamageRegion
{
    /// <summary>Dirty region in source-local coordinates, or null when clean.</summary>
    Rect? Dirty { get; }

    /// <summary>Dirty cell count (zero when <see cref="Dirty"/> is null).</summary>
    long DirtyArea { get; }
}
