// #2436: a frame-local stack of element ids with the hash of every prefix.
// A push folds one step into the parent's cached hash, so the frame never
// rehashes the ancestor path. Tail segments are reused across frames: when a
// parent and a step are unchanged, the segment from the previous frame is
// kept, which makes a repeated tree walk allocation-free after warm-up.
// BCL-only, AOT-compatible, render-thread owned.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// The id path of the element currently being prepared. <see cref="Push(string)"/>
/// and <see cref="Push(int)"/> fold one step into the stored parent hash;
/// <see cref="GlobalId"/> reads the top without recomputing anything.
/// </summary>
/// <remarks>
/// The instance is owned by one render thread. Call <see cref="Clear"/> at the
/// start of a frame and push/pop as the tree is walked. The produced
/// <see cref="ElementId"/> values compare equal to those built with
/// <see cref="ElementId.Child(string)"/> and <see cref="ElementId.Child(int)"/>.
/// </remarks>
internal sealed class ElementIdStack
{
    /// <summary>Inline capacity, matching the small-vector size the port is based on.</summary>
    internal const int DefaultCapacity = 32;

    // Index d holds the hash and the tail segment of the path at depth d.
    // Index 0 is the root: hash 0, no segment.
    private int[] _hashes;
    private ElementPathSegment?[] _tails;
    private int _depth;

    internal ElementIdStack(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _hashes = new int[capacity + 1];
        _tails = new ElementPathSegment?[capacity + 1];
    }

    internal int Depth => _depth;

    /// <summary>The id of the element on top of the stack, or <see cref="ElementId.Root"/>.</summary>
    internal ElementId GlobalId => new(_tails[_depth], _hashes[_depth], _depth);

    /// <summary>Appends a named step.</summary>
    internal void Push(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        int next = _depth + 1;
        EnsureCapacity(next);

        var parent = _tails[_depth];
        var cached = _tails[next];
        var tail = cached is not null
            && ReferenceEquals(cached.Parent, parent)
            && cached.Index == 0
            && string.Equals(cached.Key, key, StringComparison.Ordinal)
                ? cached
                : new ElementPathSegment(parent, key, 0);

        _tails[next] = tail;
        _hashes[next] = ElementId.ExtendHash(_hashes[_depth], key);
        _depth = next;
    }

    /// <summary>Appends an indexed step.</summary>
    internal void Push(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        int next = _depth + 1;
        EnsureCapacity(next);

        var parent = _tails[_depth];
        var cached = _tails[next];
        var tail = cached is not null
            && ReferenceEquals(cached.Parent, parent)
            && cached.Key is null
            && cached.Index == index
                ? cached
                : new ElementPathSegment(parent, null, index);

        _tails[next] = tail;
        _hashes[next] = ElementId.ExtendHash(_hashes[_depth], index);
        _depth = next;
    }

    /// <summary>Removes the top step, keeping its segment for a later frame to reuse.</summary>
    internal void Pop()
    {
        if (_depth == 0)
        {
            throw new InvalidOperationException("The element id stack is empty.");
        }

        _depth--;
    }

    /// <summary>Returns to the root without dropping cached segments or hashes.</summary>
    internal void Clear() => _depth = 0;

    private void EnsureCapacity(int depth)
    {
        if (depth < _hashes.Length)
        {
            return;
        }

        int size = Math.Max(depth + 1, _hashes.Length * 2);
        Array.Resize(ref _hashes, size);
        Array.Resize(ref _tails, size);
    }
}
