using CSharpFunctionalExtensions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Panels;
/// <summary>
///     Read-only view over the panel registry plus <see cref="UiState" />. Use this
///     from renderers / layout shells to answer "which panels are visible right now?"
///     without taking a hard dependency on <see cref="PanelRegistry" />. All state
///     queries go through the supplied <see cref="UiState" /> — there is no
///     registry-side state mirror (TEA: single source of truth, §FP-005).
/// </summary>
public readonly record struct PanelRegistryView(
    IReadOnlyList<IPanelProvider> Providers,
    UiState State)
{
    /// <summary>Return providers that are not <see cref="TuiPanelState.Hidden" />.</summary>
    public IReadOnlyList<IPanelProvider> GetVisible()
    {
        if (Providers.Count == 0)
            return Array.Empty<IPanelProvider>();
        var result = new List<IPanelProvider>(Providers.Count);
        for (int i = 0; i < Providers.Count; i++)
        {
            var p = Providers[i];
            if (State.PanelStates.TryGetValue(p.Id, out var s) && s != TuiPanelState.Hidden)
                result.Add(p);
        }
        return result;
    }

    /// <summary>Visible providers filtered by <paramref name="placement" />.</summary>
    public IReadOnlyList<IPanelProvider> GetVisibleByPlacement(TuiPanelPlacement placement)
    {
        if (Providers.Count == 0)
            return Array.Empty<IPanelProvider>();
        var result = new List<IPanelProvider>(Providers.Count);
        for (int i = 0; i < Providers.Count; i++)
        {
            var p = Providers[i];
            if (p.DefaultPlacement == placement
                && State.PanelStates.TryGetValue(p.Id, out var s)
                && s != TuiPanelState.Hidden)
            {
                result.Add(p);
            }
        }
        return result;
    }

    /// <summary>Current state of the panel with this id (Hidden if unknown).</summary>
    public TuiPanelState GetState(string id) =>
        State.PanelStates.TryGetValue(id, out var s) ? s : TuiPanelState.Hidden;

    /// <summary>Current size override (rows or cols), or 0 = use provider's DefaultSize.</summary>
    public int GetSize(string id) =>
        State.PanelSizes.TryGetValue(id, out int s) ? s : 0;
}

/// <summary>
///     Registration-only contract for <see cref="IPanelProvider" /> instances. Plugin
///     authors and builtin loaders see this surface;
///     <b>
///         there is no state mutation
///         here
///     </b>
///     — every visibility / focus / size transition flows through
///     <see cref="UiStore" /> / <see cref="UiReducer" /> via
///     <see cref="UiMsg.TogglePanel" />, <see cref="UiMsg.FocusPanel" />,
///     <see cref="UiMsg.CyclePanelFocus" />, <see cref="UiMsg.ResizePanel" />. The
///     runtime state itself lives in <see cref="UiState.PanelStates" /> /
///     <see cref="UiState.PanelSizes" /> / <see cref="UiState.FocusedPanelId" /> — the
///     single source of truth (TEA compliance, §FP-005).
/// </summary>
/// <remarks>
///     <para>
///         Implemented with a plain <c>lock</c> rather than lock-free CAS because the
///         panel set is small (tens, not thousands) and mutations are rare (register on
///         plugin load, unregister on hot-reload). Reads during render are
///         O(registered-panels) and go through <see cref="PanelRegistryView" />.
///     </para>
/// </remarks>
public sealed class PanelRegistry : IPanelRegistry
{

    /// <summary>Default minimum size (rows or cols) clamped on resize by the reducer.</summary>
    public const int MinSize = 2;

    /// <summary>Default maximum size (rows or cols) clamped on resize by the reducer.</summary>
    public const int MaxSize = 200;
    private readonly object _gate = new();
    private readonly ILogger<PanelRegistry>? _logger;
    private readonly List<IPanelProvider> _ordered = new();
    private readonly Dictionary<string, IPanelProvider> _providers = new(StringComparer.Ordinal);

    /// <summary>Construct a registry with an optional logger.</summary>
    public PanelRegistry(ILogger<PanelRegistry>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Result Register(IPanelProvider panel)
    {
        if (string.IsNullOrEmpty(panel.Id))
            return Result.Failure("panel id is empty");

        lock (_gate)
        {
            if (_providers.TryGetValue(panel.Id, out var existing))
            {
                _logger?.LogWarning("Panel already registered, replacing: {Id}", panel.Id);
                // Preserve registration order: replace in-place at the existing slot
                // so plugin-override of builtins keeps the original Alt+N hotkey slot.
                int idx = _ordered.IndexOf(existing);
                if (idx >= 0)
                    _ordered[idx] = panel;
                else
                    _ordered.Add(panel);
            }
            else
            {
                _ordered.Add(panel);
            }

            _providers[panel.Id] = panel;
            _logger?.LogDebug("Registered panel {Id} ({Placement}, size={Size})",
                panel.Id, panel.DefaultPlacement, panel.DefaultSize);
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public Result Unregister(string id)
    {
        lock (_gate)
        {
            if (!_providers.Remove(id))
                return Result.Failure($"panel not registered: {id}");
            _ordered.RemoveAll(p => p.Id == id);
        }
        return Result.Success();
    }

    /// <inheritdoc />
    public IReadOnlyList<IPanelProvider> All
    {
        get
        {
            lock (_gate)
            {
                return _ordered.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IPanelProvider? Get(string id)
    {
        lock (_gate)
        {
            return _providers.TryGetValue(id, out var p) ? p : null;
        }
    }

/// <summary>
///     Build a read-only <see cref="PanelRegistryView" /> snapshot over this
///     registry and the supplied <see cref="UiState" />. Renderers call this once
///     per frame to answer visibility / size queries without touching the
///     registry's internal lock.
/// </summary>
public PanelRegistryView View(UiState state)
{
    var snapshot = All;
    return new PanelRegistryView(snapshot, state);
}
}

/// <summary>
///     Registration-only abstraction over <see cref="PanelRegistry" /> used by plugin
///     contracts so plugins do not depend on the concrete implementation. Plugins
///     declare panels via <see cref="Register" />; <b>they cannot mutate panel state</b>
///     — all state transitions flow through <see cref="UiReducer" />.
/// </summary>
public interface IPanelRegistry
{

    /// <summary>All registered providers in registration order.</summary>
    public IReadOnlyList<IPanelProvider> All { get; }
    /// <summary>Register a panel provider. Returns failure on empty id.</summary>
    public Result Register(IPanelProvider panel);

    /// <summary>Unregister a panel by id.</summary>
    public Result Unregister(string id);

    /// <summary>Look up a provider by id.</summary>
    public IPanelProvider? Get(string id);
}
