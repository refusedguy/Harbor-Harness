using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Generic;

/// <summary>
/// One named gallery entry (epic #1195, part of #1228): the golden name plus
/// the fixed geometry the golden pins plus the builder. Scripted entries
/// (typed/driven/focus) build the SEED screen — the test drains a
/// <c>VirtualInputSource</c> into it before painting.
/// </summary>
/// <param name="Name">Golden file name (tests/fixtures/generic-layout).</param>
/// <param name="Cols">Golden viewport width.</param>
/// <param name="Rows">Golden viewport height.</param>
/// <param name="Build">Seed screen factory.</param>
public sealed record GenericGalleryScreen(string Name, int Cols, int Rows, Func<GenericScreen> Build);

/// <summary>
/// Layout gallery (epic #1195, part of #1228): ten neutral compositions over
/// <see cref="GenericScreen.Build"/> proving generic screens compose beyond
/// the chat chrome — static panes, splits on both axes, fields (empty,
/// filled, scripted-typed) and selection lists (seeded, scripted-driven),
/// plus a focus-cycle seed. Every entry is built from in-tree specs only,
/// so this catalog adds no <c>LayoutTree</c> construction site (see
/// <c>AllowedTreeBuilders</c>) and judges no runes (no key-gate family).
/// </summary>
public static class GenericGallery
{
    /// <summary>All ten entries in golden order.</summary>
    public static IReadOnlyList<GenericGalleryScreen> All() =>
    [
        new("single-static", 36, 7, SingleStatic),
        new("two-pane-stack", 36, 10, TwoPaneStack),
        new("two-pane-side", 36, 8, TwoPaneSide),
        new("field-empty", 32, 6, FieldEmpty),
        new("field-filled", 32, 6, FieldFilled),
        new("field-typed", 32, 6, FieldEmpty),
        new("list-basic", 30, 9, ListBasic),
        new("list-driven", 30, 9, ListSeed),
        new("focus-cycle", 36, 10, TwoPaneStack),
        new("three-pane", 40, 12, ThreePane),
    ];

    /// <summary>Single static pane (title plus two lines).</summary>
    public static GenericScreen SingleStatic() => GenericScreen.Build([
        new GenericNodeSpec { Id = "home", Title = "Welcome", Lines = ["Pick a layout below.", "Tab moves focus."] },
    ]);

    /// <summary>Two static panes stacked (vertical split, even).</summary>
    public static GenericScreen TwoPaneStack() => GenericScreen.Build(
        [
            new GenericNodeSpec { Id = "top", Title = "Top", Lines = ["upper pane"] },
            new GenericNodeSpec { Id = "bottom", Title = "Bottom", Lines = ["lower pane"] },
        ],
        [
            new GenericSplitSpec { TargetId = "top", Direction = SplitDir.Vertical, Ratio = 0.5f, NewPanelId = "bottom", Gap = 0 },
        ]);

    /// <summary>Two static panes side by side (horizontal split, even, one gap column).</summary>
    public static GenericScreen TwoPaneSide() => GenericScreen.Build(
        [
            new GenericNodeSpec { Id = "left", Title = "Left", Lines = ["west"] },
            new GenericNodeSpec { Id = "right", Title = "Right", Lines = ["east"] },
        ],
        [
            new GenericSplitSpec { TargetId = "left", Direction = SplitDir.Horizontal, Ratio = 0.5f, NewPanelId = "right", Gap = 1 },
        ]);

    /// <summary>Login form with an empty placeholder field.</summary>
    public static GenericScreen FieldEmpty() => GenericScreen.Build([
        new GenericNodeSpec
        {
            Id = "login",
            Title = "Login",
            Field = new GenericTextField { Placeholder = "login..." },
        },
    ]);

    /// <summary>Login form with a pre-filled field.</summary>
    public static GenericScreen FieldFilled()
    {
        var field = new GenericTextField { Placeholder = "login..." };
        field.SetText("ada");
        return GenericScreen.Build([
            new GenericNodeSpec { Id = "login", Title = "Login", Field = field },
        ]);
    }

    /// <summary>Option list with the middle row selected.</summary>
    public static GenericScreen ListBasic()
    {
        var list = new GenericSelectionList();
        list.SetItems(["alpha", "beta", "gamma"]);
        list.MoveTo(1);
        return GenericScreen.Build([
            new GenericNodeSpec { Id = "pick", Title = "Pick one", Field = list },
        ]);
    }

    /// <summary>Option list seed for scripted driving (selection starts at the top).</summary>
    public static GenericScreen ListSeed()
    {
        var list = new GenericSelectionList();
        list.SetItems(["alpha", "beta", "gamma"]);
        return GenericScreen.Build([
            new GenericNodeSpec { Id = "pick", Title = "Pick one", Field = list },
        ]);
    }

    /// <summary>Three static panes stacked (default splits, no explicit specs).</summary>
    public static GenericScreen ThreePane() => GenericScreen.Build([
        new GenericNodeSpec { Id = "a", Title = "One", Lines = ["first"] },
        new GenericNodeSpec { Id = "b", Title = "Two", Lines = ["second"] },
        new GenericNodeSpec { Id = "c", Title = "Three", Lines = ["third"] },
    ]);
}
