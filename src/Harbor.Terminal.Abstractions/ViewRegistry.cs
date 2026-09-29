using System.Collections.Frozen;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging;
namespace Harbor.Terminal.Abstractions;
/// <summary>
///     Registry of TUI views. Implements Registry pattern (GOF).
///     Allows plugins to register custom views (status bars, panels, overlays).
/// </summary>
/// <remarks>
///     <para>
///         <b>#490 — frozen read path.</b> <see cref="GetAll" /> and
///         <see cref="GetByPlacement" /> are called from
///         <c>BaseTuiRenderer.RenderAsync</c> for <b>every</b>
///         <c>AgentEvent</c> (including per-token <c>MessageUpdateEvent</c>).
///         They used to take the write lock and <c>ToList()</c> the backing
///         collections on each call, so the hottest dispatch path in the TUI
///         paid a lock acquisition plus a <c>List</c> + backing-array
///         allocation per call for data that is immutable once plugins are
///         registered — exactly what <see cref="Freeze" /> promises. Before
///         this change the frozen dictionary built by <see cref="Freeze" />
///         was dead code: nothing ever read it.
///     </para>
///     <para>
///         <see cref="Freeze" /> now publishes one immutable snapshot
///         (frozen map + prebuilt arrays) with a single volatile write, and
///         every read path is a field read: no lock, no allocation.
///     </para>
///     <para>
///         <b>Contract (unchanged, mirrors <c>IToolRegistry</c>).</b>
///         <see cref="Register" /> / <see cref="Unregister" /> after
///         <see cref="Freeze" /> are <b>accepted</b> and drop the snapshot,
///         returning the registry to the lock-based live path until
///         <see cref="Freeze" /> is called again — a late plugin registration
///         is never silently lost. Callers MUST treat every returned list as a
///         read-only snapshot: the frozen path hands back a shared array.
///     </para>
/// </remarks>
public sealed class ViewRegistry
{
    /// <summary>
    ///     Number of <see cref="TuiViewPlacement" /> slots the placement index
    ///     array covers. Derived from the enum (not hard-coded) so adding a
    ///     placement cannot silently truncate the index.
    /// </summary>
    private static readonly int PlacementSlots = SlotCount();

    private readonly Dictionary<TuiViewPlacement, List<ITuiView>> _byPlacement = new();
    private readonly object _lock = new();
    private readonly ILogger<ViewRegistry>? _logger;
    private readonly Dictionary<string, ITuiView> _views = new(StringComparer.Ordinal);

    /// <summary>
    ///     Publish-once read snapshot. <see langword="null" /> until
    ///     <see cref="Freeze" /> is called, and again after any
    ///     <see cref="Register" /> / <see cref="Unregister" />. Volatile so a
    ///     lock-free reader either sees the fully built snapshot or
    ///     <see langword="null" /> — never a half-published one.
    /// </summary>
    private volatile ViewSnapshot? _frozen;

    /// <summary>
    ///     Construct a <see cref="ViewRegistry" /> with an optional logger.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public ViewRegistry(ILogger<ViewRegistry>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Register a view. Replaces existing view with same ID. Accepted after
    ///     <see cref="Freeze" />: the frozen snapshot is dropped and reads fall
    ///     back to the lock-based live path until the next <see cref="Freeze" />.
    /// </summary>
    public void Register(ITuiView view)
    {
        lock (_lock)
        {
            // Remove old view with same ID from placement list
            if (_views.TryGetValue(view.Id, out var oldView))
            {
                if (_byPlacement.TryGetValue(oldView.Placement, out var oldList))
                    oldList.Remove(oldView);
            }

            _views[view.Id] = view;
            if (!_byPlacement.TryGetValue(view.Placement, out var list))
            {
                list = new List<ITuiView>();
                _byPlacement[view.Placement] = list;
            }
            if (!list.Contains(view)) list.Add(view);
            Invalidate();
            _logger?.LogDebug("Registered view: {Id} ({Placement})", view.Id, view.Placement);
        }
    }

    /// <summary>Unregister a view by ID.</summary>
    public bool Unregister(string viewId)
    {
        lock (_lock)
        {
            if (!_views.TryGetValue(viewId, out var view)) return false;
            _views.Remove(viewId);
            if (_byPlacement.TryGetValue(view.Placement, out var list))
                list.Remove(view);
            Invalidate();
            return true;
        }
    }

    /// <summary>Get a view by ID.</summary>
    public ITuiView? Get(string viewId)
    {
        var frozen = _frozen;
        if (frozen is not null)
        {
            return frozen.Map.TryGetValue(viewId, out var fv) ? fv : null;
        }

        lock (_lock)
        {
            return _views.TryGetValue(viewId, out var view) ? view : null;
        }
    }

