using Harbor.Terminal.Abstractions.ViewModels;

namespace Harbor.Terminal.Abstractions.Views.Grouping;

/// <summary>
///     Transcript mount budget ([steal/opencode] #1171, epic #1155 —
///     <c>routes/session/mount-budget.ts</c>). Mounting is budgeted by rendered
///     weight, not rows: a collapsed group renders one header however many
///     entries it holds; an expanded or ungrouped one renders its children.
///     With every group collapsed each row costs one — the former row-count
///     budget. The feed engine keeps its own byte budget; this is the
///     transcript-structure budget hosts apply before handing rows to it, so
///     the engine itself is untouched.
/// </summary>
public static class TranscriptMountBudget
{
    /// <summary>
    ///     Rendered weight of one row. <paramref name="isGrouped"/> decides
    ///     whether a kind folds at all; <paramref name="isExpanded"/> takes
    ///     the stable group key (<see cref="GroupKey(TranscriptRow.Group, int)"/>) and kind.
    /// </summary>
    public static int RowWeight(
        TranscriptRow row,
        Func<string, TranscriptGroupKind, bool> isExpanded,
        Func<TranscriptGroupKind, bool> isGrouped)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(isExpanded);
        ArgumentNullException.ThrowIfNull(isGrouped);

        if (row is not TranscriptRow.Group group)
        {
            return 1;
        }

        return GroupWeight(group.Kind, group.Children, 0, isExpanded, isGrouped);
    }

    /// <summary>
    ///     Stable key for a group's disclosure state (opencode
    ///     <c>groupID</c> in <c>routes/session/anchors.ts</c>): the first
    ///     leaf's identity plus kind and nesting level. Derived from content,
    ///     so re-projection of the same snapshot yields the same key.
    /// </summary>
    public static string GroupKey(TranscriptRow.Group group, int level = 0)
    {
        ArgumentNullException.ThrowIfNull(group);

        return $"{FirstLeafKey(group.Children)}:{group.Kind}:{level}";
    }

    /// <summary>
    ///     First row index such that rows [index, end) spend at least
    ///     <paramref name="budget"/> (opencode <c>rowsBefore</c>).
    /// </summary>
    public static int RowsBefore(IReadOnlyList<int> weights, int end, int budget)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, weights.Count);

        int index = end;
        int spent = 0;
        while (index > 0 && spent < budget)
        {
            spent += weights[--index];
        }

        return index;
    }

    /// <summary>
    ///     End row index such that rows [start, index) spend at least
    ///     <paramref name="budget"/> (opencode <c>rowsAfter</c>).
    /// </summary>
    public static int RowsAfter(IReadOnlyList<int> weights, int start, int budget)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, weights.Count);

        int index = start;
        int spent = 0;
        while (index < weights.Count && spent < budget)
        {
            spent += weights[index++];
        }

        return index;
    }

    /// <summary>
    ///     Loads older pages until the oldest row is no longer a group
    ///     (opencode <c>completeGroupBoundary</c> in
    ///     <c>routes/session/rows.ts</c>): a page boundary can cut a group in
    ///     half, which would show a partial summary under a provisional id.
    ///     The loader mutates <paramref name="rows"/> (prepends older rows);
    ///     the loop re-checks the head after every page.
    /// </summary>
    public static async Task CompleteGroupBoundaryAsync(
        IList<TranscriptRow> rows,
        Func<int> messageCount,
        Func<bool> hasMore,
        Func<Task> loadMoreAsync,
        Func<bool> isActive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(messageCount);
        ArgumentNullException.ThrowIfNull(hasMore);
        ArgumentNullException.ThrowIfNull(loadMoreAsync);
        ArgumentNullException.ThrowIfNull(isActive);

        while (isActive()
            && rows.Count > 0
            && rows[0] is TranscriptRow.Group
            && hasMore())
        {
            int before = messageCount();
            await loadMoreAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // A page that adds nothing would otherwise loop forever.
            if (messageCount() == before)
            {
                return;
            }
        }
    }

    private static int GroupWeight(
        TranscriptGroupKind kind,
        IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> children,
        int level,
        Func<string, TranscriptGroupKind, bool> isExpanded,
        Func<TranscriptGroupKind, bool> isGrouped)
    {
        if (!isGrouped(kind))
        {
            int leaves = 0;
            foreach (GroupNode<ChatEntry, TranscriptGroupKind> child in children)
            {
                leaves += child.Size;
            }

            return leaves;
        }

        var probe = new TranscriptRow.Group(kind, children, 0, Completed: true, PendingToolCallIds: []);
        if (!isExpanded(GroupKey(probe, level), kind))
        {
            return 1;
        }

        int total = 1;
        foreach (GroupNode<ChatEntry, TranscriptGroupKind> child in children)
        {
            total += child is GroupNode<ChatEntry, TranscriptGroupKind>.Group nested
                ? GroupWeight(nested.Kind, nested.Children, level + 1, isExpanded, isGrouped)
                : 1;
        }

        return total;
    }

    private static string FirstLeafKey(IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> children)
    {
        foreach (GroupNode<ChatEntry, TranscriptGroupKind> node in children)
        {
            if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Entry leaf)
            {
                return EntryKey(leaf.Value);
            }

            if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Group nested)
            {
                return FirstLeafKey(nested.Children);
            }
        }

        return "empty";
    }

    private static string EntryKey(ChatEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ToolCallId))
        {
            return $"call:{entry.ToolCallId}";
        }

        // Identity for entries without a call id (user/assistant/thinking):
        // FNV-1a over role + content, the same hash the thinking block uses
        // for its content identity.
        const ulong offset = 14695981039346656037ul;
        const ulong prime = 1099511628211ul;
        ulong hash = offset;
        foreach (char c in entry.Role)
        {
            hash = (hash ^ (byte)c) * prime;
        }

        hash = (hash ^ 0xFF) * prime;
        foreach (char c in entry.Content)
        {
            hash = (hash ^ (byte)c) * prime;
        }

        return $"entry:{hash:x}";
    }
}
