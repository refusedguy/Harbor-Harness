using System;
using System.Collections.Generic;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     In-memory tree of numeric element ids. The applier's phase 3 writes
///     only here, after phases 1 and 2 have proved the batch safe. Each write
///     reports whether it changed state, which is what drives the dirty flag.
///     The tree is BCL only and knows nothing about Scene or DamageLedger.
/// </summary>
internal sealed class RetainedElementTree
{
    private readonly Dictionary<int, Element> _elements = [];
    private int _rootId;

    internal int RootId => _rootId;

    internal bool Contains(int id) => _elements.ContainsKey(id);

    internal void CollectState(HashSet<int> ids, Dictionary<int, int> parents)
    {
        foreach (var pair in _elements)
        {
            ids.Add(pair.Key);
            parents[pair.Key] = pair.Value.ParentId;
        }
    }

    internal int? StyleIdOf(int id) => _elements.TryGetValue(id, out var element) && element.StyleId >= 0
        ? element.StyleId
        : null;

    internal string? TextOf(int id) => _elements.TryGetValue(id, out var element) ? element.Text : null;

    internal bool HasListener(int id, string eventType) =>
        _elements.TryGetValue(id, out var element)
        && element.Listeners is not null
        && element.Listeners.Contains(eventType);

    internal IReadOnlyList<int> ChildrenOf(int id) => Get(id).Children;

    internal bool Create(int id, SpecKind kind)
    {
        if (_elements.ContainsKey(id))
            return false;
        _elements.Add(id, new Element(id, kind));
        return true;
    }

    internal bool SetStyle(int id, int styleId)
    {
        var element = Get(id);
        if (element.StyleId == styleId)
            return false;
        element.StyleId = styleId;
        return true;
    }

    internal bool SetText(int id, string text)
    {
        var element = Get(id);
        if (string.Equals(element.Text, text, StringComparison.Ordinal))
            return false;
        element.Text = text;
        return true;
    }

    internal bool SetListener(int id, string eventType, bool enabled)
    {
        var element = Get(id);
        bool had = element.Listeners?.Contains(eventType) ?? false;
        if (had == enabled)
            return false;
        if (enabled)
            (element.Listeners ??= new HashSet<string>(StringComparer.Ordinal)).Add(eventType);
        else
            element.Listeners!.Remove(eventType);
        return true;
    }

    internal bool Append(int parentId, int childId)
    {
        var siblings = Get(parentId).Children;
        var child = Get(childId);
        if (child.ParentId == parentId && siblings.Count > 0 && siblings[^1] == childId)
            return false;

        Detach(child);
        siblings.Add(childId);
        child.ParentId = parentId;
        return true;
    }

    internal bool InsertBefore(int parentId, int childId, int beforeId)
    {
        var siblings = Get(parentId).Children;
        int index = siblings.IndexOf(beforeId);
        var child = Get(childId);

        if (child.ParentId == parentId)
        {
            int current = siblings.IndexOf(childId);
            if (current == index - 1)
                return false;
            siblings.RemoveAt(current);
            if (current < index)
                index--;
        }
        else
        {
            Detach(child);
        }

        siblings.Insert(index, childId);
        child.ParentId = parentId;
        return true;
    }

    internal bool SetRoot(int id)
    {
        if (_rootId == id)
            return false;
        _rootId = id;
        return true;
    }

    internal int[] Destroy(int id)
    {
        var freed = new List<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            int current = stack.Pop();
            if (!_elements.TryGetValue(current, out var element))
                continue;

            freed.Add(current);
            foreach (int child in element.Children)
                stack.Push(child);
        }

        foreach (int freedId in freed)
        {
            var element = _elements[freedId];
            if (element.ParentId != 0 && _elements.TryGetValue(element.ParentId, out var parent))
                parent.Children.Remove(freedId);
            _elements.Remove(freedId);
        }

        if (_rootId != 0 && !_elements.ContainsKey(_rootId))
            _rootId = 0;
        return freed.ToArray();
    }

    private void Detach(Element child)
    {
        if (child.ParentId != 0 && _elements.TryGetValue(child.ParentId, out var parent))
            parent.Children.Remove(child.Id);
        child.ParentId = 0;
    }

    private Element Get(int id)
    {
        if (!_elements.TryGetValue(id, out var element))
            throw new InvalidOperationException($"Element {id} is missing in the apply phase; validation accepted it by mistake.");
        return element;
    }

    private sealed class Element
    {
        internal Element(int id, SpecKind kind)
        {
            Id = id;
            Kind = kind;
        }

        internal SpecKind Kind { get; }
        internal int StyleId { get; set; } = -1;
        internal string? Text { get; set; }
        internal int ParentId { get; set; }
        internal int Id { get; set; }
        internal List<int> Children { get; } = [];
        internal HashSet<string>? Listeners { get; set; }
    }
}