    /// <summary>
    ///     Get all views for a placement.
    /// </summary>
    /// <returns>
    ///     A read-only snapshot. Frozen: the shared array built by
    ///     <see cref="Freeze" /> (zero allocation, no lock). Unfrozen: a fresh
    ///     list built under the lock.
    /// </returns>
    public IReadOnlyList<ITuiView> GetByPlacement(TuiViewPlacement placement)
    {
        var frozen = _frozen;
        if (frozen is not null) return frozen.GetByPlacement(placement);
        lock (_lock)
        {
            return _byPlacement.TryGetValue(placement, out var list)
                ? list.ToList()
                : Array.Empty<ITuiView>();
        }
    }

    /// <summary>
    ///     Get all registered views.
    /// </summary>
    /// <returns>
    ///     A read-only snapshot. Frozen: the shared array built by
    ///     <see cref="Freeze" /> (zero allocation, no lock). Unfrozen: a fresh
    ///     list built under the lock.
    /// </returns>
    public IReadOnlyList<ITuiView> GetAll()
    {
        var frozen = _frozen;
        if (frozen is not null) return frozen.All;
        lock (_lock)
        {
            return _views.Values.ToList();
        }
    }

    /// <summary>
    ///     Freeze for fast, lock-free, allocation-free lookups (call after all
    ///     plugins registered). Builds the frozen map and the prebuilt
    ///     <see cref="GetAll" /> / <see cref="GetByPlacement" /> arrays, then
    ///     publishes them as one snapshot.
    /// </summary>
    public void Freeze()
    {
        lock (_lock)
        {
            _frozen = ViewSnapshot.Build(_views, _byPlacement);
        }
    }

    /// <summary>
    ///     Drops the frozen snapshot. Called (under the lock) by every mutation
    ///     so a post-freeze registration is visible to the lock-based live path
    ///     instead of being served from a stale snapshot.
    /// </summary>
    private void Invalidate() => _frozen = null;

    private static int SlotCount()
    {
        var values = Enum.GetValues<TuiViewPlacement>();
        int max = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int slot = (int)values[i];
            if (slot > max)
            {
                max = slot;
            }
        }

        return max + 1;
    }

    /// <summary>
    ///     Immutable publish-once read view (#490). Built once per
    ///     <see cref="Freeze" /> and swapped in with a single volatile write, so
    ///     readers never observe a torn view — a frozen map from one generation
    ///     paired with arrays from another.
    /// </summary>
    private sealed class ViewSnapshot
    {
        /// <summary>Frozen id → view map. Published together with <see cref="All" />.</summary>
        internal readonly FrozenDictionary<string, ITuiView> Map;

        /// <summary>Prebuilt <c>GetAll()</c> array — returned as-is, zero allocation.</summary>
        internal readonly ITuiView[] All;

        /// <summary>Per-placement prebuilt arrays; every slot is non-null.</summary>
        private readonly ITuiView[][] _byPlacement;

        private ViewSnapshot(FrozenDictionary<string, ITuiView> map, ITuiView[] all, ITuiView[][] byPlacement)
        {
            Map = map;
            All = all;
            _byPlacement = byPlacement;
        }

        public static ViewSnapshot Build(
            Dictionary<string, ITuiView> views,
            Dictionary<TuiViewPlacement, List<ITuiView>> byPlacement)
        {
            // No LINQ: plain loops on the freeze path (hot-path house rule).
            var map = views.ToFrozenDictionary(StringComparer.Ordinal);
            var all = new ITuiView[map.Count];
            int i = 0;
            foreach (var view in map.Values)
            {
                all[i++] = view;
            }

            // One slot per placement, pre-filled with the shared empty array so a
            // frozen GetByPlacement never allocates — not even for a placement
            // with no views.
            var placements = new ITuiView[PlacementSlots][];
            for (int slot = 0; slot < placements.Length; slot++)
            {
                placements[slot] = Array.Empty<ITuiView>();
            }

            foreach (var (placement, list) in byPlacement)
            {
                int slot = (int)placement;
                if (slot < 0 || slot >= placements.Length || list.Count == 0) continue;
                var bucket = new ITuiView[list.Count];
                for (int v = 0; v < list.Count; v++)
                {
                    bucket[v] = list[v];
                }

                placements[slot] = bucket;
            }

            return new ViewSnapshot(map, all, placements);
        }

        public IReadOnlyList<ITuiView> GetByPlacement(TuiViewPlacement placement)
        {
            int slot = (int)placement;
            return slot >= 0 && slot < _byPlacement.Length ? _byPlacement[slot] : Array.Empty<ITuiView>();
        }
    }
}

