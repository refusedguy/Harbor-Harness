using Harbor.Ui.Framework.Rendering;

namespace Harbor.DesignSystem.Tests;

/// <summary>
/// Style priority contract (issue #2349): ordinary → semantic states →
/// disabled. These are pure struct operations — no palette state, safe to
/// parallelize. Every assertion pins one rung of the fixed priority order.
/// </summary>
public class StylePriorityTests
{
    // Distinct, easily-identifiable styles. Indexed colors avoid truecolor and
    // keep the values stable across themes.
    private static readonly CellStyle Ordinary = new(PackedColor.Indexed(1));
    private static readonly CellStyle Hovered = new(PackedColor.Indexed(2));
    private static readonly CellStyle Active = new(PackedColor.Indexed(3));
    private static readonly CellStyle Selected = new(PackedColor.Indexed(4));
    private static readonly CellStyle Focused = new(PackedColor.Indexed(5));
    private static readonly CellStyle Disabled = new(PackedColor.Indexed(6));

    private static StyleCatalog FullCatalog() => new(
        Ordinary,
        Hovered: Hovered,
        Active: Active,
        Selected: Selected,
        Focused: Focused,
        Disabled: Disabled);

    [Test]
    public async Task NoStateNotDisabled_ReturnsOrdinary()
    {
        var resolved = StylePriority.Resolve(FullCatalog(), VisualState.None, disabled: false);

        await Assert.That(resolved).IsEqualTo(Ordinary);
    }

    [Test]
    public async Task EachSemanticState_OverridesOrdinary()
    {
        var catalog = FullCatalog();

        await Assert.That(StylePriority.Resolve(catalog, VisualState.Hovered, false)).IsEqualTo(Hovered);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Active, false)).IsEqualTo(Active);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Selected, false)).IsEqualTo(Selected);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Focused, false)).IsEqualTo(Focused);
    }

    [Test]
    public async Task LaterStateWins_OverEarlierState()
    {
        var catalog = FullCatalog();

        // Fixed order: Hovered < Active < Selected < Focused.
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Hovered | VisualState.Active, false)).IsEqualTo(Active);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Active | VisualState.Selected, false)).IsEqualTo(Selected);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Selected | VisualState.Focused, false)).IsEqualTo(Focused);

        // All four active: the highest-priority semantic state wins.
        VisualState all = VisualState.Hovered | VisualState.Active | VisualState.Selected | VisualState.Focused;
        await Assert.That(StylePriority.Resolve(catalog, all, false)).IsEqualTo(Focused);
    }

    [Test]
    public async Task StateWithNullCatalogSlot_DoesNotOverride()
    {
        // Catalog supplies only Ordinary + Selected; the other states have no
        // slot, so activating them must leave the baseline untouched.
        var catalog = new StyleCatalog(Ordinary, Selected: Selected);

        await Assert.That(StylePriority.Resolve(catalog, VisualState.Hovered, false)).IsEqualTo(Ordinary);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Focused, false)).IsEqualTo(Ordinary);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Selected, false)).IsEqualTo(Selected);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Hovered | VisualState.Selected, false)).IsEqualTo(Selected);
    }

    [Test]
    public async Task Disabled_WinsOverOrdinaryAndEveryState()
    {
        var catalog = FullCatalog();
        VisualState all = VisualState.Hovered | VisualState.Active | VisualState.Selected | VisualState.Focused;

        await Assert.That(StylePriority.Resolve(catalog, VisualState.None, disabled: true)).IsEqualTo(Disabled);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Selected, disabled: true)).IsEqualTo(Disabled);
        await Assert.That(StylePriority.Resolve(catalog, all, disabled: true)).IsEqualTo(Disabled);
    }

    [Test]
    public async Task DisabledWithNoDisabledStyle_KeepsSemanticStyle()
    {
        // A catalog without a Disabled slot opts out of a separate disabled
        // appearance: disabling must not erase the semantic state.
        var catalog = new StyleCatalog(Ordinary, Selected: Selected);

        await Assert.That(StylePriority.Resolve(catalog, VisualState.None, disabled: true)).IsEqualTo(Ordinary);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.Selected, disabled: true)).IsEqualTo(Selected);
    }

    [Test]
    public async Task DisabledFlagIgnored_WhenCatalogHasNoDisabledSlot_AndNoState()
    {
        var catalog = new StyleCatalog(Ordinary);

        await Assert.That(StylePriority.Resolve(catalog, VisualState.None, disabled: true)).IsEqualTo(Ordinary);
        await Assert.That(StylePriority.Resolve(catalog, VisualState.None, disabled: false)).IsEqualTo(Ordinary);
    }
}
