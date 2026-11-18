using System.Diagnostics.CodeAnalysis;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Named panel registry behind <see cref="LayoutTree"/>. Owns every id-keyed
/// operation (lookup, registration, replacement, removal) so identity has one
/// home and the tree can concentrate on geometry. O(1) by construction.
/// </summary>
/// <remarks>
/// <see cref="Values"/> is the dictionary's own value view, so the per-frame
/// paint and cache-replay loops keep their struct enumerator and do not box
/// the way an <c>IReadOnlyCollection</c> foreach would.
/// </remarks>
internal sealed class PanelIndex
{
    private readonly Dictionary<string, Panel> _byId = [];

    /// <summary>Registered panels. Identity and membership live here, not in the tree.</summary>
    public IReadOnlyCollection<Panel> Panels => _byId.Values;

    /// <summary>Struct-enumerating values view for hot paths.</summary>
    public Dictionary<string, Panel>.ValueCollection Values => _byId.Values;

    /// <summary>Resolves an id, throwing when it is unknown.</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is not registered.</exception>
    public Panel Get(string id) =>
        _byId.TryGetValue(id, out var panel)
            ? panel
            : throw new KeyNotFoundException($"panel '{id}' not found");

    /// <summary>Tries named lookup without throwing.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out Panel? panel) => _byId.TryGetValue(id, out panel);

    /// <summary>True when <paramref name="id"/> is registered.</summary>
    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>Adds or replaces a panel under its own id.</summary>
    public void Register(Panel panel) => _byId[panel.Id] = panel;

    /// <summary>Replaces an existing id with a same-id panel (in-place leaf swap).</summary>
    public void Replace(string id, Panel panel) => _byId[id] = panel;

    /// <summary>Unregisters an id. No-op when it is unknown.</summary>
    public bool Remove(string id) => _byId.Remove(id);
}
