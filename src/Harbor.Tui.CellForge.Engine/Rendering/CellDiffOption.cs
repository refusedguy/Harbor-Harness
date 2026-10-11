// #436: engine-owned port of src/Harbor.Ui.Framework.Rendering/CellDiffOption.cs — verbatim except its namespace.
// The engine is a standalone leaf (zero Harbor references); this vocabulary lives here now.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Per-cell diff directive (R1 steal, epic #1155: ratatui
/// <c>buffer/cell.rs CellDiffOption</c> port — None/Skip/AlwaysUpdate/ForcedWidth).
/// Stored in <see cref="ScreenBuffer"/>'s side table, NOT in <see cref="Cell"/>:
/// the 16-byte cell layout and its five-compare equality stay untouched, so the
/// fused scan, the row hashes (which fold the option separately) and the codec
/// round-trip keep their shape. Row-hash fast path stays sound: the hash folds
/// the option, so an option-only change still breaks hash equality.
/// </summary>
public enum CellDiffOption : byte
{
    /// <summary>Classic behavior: yield when different, skip when equal.</summary>
    None = 0,

    /// <summary>
    /// Painted out-of-band (image protocols, foreign overlays): never yielded,
    /// mirrored into FRONT silently. The engine assumes the terminal already
    /// shows the BACK content — marking a cell Skip that nothing painted is a
    /// stuck-cell bug by contract, not a rendering one. Portable batches omit
    /// these cells as well: every consumer paints them out-of-band.
    /// </summary>
    Skip = 1,

    /// <summary>Yielded even when FRONT already equals BACK (remote mirrors, repaints).</summary>
    AlwaysUpdate = 2,

    /// <summary>
    /// Explicit cursor advance from <see cref="ScreenBuffer.GetForcedWidth"/>:
    /// yielded when different, then the scan skips <c>width - 1</c> reserved
    /// columns (mirrored silently). For cells whose symbol measures wider than
    /// the grid (image-protocol payloads — ratatui #2685 class).
    /// </summary>
    ForcedWidth = 3,
}
