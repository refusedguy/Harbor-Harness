namespace Harbor.Terminal.Abstractions.Views.Grouping;

/// <summary>
///     A node of the transcript grouping tree ([steal/opencode] #1171, epic
///     #1155 — <c>routes/session/grouping/tree.ts</c>).
/// </summary>
/// <remarks>
///     <para>
///         The tree is rendering-agnostic: entries are opaque, and message/part
///         identity, visibility and live state stay owned by the transcript
///         layer (<see cref="TranscriptGrouping"/>). A group's
///         <see cref="GroupNode{TEntry, TKind}.Group.Size"/> counts descendant
///         leaves, independent of disclosure state.
///     </para>
///     <para>
///         Published trees are immutable snapshots; only freshly constructed
///         nodes are writable while grouping — the same freeze discipline
///         as the TypeScript original.
///     </para>
/// </remarks>
/// <typeparam name="TEntry">Opaque leaf payload.</typeparam>
/// <typeparam name="TKind">Group discriminator (transcript: <see cref="TranscriptGroupKind"/>).</typeparam>
public abstract record GroupNode<TEntry, TKind>(int Size)
    where TKind : notnull
{
    /// <summary>A standalone leaf (an entry whose grouping path is empty).</summary>
    public sealed record Entry(TEntry Value) : GroupNode<TEntry, TKind>(1);

    /// <summary>
    ///     Adjacent entries sharing one path segment. <see cref="Size"/> is the
    ///     descendant leaf count, <em>not</em> <see cref="Children"/> length.
    /// </summary>
    public sealed record Group(
        TKind Kind,
        IReadOnlyList<GroupNode<TEntry, TKind>> Children,
        int LeafCount) : GroupNode<TEntry, TKind>(LeafCount);
}

/// <summary>
///     Generic grouping engine: group adjacent entries by their nesting paths,
///     concatenate disjoint chunks, split at leaf offsets
///     ([steal/opencode] #1171 — <c>routes/session/grouping/tree.ts</c>).
///     Production transcript rules live in <see cref="TranscriptGrouping"/>;
///     lifecycle and status decisions stay outside this engine.
/// </summary>
public static class GroupTree
{
    /// <summary>
    ///     Groups adjacent entries by their configured nesting paths. A path of
    ///     length zero creates a standalone leaf. Only adjacent entries merge —
    ///     a later run of the same kind forms a sibling group.
    /// </summary>
    public static IReadOnlyList<GroupNode<TEntry, TKind>> GroupEntries<TEntry, TKind>(
        IEnumerable<TEntry> entries,
        Func<TEntry, IReadOnlyList<TKind>> path)
        where TKind : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(path);

        var result = new List<BuilderNode<TEntry, TKind>>();
        foreach (TEntry entry in entries)
        {
            AppendEntry(result, entry, path(entry), 0);
        }

