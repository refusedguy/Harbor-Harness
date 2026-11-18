// #2361: stable element identity for the prepaint frame pipeline. GPUI's
// GlobalElementId analogue: a path from the frame root whose hash is folded
// from the prefix, so a subtree can be addressed by the same id in two
// consecutive frames. BCL-only, AOT-compatible, render-thread owned, same
// leaf rule as INode.cs and Scene.cs.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>One immutable step of an <see cref="ElementId"/> path.</summary>
internal sealed class ElementPathSegment
{
    internal ElementPathSegment(ElementPathSegment? parent, string? key, int index)
    {
        Parent = parent;
        Key = key;
        Index = index;
    }

    internal ElementPathSegment? Parent { get; }
    internal string? Key { get; }
    internal int Index { get; }
}

/// <summary>
/// Stable identity of a node: the path from the frame root plus a hash folded
/// from that prefix. Two nodes at the same path in two frames have equal ids,
/// which is what lets <see cref="PrepaintContext"/> reuse a record; moving a
/// node or changing a named/indexed step changes the id and forces a fresh
/// record. Value equality walks the whole path, so the folded hash alone is
/// never the only comparison.
/// </summary>
public readonly struct ElementId : IEquatable<ElementId>
{
    private readonly ElementPathSegment? _tail;
    private readonly int _hash;
    private readonly int _depth;

    internal ElementId(ElementPathSegment? tail, int hash, int depth)
    {
        _tail = tail;
        _hash = hash;
        _depth = depth;
    }

    /// <summary>The frame root. Its path is <c>/</c> and its prefix hash is zero.</summary>
    public static ElementId Root => default;

    /// <summary>True for <see cref="Root"/>.</summary>
    public bool IsRoot => _tail is null;

    /// <summary>Number of path steps below the root.</summary>
    public int Depth => _depth;

    /// <summary>Hash folded from the path prefix; used as the cache bucket.</summary>
    internal int PrefixHash => _hash;

    /// <summary>Appends a named step (an id that does not depend on position).</summary>
    public ElementId Child(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var tail = new ElementPathSegment(_tail, key, 0);
        return new ElementId(tail, ExtendHash(_hash, key), _depth + 1);
    }

    /// <summary>Appends an indexed step (a position in an ordered container).</summary>
    public ElementId Child(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var tail = new ElementPathSegment(_tail, null, index);
        return new ElementId(tail, ExtendHash(_hash, index), _depth + 1);
    }

    // #2436: one folding rule for both routes to an id. ElementIdStack feeds a
    // cached parent hash into these, so a push hashes one step instead of the
    // whole ancestor path and still produces an id equal to Child's.
    internal static int ExtendHash(int prefix, string key) =>
        HashCode.Combine(prefix, StringComparer.Ordinal.GetHashCode(key));

    internal static int ExtendHash(int prefix, int index) =>
        HashCode.Combine(prefix, index);

    /// <inheritdoc />
    public bool Equals(ElementId other)
    {
        if (_depth != other._depth || _hash != other._hash)
        {
            return false;
        }

        var a = _tail;
        var b = other._tail;
        while (a is not null && b is not null)
        {
            if (a.Index != b.Index || !string.Equals(a.Key, b.Key, StringComparison.Ordinal))
            {
                return false;
            }

            a = a.Parent;
            b = b.Parent;
        }

        return a is null && b is null;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ElementId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _hash;

    /// <summary>Equality by full path, not just by the folded hash.</summary>
    public static bool operator ==(ElementId left, ElementId right) => left.Equals(right);

    /// <summary>Path inequality.</summary>
    public static bool operator !=(ElementId left, ElementId right) => !left.Equals(right);

    /// <summary>Diagnostic path, e.g. <c>/row/2/text</c>.</summary>
    public override string ToString()
    {
        if (_tail is null)
        {
            return "/";
        }

        var parts = new string[_depth];
        var segment = _tail;
        for (int i = _depth - 1; i >= 0 && segment is not null; i--)
        {
            parts[i] = segment.Key ?? segment.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            segment = segment.Parent;
        }

        return "/" + string.Join('/', parts);
    }
}
