// #2359: the declarative key-context stack — GPUI `DispatchTree` analogue.
// BCL-only, AOT-compatible, zero Harbor references, render-thread owned.
//
// BOUNDARIES (the point of the issue, so stated where a reviewer will see it):
// this is a primitive, NOT a second host input path. It resolves an opaque
// action id from a tree of KeyContexts and runs capture (root→focus) then
// bubble (focus→root) over view nodes. It never names a host action, so
// ChatKeyMap keeps owning the ChatAction vocabulary; it never interprets a
// chord, so LeaderKeyRouter keeps owning Ctrl+X and now shares PendingChordQueue
// for the timing. The existing InputRouting/FocusStack host loop is untouched.

namespace Harbor.Tui.CellForge.Input;

/// <summary>Which phase of the dispatch path produced the verdict.</summary>
public enum KeyRoutingPhase : byte
{
    /// <summary>No handler took the action; the host dispatches the returned id.</summary>
    None = 0,

    /// <summary>A capture handler on the root→focus path took it.</summary>
    Capture = 1,

    /// <summary>A bubble handler on the focus→root path took it (default stop).</summary>
    Bubble = 2,
}

/// <summary>What dispatch decided for one key.</summary>
public enum KeyDispatchVerdict : byte
{
    /// <summary>No context bound the key.</summary>
    Unhandled = 0,

    /// <summary>A context bound the key to an action id.</summary>
    Action = 1,

    /// <summary>A context bound the key to an explicit no-action.</summary>
    NoAction = 2,
}

/// <summary>One dispatch outcome: verdict, opaque action id (action only), and handling phase.</summary>
public readonly record struct DispatchResult(KeyDispatchVerdict Verdict, string? ActionId, KeyRoutingPhase Phase)
{
    /// <summary>No context bound the key.</summary>
    public static DispatchResult Unhandled => new(KeyDispatchVerdict.Unhandled, null, KeyRoutingPhase.None);

    /// <summary>An explicit no-action consumed the key.</summary>
    public static DispatchResult NoAction => new(KeyDispatchVerdict.NoAction, null, KeyRoutingPhase.None);

    /// <summary>An action was resolved; <paramref name="phase"/> says who took it.</summary>
    public static DispatchResult Action(string actionId, KeyRoutingPhase phase) =>
        new(KeyDispatchVerdict.Action, actionId, phase);
}

/// <summary>
/// One view in a <see cref="DispatchTree"/>: an id, a parent, attached
/// <see cref="KeyContext"/>s, and optional capture/bubble listeners. The
/// root→focus chain of these nodes is the view stack the two phases walk.
/// </summary>
public sealed class KeyViewNode
{
    private readonly List<KeyViewNode> _children = [];
    private readonly List<KeyContext> _contexts = [];
    private List<Func<KeyEvent, string, bool>>? _capture;
    private List<Func<KeyEvent, string, bool>>? _bubble;

    /// <summary>Creates a detached node.</summary>
    public KeyViewNode(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>Stable node id.</summary>
    public string Id { get; }

    /// <summary>The parent, or null for a detached node or the root.</summary>
    public KeyViewNode? Parent { get; private set; }

    /// <summary>Child nodes in insertion order.</summary>
    public IReadOnlyList<KeyViewNode> Children => _children;

    /// <summary>Contexts attached to this node, outer first.</summary>
    public IReadOnlyList<KeyContext> Contexts => _contexts;

    /// <summary>Attaches <paramref name="child"/> and returns it for chaining.</summary>
    public KeyViewNode AddChild(KeyViewNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent is not null)
        {
            throw new InvalidOperationException($"Node '{child.Id}' already has a parent.");
        }

        child.Parent = this;
        _children.Add(child);
        return child;
    }

    /// <summary>Attaches a key context to this node.</summary>
    public void AddContext(KeyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _contexts.Add(context);
    }