        return result.Select(n => n.Freeze()).ToArray();
    }

    /// <summary>
    ///     Concatenates ordered, disjoint chunks, recursively merging compatible
    ///     groups at their seam. Untouched subtrees retain their object
    ///     identity. This is concatenation, not ingestion: callers reconcile
    ///     overlapping pages before merging — equal payloads may be distinct
    ///     entries and are never deduplicated here.
    /// </summary>
    public static IReadOnlyList<GroupNode<TEntry, TKind>> MergeGroups<TEntry, TKind>(
        IReadOnlyList<GroupNode<TEntry, TKind>> left,
        IReadOnlyList<GroupNode<TEntry, TKind>> right)
        where TKind : notnull
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count == 0)
        {
            return right;
        }

        if (right.Count == 0)
        {
            return left;
        }

        GroupNode<TEntry, TKind> a = left[left.Count - 1];
        GroupNode<TEntry, TKind> b = right[0];
        if (a is not GroupNode<TEntry, TKind>.Group ga
            || b is not GroupNode<TEntry, TKind>.Group gb
            || !EqualityComparer<TKind>.Default.Equals(ga.Kind, gb.Kind))
        {
            return left.Concat(right).ToArray();
        }

        var merged = new List<GroupNode<TEntry, TKind>>(left.Count + right.Count);
        for (int i = 0; i < left.Count - 1; i++)
        {
            merged.Add(left[i]);
        }

        merged.Add(new GroupNode<TEntry, TKind>.Group(
            ga.Kind,
            MergeGroups(ga.Children, gb.Children),
            ga.Size + gb.Size));

        for (int i = 1; i < right.Count; i++)
        {
            merged.Add(right[i]);
        }

        return merged.ToArray();
    }

    /// <summary>
    ///     Splits at a depth-first leaf offset. Group headers count as zero;
    ///     cached sizes skip whole subtrees and only ancestors crossing the cut
    ///     are rebuilt. The halves seam-merge back without changing meaning.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="count"/> is negative or exceeds the leaf total.
    /// </exception>
    public static (IReadOnlyList<GroupNode<TEntry, TKind>> Left, IReadOnlyList<GroupNode<TEntry, TKind>> Right) SplitGroups<TEntry, TKind>(
        IReadOnlyList<GroupNode<TEntry, TKind>> nodes,
        int count)
        where TKind : notnull
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Group split requires a non-negative offset.");
        }

        if (count == 0)
        {
            return ([], nodes);
        }

        int offset = 0;
        for (int index = 0; index < nodes.Count; index++)
        {
            GroupNode<TEntry, TKind> node = nodes[index];
            int end = offset + node.Size;
            if (count == end)
            {
                return (nodes.Take(index + 1).ToArray(), nodes.Skip(index + 1).ToArray());
            }

            if (count < end)
            {
                if (node is not GroupNode<TEntry, TKind>.Group group)
                {
                    throw new ArgumentOutOfRangeException(nameof(count), "Cannot split inside an entry.");
                }

                int size = count - offset;
                var (left, right) = SplitGroups(group.Children, size);
                var before = nodes.Take(index).ToList();
                before.Add(new GroupNode<TEntry, TKind>.Group(group.Kind, left, size));
                var after = new List<GroupNode<TEntry, TKind>>
                {
                    new GroupNode<TEntry, TKind>.Group(group.Kind, right, group.Size - size)
                };
                for (int i = index + 1; i < nodes.Count; i++)
                {
                    after.Add(nodes[i]);
                }

                return (before.ToArray(), after.ToArray());
            }

            offset = end;
        }

        throw new ArgumentOutOfRangeException(nameof(count), "Group split exceeds entry count.");
    }

    private static void AppendEntry<TEntry, TKind>(
        List<BuilderNode<TEntry, TKind>> nodes,
        TEntry entry,
        IReadOnlyList<TKind> path,
        int depth)
        where TKind : notnull
    {
        if (depth >= path.Count)
        {
            nodes.Add(new BuilderEntry<TEntry, TKind>(entry));
            return;
        }

        TKind kind = path[depth];
        if (nodes.Count > 0
            && nodes[nodes.Count - 1] is BuilderGroup<TEntry, TKind> previous
            && EqualityComparer<TKind>.Default.Equals(previous.Kind, kind))
        {
            previous.Size++;
            AppendEntry(previous.Children, entry, path, depth + 1);
            return;
        }

        var children = new List<BuilderNode<TEntry, TKind>>();
        AppendEntry(children, entry, path, depth + 1);
        nodes.Add(new BuilderGroup<TEntry, TKind>(kind, children));
    }

    private abstract class BuilderNode<TEntry, TKind>
        where TKind : notnull
    {
        public abstract GroupNode<TEntry, TKind> Freeze();
    }

    private sealed class BuilderEntry<TEntry, TKind>(TEntry entry) : BuilderNode<TEntry, TKind>
        where TKind : notnull
    {
        public override GroupNode<TEntry, TKind> Freeze() =>
            new GroupNode<TEntry, TKind>.Entry(entry);
    }

    private sealed class BuilderGroup<TEntry, TKind>(TKind kind, List<BuilderNode<TEntry, TKind>> children)
        : BuilderNode<TEntry, TKind>
        where TKind : notnull
    {
        public TKind Kind { get; } = kind;

        public List<BuilderNode<TEntry, TKind>> Children { get; } = children;

        public int Size { get; set; } = 1;

        public override GroupNode<TEntry, TKind> Freeze() =>
            new GroupNode<TEntry, TKind>.Group(Kind, Children.Select(c => c.Freeze()).ToArray(), Size);
    }
}
