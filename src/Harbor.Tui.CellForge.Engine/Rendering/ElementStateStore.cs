// #2436: element state that lives longer than one frame, keyed by the stable
// global element id and the state's TypeId. An accessed set is built during a
// frame; EndFrame drops entries no element touched, so state left behind by a
// changed path does not accumulate. BCL-only, AOT-compatible, render-thread
// owned. System.Type is the TypeId: typeof(T) is resolved without reflection
// over strings and works under NativeAOT.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>The type half of an element-state key.</summary>
internal readonly record struct TypeId(Type RuntimeType)
{
    internal static TypeId Of<TState>() => new(typeof(TState));
}

/// <summary>One element-state slot: a global element id and a state type.</summary>
internal readonly record struct ElementStateKey(ElementId Id, TypeId StateType);

/// <summary>
/// Stores per-element state across frames. Call <see cref="BeginFrame"/> before
/// walking the tree and <see cref="EndFrame"/> after it; every
/// <see cref="GetOrCreate{TState}"/> or <see cref="TryGet{TState}"/> marks its
/// slot as accessed. EndFrame removes the slots that were not accessed.
/// </summary>
/// <remarks>
/// One store is owned by one render thread. After the first use of a slot the
/// lookup, the access mark, and a frame with no dead slots allocate nothing.
/// </remarks>
internal sealed class ElementStateStore
{
    private readonly Dictionary<ElementStateKey, object> _states = [];
    private readonly HashSet<ElementStateKey> _accessed = [];
    private readonly List<ElementStateKey> _dead = [];

    /// <summary>Number of live state slots.</summary>
    internal int Count => _states.Count;

    /// <summary>Starts a frame by clearing the accessed set.</summary>
    internal void BeginFrame() => _accessed.Clear();

    /// <summary>Drops slots that no element accessed during the frame.</summary>
    internal void EndFrame()
    {
        _dead.Clear();
        foreach (var key in _states.Keys)
        {
            if (!_accessed.Contains(key))
            {
                _dead.Add(key);
            }
        }

        for (int i = 0; i < _dead.Count; i++)
        {
            _states.Remove(_dead[i]);
        }

        _accessed.Clear();
    }

    /// <summary>Returns the state for this element and type, creating it on first use.</summary>
    internal TState GetOrCreate<TState>(ElementId id)
        where TState : class, new()
    {
        var key = new ElementStateKey(id, TypeId.Of<TState>());
        _accessed.Add(key);

        if (_states.TryGetValue(key, out var found))
        {
            return (TState)found;
        }

        var created = new TState();
        _states.Add(key, created);
        return created;
    }

    /// <summary>Looks up state for this element and type and marks the slot as accessed.</summary>
    internal bool TryGet<TState>(ElementId id, out TState? state)
        where TState : class
    {
        var key = new ElementStateKey(id, TypeId.Of<TState>());
        _accessed.Add(key);

        if (_states.TryGetValue(key, out var found))
        {
            state = (TState)found;
            return true;
        }

        state = null;
        return false;
    }

    /// <summary>Drops all state, including the accessed set.</summary>
    internal void Reset()
    {
        _states.Clear();
        _accessed.Clear();
        _dead.Clear();
    }
}
