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
///     seeded via <see cref="AppMsg.SeedPanels" />.
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
    ///     via <see cref="AppMsg.SeedPanels" />. Preserves already-known state
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
            statesBuilder.Add(p.Id, current.Ui.PanelStates.TryGetValue(p.Id, out var s)
                ? s
                : TuiPanelState.Hidden);
            sizesBuilder.Add(p.Id, current.Ui.PanelSizes.TryGetValue(p.Id, out int sz)
                ? sz
                : p.DefaultSize);
        }

        return store.Dispatch(new AppMsg.SeedPanels(
            idsBuilder.MoveToImmutable(),
            statesBuilder.ToImmutable(),
            sizesBuilder.ToImmutable()));
    }
}

/// <summary>
/// UX1 (#261, part of epic #260): single-active-panel arbiter for the fixed
/// slot layout (feed / composer / ONE active panel / statusline).
/// Resolves at most one winner across all visible providers: the focused panel
/// first, then the first pinned panel, then the first visible panel — ties
/// break in registration order (same order as Alt+1..9 hotkeys and
/// <c>CycleFocus</c>). Everything secondary stays mounted in
/// <see cref="UiState"/> for the future modal layer; the dock simply does not
/// paint it. Pure: reads only the provider list + snapshot, never mutates.
/// </summary>
public static class PanelArbiter
{
    /// <summary>Resolve the single active panel from a registry + snapshot.</summary>
    public static IPanelProvider? ResolveActive(PanelRegistry registry, UiState state)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);
        return ResolveActive(registry.All, state);
    }

    /// <summary>
    /// Resolve the single active panel from an ordered provider list + snapshot.
    /// Order is significant: pinned/visible fallbacks pick the first match.
    /// </summary>
    public static IPanelProvider? ResolveActive(IReadOnlyList<IPanelProvider> providers, UiState state)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(state);

        // FocusedPanelId is authoritative when its state agrees.
        string? focusedId = state.Ui.FocusedPanelId;
        if (!string.IsNullOrEmpty(focusedId)
            && state.Ui.PanelStates.TryGetValue(focusedId, out var focusedState)
            && focusedState == TuiPanelState.Focused)
        {
            for (int i = 0; i < providers.Count; i++)
            {
                if (string.Equals(providers[i].Id, focusedId, StringComparison.Ordinal))
                {
                    return providers[i];
                }
            }
        }

        IPanelProvider? firstPinned = null;
        IPanelProvider? firstVisible = null;
        for (int i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            if (!state.Ui.PanelStates.TryGetValue(provider.Id, out var panelState)
                || panelState == TuiPanelState.Hidden)
            {
                continue;
            }

            if (panelState == TuiPanelState.Focused)
            {
                return provider;
            }

            if (panelState == TuiPanelState.Pinned)
            {
                firstPinned ??= provider;
            }
            else
            {
                firstVisible ??= provider;
            }
        }

        return firstPinned ?? firstVisible;
    }
}