    /// <summary>Removes the first context with <paramref name="name"/>.</summary>
    public bool RemoveContext(string name)
    {
        for (int i = 0; i < _contexts.Count; i++)
        {
            if (string.Equals(_contexts[i].Name, name, StringComparison.Ordinal))
            {
                _contexts.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds a capture listener (root→focus). First true wins.</summary>
    public void OnCapture(Func<KeyEvent, string, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        (_capture ??= []).Add(handler);
    }

    /// <summary>Adds a bubble listener (focus→root). First true stops propagation.</summary>
    public void OnBubble(Func<KeyEvent, string, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        (_bubble ??= []).Add(handler);
    }

    internal bool Capture(KeyEvent key, string actionId)
    {
        if (_capture is null)
        {
            return false;
        }

        for (int i = 0; i < _capture.Count; i++)
        {
            if (_capture[i](key, actionId))
            {
                return true;
            }
        }

        return false;
    }

    internal bool Bubble(KeyEvent key, string actionId)
    {
        if (_bubble is null)
        {
            return false;
        }

        for (int i = 0; i < _bubble.Count; i++)
        {
            if (_bubble[i](key, actionId))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Holds a tree of <see cref="KeyContext"/>s and the focused view, and routes
/// one key in two phases: binding resolution by context depth (deepest wins,
/// insertion order breaks ties inside a context), then capture from root to
/// focus and bubble from focus back to root with a default stop at the first
/// handler that consumes the action. Indexed loops and a reusable path buffer;
/// no allocation on the miss path.
/// </summary>
public sealed class DispatchTree
{
    private readonly List<KeyViewNode> _path = [];
    private KeyViewNode? _root;
    private KeyViewNode? _focus;

    /// <summary>The tree root, created on first access.</summary>
    public KeyViewNode Root => _root ??= new KeyViewNode("root");

    /// <summary>The focused view, or null when the root itself has focus.</summary>
    public KeyViewNode? Focus => _focus;

    /// <summary>Adds a child under the root and returns it.</summary>
    public KeyViewNode AddChild(string id) => Root.AddChild(new KeyViewNode(id));

    /// <summary>Sets the focused view. The node must belong to this tree.</summary>
    public void SetFocus(KeyViewNode? node)
    {
        if (node is not null && RootOf(node) != Root)
        {
            throw new ArgumentException($"Node '{node.Id}' is not in this dispatch tree.", nameof(node));
        }

        _focus = node;
    }

    /// <summary>Routes one key through resolution, capture and bubble.</summary>
    public DispatchResult Dispatch(KeyEvent key)
    {
        BuildPath();

        // Depth priority: walk root→focus and keep the last match, so a deeper
        // context overrides a shallower one regardless of bind order. Inside a
        // single context KeyContext.TryMatch already picks the newest bind.
        ContextBinding? resolved = null;
        for (int i = 0; i < _path.Count; i++)
        {
            IReadOnlyList<KeyContext> contexts = _path[i].Contexts;
            for (int c = 0; c < contexts.Count; c++)
            {
                if (contexts[c].TryMatch(key, out ContextBinding hit))
                {
                    resolved = hit;
                }
            }
        }

        if (resolved is null)
        {
            return DispatchResult.Unhandled;
        }

        if (resolved.Value.IsNoAction)
        {
            return DispatchResult.NoAction;
        }

        string actionId = resolved.Value.ActionId!;

        // Capture: global listeners at the root get the first shot, then down
        // to the focus. A hit stops the whole dispatch.
        for (int i = 0; i < _path.Count; i++)
        {
            if (_path[i].Capture(key, actionId))
            {
                return DispatchResult.Action(actionId, KeyRoutingPhase.Capture);
            }
        }

        // Bubble: first handler from the focused view back to the root wins;
        // this is the repo's existing "first handled" semantics.
        for (int i = _path.Count - 1; i >= 0; i--)
        {
            if (_path[i].Bubble(key, actionId))
            {
                return DispatchResult.Action(actionId, KeyRoutingPhase.Bubble);
            }
        }

        return DispatchResult.Action(actionId, KeyRoutingPhase.None);
    }

    private void BuildPath()
    {
        _path.Clear();
        KeyViewNode? node = _focus ?? _root;
        while (node is not null)
        {
            _path.Add(node);
            node = node.Parent;
        }

        _path.Reverse();
    }

    private static KeyViewNode? RootOf(KeyViewNode node)
    {
        KeyViewNode? current = node;
        while (current.Parent is not null)
        {
            current = current.Parent;
        }

        return current;
    }
}