/// <summary>
///     Registry of view models. Allows plugins to register custom view models
///     that views can bind to.
/// </summary>
/// <remarks>
///     <para>
///         <b>#490 — frozen read path.</b> <see cref="GetAll" /> is called from
///         <c>BaseTuiRenderer.RenderAsync</c> for <b>every</b>
///         <c>AgentEvent</c>, and used to take the write lock and
///         <c>ToList()</c> each time. This registry had no
///         <see cref="Freeze" /> at all, so there was no fast path to take.
///         <see cref="Freeze" /> now publishes one immutable snapshot and the
///         reads are a field read: no lock, no allocation.
///     </para>
///     <para>
///         <b>Contract (mirrors <see cref="ViewRegistry" />).</b>
///         <see cref="Register" /> / <see cref="Unregister" /> after
///         <see cref="Freeze" /> are <b>accepted</b> and drop the snapshot,
///         returning the registry to the lock-based live path until
///         <see cref="Freeze" /> is called again. Callers MUST treat every
///         returned list as a read-only snapshot.
///     </para>
/// </remarks>
public sealed class ViewModelRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ITuiViewModel> _viewModels = new(StringComparer.Ordinal);

    /// <summary>
    ///     Publish-once read snapshot; see <see cref="ViewRegistry" /> for the
    ///     contract. <see langword="null" /> until <see cref="Freeze" /> is
    ///     called and again after any mutation.
    /// </summary>
    private volatile ViewModelSnapshot? _frozen;

    /// <summary>
    ///     Register (or replace) a view model by id. Accepted after
    ///     <see cref="Freeze" />: the frozen snapshot is dropped and reads fall
    ///     back to the lock-based live path until the next <see cref="Freeze" />.
    /// </summary>
    /// <param name="viewModel">The view model to register.</param>
    public void Register(ITuiViewModel viewModel)
    {
        lock (_lock)
        {
            _viewModels[viewModel.Id] = viewModel;
            _frozen = null;
        }
    }

    /// <summary>
    ///     Unregister a view model by id. Accepted after <see cref="Freeze" />:
    ///     a successful removal drops the frozen snapshot so reads fall back to
    ///     the lock-based live path until the next <see cref="Freeze" />.
    /// </summary>
    /// <param name="id">The view model id.</param>
    /// <returns><see langword="true" /> if the view model was registered and is now removed.</returns>
    public bool Unregister(string id)
    {
        lock (_lock)
        {
            if (!_viewModels.Remove(id)) return false;
            _frozen = null;
            return true;
        }
    }

    /// <summary>
    ///     Look up a view model by id, returning it as a specific subtype.
    /// </summary>
    /// <typeparam name="TViewModel">The expected view model type.</typeparam>
    /// <param name="id">The view model id.</param>
    /// <returns>The strongly-typed view model, or <see langword="null" />.</returns>
    public TViewModel? Get<TViewModel>(string id) where TViewModel : class, ITuiViewModel
    {
        return Get(id) as TViewModel;
    }

    /// <summary>
    ///     Look up a view model by id.
    /// </summary>
    /// <param name="id">The view model id.</param>
    /// <returns>The view model, or <see langword="null" /> if not registered.</returns>
    public ITuiViewModel? Get(string id)
    {
        var frozen = _frozen;
        if (frozen is not null)
        {
            return frozen.Map.TryGetValue(id, out var fvm) ? fvm : null;
        }

        lock (_lock)
        {
            return _viewModels.TryGetValue(id, out var vm) ? vm : null;
        }
    }

    /// <summary>
    ///     Get a snapshot of all registered view models.
    /// </summary>
    /// <returns>
    ///     A read-only snapshot. Frozen: the shared array built by
    ///     <see cref="Freeze" /> (zero allocation, no lock). Unfrozen: a fresh
    ///     list built under the lock.
    /// </returns>
    public IReadOnlyList<ITuiViewModel> GetAll()
    {
        var frozen = _frozen;
        if (frozen is not null) return frozen.All;
        lock (_lock)
        {
            return _viewModels.Values.ToList();
        }
    }

    /// <summary>
    ///     Freeze for fast, lock-free, allocation-free lookups (call after all
    ///     plugins registered).
    /// </summary>
    public void Freeze()
    {
        lock (_lock)
        {
            var map = _viewModels.ToFrozenDictionary(StringComparer.Ordinal);
            var all = new ITuiViewModel[map.Count];
            int i = 0;
            foreach (var vm in map.Values)
            {
                all[i++] = vm;
            }

            _frozen = new ViewModelSnapshot(map, all);
        }
    }

    /// <summary>
    ///     Immutable publish-once read view (#490): the frozen map plus the
    ///     prebuilt <see cref="GetAll" /> array, published together so readers
    ///     never see a map and an array from different generations.
    /// </summary>
    private sealed class ViewModelSnapshot(FrozenDictionary<string, ITuiViewModel> map, ITuiViewModel[] all)
    {
        /// <summary>Frozen id → view-model map. Published together with <see cref="All" />.</summary>
        internal readonly FrozenDictionary<string, ITuiViewModel> Map = map;

        /// <summary>Prebuilt <c>GetAll()</c> array — returned as-is, zero allocation.</summary>
        internal readonly ITuiViewModel[] All = all;
    }
}

// The ITuiPlugin contract lives in Harbor.Terminal.Abstractions.Plugins (Plugins/ITuiPlugin.cs)
// alongside its full documentation. It is intentionally kept out of this file so that the
// registry types and the plugin contract evolve independently.
