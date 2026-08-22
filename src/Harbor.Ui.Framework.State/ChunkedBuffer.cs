using System.Collections.Immutable;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Immutable, append-optimized text accumulator for streaming buffers.
/// </summary>
/// <remarks>
///     <para>
///         Replaces per-delta <c>string + string</c> concatenation in the UI
///         reducers (O(N²) total work and O(N²) allocations across a stream)
///         with an O(1) persistent-stack push per delta. The accumulated text
///         is materialized into a single <see cref="string" /> only at flush
///         points decided by <see cref="StreamingSync" /> — see
///         <see cref="Materialize" />.
///     </para>
///     <para>
///         AOT-safe: plain sealed class over <see cref="ImmutableStack{T}" />,
///         no reflection, no mutation.
///     </para>
/// </remarks>
public sealed class ChunkedBuffer
{
    /// <summary>The shared empty instance.</summary>
    public static readonly ChunkedBuffer Empty = new(ImmutableStack<string>.Empty, 0);

    private ChunkedBuffer(ImmutableStack<string> chunksReversed, int length)
    {
        ChunksReversed = chunksReversed;
        Length = length;
    }

    /// <summary>
    ///     Pending chunks in reverse insertion order (most recent first) so
    ///     <see cref="Append" /> is an O(1) push.
    /// </summary>
    public ImmutableStack<string> ChunksReversed { get; }

    /// <summary>Total number of characters held in <see cref="ChunksReversed" />.</summary>
    public int Length { get; }

    /// <summary>Whether no characters are pending.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>
    ///     Return a snapshot with <paramref name="delta" /> appended. O(1);
    ///     returns <see langword="this" /> when the delta is empty.
    /// </summary>
    public ChunkedBuffer Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return this;
        return new ChunkedBuffer(ChunksReversed.Push(delta), Length + delta.Length);
    }

    /// <summary>
    ///     Concatenate all pending chunks in insertion order into one string.
    ///     Returns <see cref="string.Empty" /> when nothing is pending.
    /// </summary>
    public string Materialize()
    {
        if (Length == 0)
            return string.Empty;

        int count = 0;
        for (ImmutableStack<string> node = ChunksReversed; !node.IsEmpty; node = node.Pop())
            count++;

        var parts = new string[count];
        int index = count;
        for (ImmutableStack<string> node = ChunksReversed; !node.IsEmpty; node = node.Pop())
            parts[--index] = node.Peek();

        return string.Concat(parts);
    }
}
