// #2360 (epic #1195): measured-layout input vocabulary — the engine-owned
// port of GPUI/taffy's AvailableSpace (crates/gpui/src/taffy.rs:689, commit
// 698368515d17a602dd51ea61297e5412ce06f52f). A measured leaf is a pure
// function AvailableSpace -> Size; the space is either a definite cell box or
// a semantic min-content / max-content request.
// Same leaf rule as every file in this directory: BCL-only, AOT-compatible,
// zero Harbor references, render-thread owned.
//
// BOUNDARY WITH #1221: this type is data only. It is not a solver and it does
// not extend the SizeConstraint dictionary (Fixed/Fill/Auto/Percent/Ratio) or
// Fill weights. MeasuredLeaf.ToTrack converts the two semantic extents into a
// plain FlexTrack.Auto(min, max); FlexLayout/FlexTrack remain the single owner
// of intrinsic sizing and distribution. See MeasuredLeaf.cs.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>Which kind of space a measured leaf is being asked to size for.</summary>
public enum AvailableSpaceKind : byte
{
    /// <summary>A definite box: <see cref="AvailableSpace.Width"/> /
    /// <see cref="AvailableSpace.Height"/> are hard upper bounds.</summary>
    Definite = 0,

    /// <summary>Smallest size the content fits without overflow: the longest
    /// unbreakable word for text, the widest column cell for a table.</summary>
    MinContent = 1,

    /// <summary>Size with no wrapping at all: the whole line for text, the sum
    /// of full column widths for a table.</summary>
    MaxContent = 2,
}

/// <summary>
/// Space offered to a measured leaf (GPUI <c>AvailableSpace</c> analogue). One
/// value carries both axes so it pairs with <see cref="INode.Measure"/>'s
/// <see cref="Constraints"/> in / <see cref="Size"/> out shape. Definite extents
/// clamp at zero; semantic requests carry zero extents — a closure MUST branch
/// on <see cref="Kind"/> (or <see cref="IsMinContent"/> /
/// <see cref="IsMaxContent"/>) and ignore Width/Height for those.
/// </summary>
public readonly record struct AvailableSpace
{
    private AvailableSpace(AvailableSpaceKind kind, int width, int height)
    {
        Kind = kind;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
    }

    /// <summary>The space kind.</summary>
    public AvailableSpaceKind Kind { get; }

    /// <summary>Definite width bound; zero for semantic requests.</summary>
    public int Width { get; }

    /// <summary>Definite height bound; zero for semantic requests.</summary>
    public int Height { get; }

    /// <summary>True for a definite cell box.</summary>
    public bool IsDefinite => Kind == AvailableSpaceKind.Definite;

    /// <summary>True for a min-content proposal.</summary>
    public bool IsMinContent => Kind == AvailableSpaceKind.MinContent;

    /// <summary>True for a max-content proposal.</summary>
    public bool IsMaxContent => Kind == AvailableSpaceKind.MaxContent;

    /// <summary>A definite cell box; negative extents count as zero.</summary>
    public static AvailableSpace Definite(int width, int height) =>
        new(AvailableSpaceKind.Definite, width, height);

    /// <summary>The content's minimum intrinsic size.</summary>
    public static AvailableSpace MinContent => new(AvailableSpaceKind.MinContent, 0, 0);

    /// <summary>The content's maximum intrinsic size.</summary>
    public static AvailableSpace MaxContent => new(AvailableSpaceKind.MaxContent, 0, 0);
}
