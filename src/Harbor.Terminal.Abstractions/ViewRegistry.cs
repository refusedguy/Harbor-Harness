using System.Collections.Frozen;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging;
namespace Harbor.Terminal.Abstractions;
/// <summary>
///     Registry of TUI views. Implements Registry pattern (GOF).
///     Allows plugins to register custom views (status bars, panels, overlays).
/// </summary>
public sealed class ViewRegistry
{
    private readonly Dictionary<TuiViewPlacement, List<ITuiView>> _byPlacement = new();
    private readonly object _lock = new();
    private readonly ILogger<ViewRegistry>? _logger;
    private readonly Dictionary<string, ITuiView> _views = new(StringComparer.Ordinal);
    private FrozenDictionary<string, ITuiView>? _frozen;

    /// <summary>
    ///     Construct a <see cref="ViewRegistry" /> with an optional logger.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public ViewRegistry(ILogger<ViewRegistry>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Register a view. Replaces existing view with same ID.</summary>
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
            _frozen = null;
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
            _frozen = null;
            return true;
        }
    }

    /// <summary>Get a view by ID.</summary>
    public ITuiView? Get(string viewId)
    {
        var frozen = _frozen;
        if (frozen is not null && frozen.TryGetValue(viewId, out var v)) return v;
        lock (_lock)
        {
            return _views.TryGetValue(viewId, out var view) ? view : null;
        }
    }

    /// <summary>Get all views for a placement.</summary>
    public IReadOnlyList<ITuiView> GetByPlacement(TuiViewPlacement placement)
    {
        lock (_lock)
        {
            return _byPlacement.TryGetValue(placement, out var list)
                ? list.ToList()
                : Array.Empty<ITuiView>();
        }
    }

    /// <summary>Get all registered views.</summary>
    public IReadOnlyList<ITuiView> GetAll()
    {
        lock (_lock)
        {
            return _views.Values.ToList();
        }
    }

    /// <summary>Freeze for fast lookups (call after all plugins registered).</summary>
    public void Freeze()
    {
        lock (_lock)
        {
            _frozen = _views.ToFrozenDictionary();
        }
    }
}

/// <summary>
///     Registry of view models. Allows plugins to register custom view models
///     that views can bind to.
/// </summary>
public sealed class ViewModelRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ITuiViewModel> _viewModels = new(StringComparer.Ordinal);

    /// <summary>
    ///     Register (or replace) a view model by id.
    /// </summary>
    /// <param name="viewModel">The view model to register.</param>
    public void Register(ITuiViewModel viewModel)
    {
        lock (_lock)
        {
            _viewModels[viewModel.Id] = viewModel;
        }
    }

    /// <summary>
    ///     Unregister a view model by id.
    /// </summary>
    /// <param name="id">The view model id.</param>
    /// <returns><see langword="true" /> if the view model was registered and is now removed.</returns>
    public bool Unregister(string id)
    {
        lock (_lock)
        {
            return _viewModels.Remove(id);
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
        lock (_lock)
        {
            return _viewModels.TryGetValue(id, out var vm) ? vm as TViewModel : null;
        }
    }

    /// <summary>
    ///     Look up a view model by id.
    /// </summary>
    /// <param name="id">The view model id.</param>
    /// <returns>The view model, or <see langword="null" /> if not registered.</returns>
    public ITuiViewModel? Get(string id)
    {
        lock (_lock)
        {
            return _viewModels.TryGetValue(id, out var vm) ? vm : null;
        }
    }

    /// <summary>
    ///     Get a snapshot of all registered view models.
    /// </summary>
    /// <returns>A read-only list of registered view models.</returns>
    public IReadOnlyList<ITuiViewModel> GetAll()
    {
        lock (_lock)
        {
            return _viewModels.Values.ToList();
        }
    }
}

// The ITuiPlugin contract lives in Harbor.Terminal.Abstractions.Plugins (Plugins/ITuiPlugin.cs)
// alongside its full documentation. It is intentionally kept out of this file so that the
// registry types and the plugin contract evolve independently.
