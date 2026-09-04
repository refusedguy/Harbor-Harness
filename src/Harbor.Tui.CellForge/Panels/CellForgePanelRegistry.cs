using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Thin owner of the shared <see cref="PanelRegistry" /> for the CellForge
///     renderer (CF-E-001 preparation). Mirrors
///     <c>SpectreTuiRenderer.Panels + SeedPanelRegistryIntoState</c>: the
///     registry holds <see cref="IPanelProvider" /> instances only, all
///     visibility / focus / size state lives in <see cref="UiState" /> and is
///     seeded via <see cref="UiMsg.SeedPanels" />.
/// </summary>
/// <remarks>
///     Infrastructure only: this class does NOT register the 7 SpectreTui
///     builtins (help/logs/diagnostics/diff-preview/file-tree/todo-list/
///     token-breakdown). Callers register providers through
///     <see cref="Registry" /> (or <see cref="Register" />) and then call
///     <see cref="EnsureSeeded" /> so the reducer becomes the single source
///     of truth. Existing widgets and <c>CellForgeTuiRenderer</c> are untouched.
/// </remarks>
public sealed class CellForgePanelRegistry
{
    /// <summary>Create an owner with a fresh empty registry.</summary>
    public CellForgePanelRegistry()
        : this(new PanelRegistry())
    {
    }

    /// <summary>Create an owner around a host-supplied registry (tests / DI).</summary>
    public CellForgePanelRegistry(PanelRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    ///     The underlying registration-only registry. Registration order is
    ///     significant (Alt+1..9 hotkey slots follow it).
    /// </summary>
    public PanelRegistry Registry { get; }

    /// <summary>Register a panel provider (in-place replace on duplicate id).</summary>
    public void Register(IPanelProvider panel) => _ = Registry.Register(panel);

    /// <summary>
    ///     Seed registered panel ids + states + sizes into <see cref="UiState" />
    ///     via <see cref="UiMsg.SeedPanels" />. Preserves already-known state
    ///     for re-registered ids (same rule as SpectreTui seeding); unknown ids
    ///     start <see cref="TuiPanelState.Hidden" /> with the provider's
    ///     <c>DefaultSize</c>. Returns the reducer effect for the host to run.
    /// </summary>
    /// <param name="store">The CellForge TEA store to seed.</param>
    public TuiEffect EnsureSeeded(UiStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var all = Registry.All;
        var idsBuilder = ImmutableArray.CreateBuilder<string>(all.Count);
        var statesBuilder = ImmutableDictionary.CreateBuilder<string, TuiPanelState>(StringComparer.Ordinal);
        var sizesBuilder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var current = store.State;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            idsBuilder.Add(p.Id);
            statesBuilder.Add(p.Id, current.PanelStates.TryGetValue(p.Id, out var s)
                ? s
                : TuiPanelState.Hidden);
            sizesBuilder.Add(p.Id, current.PanelSizes.TryGetValue(p.Id, out int sz)
                ? sz
                : p.DefaultSize);
        }

        return store.Dispatch(new UiMsg.SeedPanels(
            idsBuilder.MoveToImmutable(),
            statesBuilder.ToImmutable(),
            sizesBuilder.ToImmutable()));
    }
}
