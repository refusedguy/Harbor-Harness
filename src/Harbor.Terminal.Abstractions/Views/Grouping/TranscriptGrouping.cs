using Harbor.Abstractions.Permissions;
using Harbor.Terminal.Abstractions.ViewModels;

namespace Harbor.Terminal.Abstractions.Views.Grouping;

/// <summary>
///     Transcript group discriminator ([steal/opencode] #1171, epic #1155 —
///     <c>routes/session/grouping/session.ts</c> <c>GroupKind</c>).
/// </summary>
public enum TranscriptGroupKind : byte
{
    /// <summary>Low-verbosity wrapper around a run of tools and thoughts.</summary>
    Activity,

    /// <summary>Adjacent reasoning (thinking) entries.</summary>
    Reasoning,

    /// <summary>Adjacent read-only inspection calls and their results.</summary>
    Exploration,

    /// <summary>Instruction loads (system entries).</summary>
    Instructions,
}

/// <summary>
///     Experimental transcript detail ([steal/opencode] #1171 —
///     <c>Verbosity</c> in <c>routes/session/grouping/session.ts</c>).
///     <see cref="Medium"/> selects the default production rules.
/// </summary>
public enum TranscriptVerbosity : byte
{
    /// <summary>Runs of tools and thoughts collapse into one activity summary.</summary>
    Low,

    /// <summary>Default: reasoning and exploration fold, text stands alone.</summary>
    Medium,

    /// <summary>Same grouping as <see cref="Medium"/>; hosts may expand more.</summary>
    High,
}

/// <summary>
///     One projected transcript row: a standalone entry or a group with
///     completion and permission-pending state ([steal/opencode] #1171 —
///     <c>SessionRow</c> in <c>routes/session/grouping/session.ts</c>).
/// </summary>
public abstract record TranscriptRow
{
    /// <summary>An ungrouped entry (empty grouping path).</summary>
    public sealed record Single(ChatEntry Entry) : TranscriptRow;

    /// <summary>
    ///     A group of adjacent entries. <see cref="PendingToolCallIds"/>
    ///     carries the permission-blocked tool calls parked at the group end
    ///     (see <see cref="TranscriptGrouping.PartitionPending"/>); it is
    ///     empty for reasoning and instruction groups.
    /// </summary>
    public sealed record Group(
        TranscriptGroupKind Kind,
        IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> Children,
        int Size,
        bool Completed,
        IReadOnlyList<string> PendingToolCallIds) : TranscriptRow;
}

/// <summary>
///     Production transcript rules over <see cref="GroupTree"/> ([steal/opencode]
///     #1171 — <c>routes/session/grouping/session.ts</c>). Grouping is a pure
///     function of the entry snapshot: lifecycle and status decisions stay
///     outside, in the view models and the feed engine (which this layer never
///     touches — the virtualized timeline keeps its own budget and geometry).
/// </summary>
public static class TranscriptGrouping
{
    /// <summary>Default production verbosity (opencode <c>defaultVerbosity</c>).</summary>
    public const TranscriptVerbosity DefaultVerbosity = TranscriptVerbosity.Medium;

    /// <summary>
    ///     Grouping path for one entry. Adjacent thoughts group, as do
    ///     read-only inspection calls and their results. Low wraps every run
    ///     of tools and thoughts in one activity summary. The question tool
    ///     always stands alone.
    /// </summary>
    public static IReadOnlyList<TranscriptGroupKind> EntryPath(
        ChatEntry entry,
        TranscriptVerbosity verbosity = DefaultVerbosity)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (IsQuestionTool(entry))
        {
            return [];
        }

        if (string.Equals(entry.Role, "thinking", StringComparison.OrdinalIgnoreCase))
        {
            return verbosity == TranscriptVerbosity.Low
                ? [TranscriptGroupKind.Activity, TranscriptGroupKind.Reasoning]
                : [TranscriptGroupKind.Reasoning];
        }

        if (string.Equals(entry.Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            return verbosity == TranscriptVerbosity.Low
                ? [TranscriptGroupKind.Activity, TranscriptGroupKind.Instructions]
                : [TranscriptGroupKind.Instructions];
        }

        if (IsToolEntry(entry, out string? toolName))
        {
            bool exploration = IsExplorationTool(toolName);
            if (verbosity == TranscriptVerbosity.Low)
            {
                return exploration
                    ? [TranscriptGroupKind.Activity, TranscriptGroupKind.Exploration]
                    : [TranscriptGroupKind.Activity];
            }

            return exploration ? [TranscriptGroupKind.Exploration] : [];
        }

        return [];
    }

    /// <summary>
    ///     Hydrates a fresh entry batch in one pass rather than merging one
    ///     leaf at a time (opencode <c>projectEntries</c>). Every group but the
    ///     trailing one is completed; the trailing group is still live.
    /// </summary>
    public static List<TranscriptRow> ProjectEntries(
        IReadOnlyList<ChatEntry> entries,
        TranscriptVerbosity verbosity = DefaultVerbosity)
    {
        ArgumentNullException.ThrowIfNull(entries);

        IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> nodes =
            GroupTree.GroupEntries(entries, e => EntryPath(e, verbosity));

        var rows = new List<TranscriptRow>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is GroupNode<ChatEntry, TranscriptGroupKind>.Entry leaf)
            {
                rows.Add(new TranscriptRow.Single(leaf.Value));
            }
            else if (nodes[i] is GroupNode<ChatEntry, TranscriptGroupKind>.Group group)
            {
                rows.Add(new TranscriptRow.Group(
                    group.Kind, group.Children, group.Size,
                    Completed: i < nodes.Count - 1,
                    PendingToolCallIds: []));
            }
        }

        return rows;
    }

    /// <summary>
    ///     Production-rules append (opencode <c>append</c>): a solo entry
    ///     completes the previous group and is spliced in; an entry that
    ///     continues the previous group of the same kind merges into it.
    ///     Permission-blocked tools stay parked at the group end — a new entry
    ///     is inserted before that blocked tail, never after it.
    /// </summary>
    public static void Append(
        IList<TranscriptRow> rows,
        ChatEntry entry,
        TranscriptVerbosity verbosity = DefaultVerbosity,
        int index = -1)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(entry);
        if (index < 0)
        {
            index = rows.Count;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, rows.Count);

        IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> nodes =
            GroupTree.GroupEntries([entry], e => EntryPath(e, verbosity));
        GroupNode<ChatEntry, TranscriptGroupKind> node = nodes[0];

        if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Entry)
        {
            CompletePrevious(rows, index);
            rows.Insert(index, new TranscriptRow.Single(entry));
            return;
        }

        var incoming = (GroupNode<ChatEntry, TranscriptGroupKind>.Group)node;
        if (index > 0 && rows[index - 1] is TranscriptRow.Group previous && previous.Kind == incoming.Kind)
        {
            int pending = previous.Kind == TranscriptGroupKind.Exploration
                ? previous.PendingToolCallIds.Count
                : 0;
            var previousNode = new GroupNode<ChatEntry, TranscriptGroupKind>.Group(
                previous.Kind, previous.Children, previous.Size);
            var (left, right) = GroupTree.SplitGroups(
                [previousNode], Math.Max(0, previous.Size - pending));
            IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> merged =
                GroupTree.MergeGroups(GroupTree.MergeGroups(left, [incoming]), right);
            var updated = (GroupNode<ChatEntry, TranscriptGroupKind>.Group)merged[0];
            rows[index - 1] = previous with { Children = updated.Children, Size = updated.Size };
            return;
        }

        CompletePrevious(rows, index);
        rows.Insert(index, new TranscriptRow.Group(
            incoming.Kind, incoming.Children, incoming.Size,
            Completed: false,
            PendingToolCallIds: []));
    }

    /// <summary>Marks the group before <paramref name="index"/> completed (opencode <c>completePrevious</c>).</summary>
    public static void CompletePrevious(IList<TranscriptRow> rows, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (index < 0)
        {
            index = rows.Count;
        }

        if (index > 0 && index <= rows.Count && rows[index - 1] is TranscriptRow.Group previous && !previous.Completed)
        {
            rows[index - 1] = previous with { Completed = true };
        }
    }

    /// <summary>
    ///     Tool-call ids for an existing group, not a flat timeline (opencode
    ///     <c>groupRefs</c>). Pending (permission-blocked) calls are excluded
    ///     unless <paramref name="includePending"/> is set.
    /// </summary>
    public static List<string> GroupRefs(TranscriptRow.Group group, bool includePending = false)
    {
        ArgumentNullException.ThrowIfNull(group);

        HashSet<string> pending = includePending
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(group.PendingToolCallIds, StringComparer.Ordinal);
        var refs = new List<string>();
        Visit(group.Children, refs, pending);
        return refs;
    }

    /// <summary>
    ///     Parks permission-blocked tools at the group end without reordering
    ///     the rest (opencode <c>partitionPending</c>). Exploration groups keep
    ///     a stable partition — admitted or dismissed permissions never shuffle
    ///     settled calls. Activity groups render blocked tools outside the
    ///     summary, also without reordering. Identity is the tool-call id, so
    ///     a later call reusing an id is never hidden by an earlier blocked one.
    /// </summary>
    public static void PartitionPending(IList<TranscriptRow> rows, ISet<string> pending)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(pending);

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is TranscriptRow.Group group
                && (group.Kind == TranscriptGroupKind.Exploration || group.Kind == TranscriptGroupKind.Activity))
            {
                IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> children =
                    PartitionChildren(group, pending);
                IReadOnlyList<string> blocked = GroupRefs(
                    group with { Children = children, PendingToolCallIds = [] }, includePending: true)
                    .Where(pending.Contains).ToArray();
                rows[i] = group with { Children = children, PendingToolCallIds = blocked };
            }
        }
    }

    /// <summary>True when the group subtree already carries this tool call (opencode <c>hasPart</c>).</summary>
    public static bool HasToolCall(IList<TranscriptRow> rows, string toolCallId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(toolCallId);

        foreach (TranscriptRow row in rows)
        {
            if (row is TranscriptRow.Single single
                && string.Equals(single.Entry.ToolCallId, toolCallId, StringComparison.Ordinal))
            {
                return true;
            }

            if (row is TranscriptRow.Group group
                && GroupRefs(group, includePending: true).Contains(toolCallId, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Summary for an activity group's subtree; permission-blocked tools
    ///     are left out (opencode <c>activitySummary</c> — e.g. "2 commands,
    ///     1 edit, 1 thought, 3 reads"). The label counts only finished work:
    ///     blocked calls surface through <see cref="TranscriptActivitySummary.Active"/>
    ///     instead. Once a later row closes the group, its thoughts count as
    ///     finished, as in Medium.
    /// </summary>
    public static TranscriptActivitySummary SummarizeActivity(TranscriptRow.Group group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var pending = new HashSet<string>(group.PendingToolCallIds, StringComparer.Ordinal);
        int commands = 0, edits = 0, thoughts = 0, reads = 0, tools = 0, instructions = 0;
        bool active = !group.Completed || pending.Count > 0;

        foreach (ChatEntry entry in VisitEntries(group.Children))
        {
            if (!string.IsNullOrEmpty(entry.ToolCallId) && pending.Contains(entry.ToolCallId))
            {
                continue;
            }

            if (string.Equals(entry.Role, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                if (group.Completed && !string.IsNullOrWhiteSpace(entry.Content))
                {
                    thoughts++;
                }

                continue;
            }

            if (string.Equals(entry.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                instructions++;
                continue;
            }

            if (IsToolEntry(entry, out string? toolName)
                && ToolCategories.TryClassify(toolName, out ToolCategory category))
            {
                // A call and its result are one item: the result entry only
                // keeps the call company in the group, it never counts twice.
                if (!string.Equals(entry.Role, "tool-result", StringComparison.OrdinalIgnoreCase))
                {
                    switch (category)
                    {
                        case ToolCategory.Exec: commands++; break;
                        case ToolCategory.Write: edits++; break;
                        case ToolCategory.Read:
                        case ToolCategory.Network: reads++; break;
                        default: tools++; break;
                    }
                }

                continue;
            }

            if (IsToolEntry(entry, out _)
                && !string.Equals(entry.Role, "tool-result", StringComparison.OrdinalIgnoreCase))
            {
                tools++;
            }
        }

        var parts = new List<string>(6);
        if (commands > 0)
        {
            parts.Add(Plural(commands, "command"));
        }

        if (edits > 0)
        {
            parts.Add(Plural(edits, "edit"));
        }

        if (thoughts > 0)
        {
            parts.Add(Plural(thoughts, "thought"));
        }

        if (reads > 0)
        {
            parts.Add(Plural(reads, "read"));
        }

        if (tools > 0)
        {
            parts.Add(Plural(tools, "tool"));
        }

        if (instructions > 0)
        {
            parts.Add(Plural(instructions, "instruction"));
        }

        string label = string.Join(", ", parts);
        if (label.Length == 0 && active)
        {
            label = "Working…";
        }

        return new TranscriptActivitySummary(label, active);
    }

    /// <summary>
    ///     True when the entry names the question tool, which always stands
    ///     alone (opencode <c>partPath</c> question rule). A single-name
    ///     lookup — not a tool table — so there is nothing to keep in sync.
    /// </summary>
    public static bool IsQuestionTool(ChatEntry entry) =>
        IsToolEntry(entry, out string? toolName)
        && string.Equals(toolName, "question", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     True for read-only inspection and network-reaching calls (opencode
    ///     <c>explorationTools</c>). Derived from each tool's own safety
    ///     declaration (<see cref="ToolCategory"/>) instead of a
    ///     hand-maintained name set, so a new tool re-points the rule instead
    ///     of being forgotten by it.
    /// </summary>
    public static bool IsExplorationTool(string? toolName) =>
        !string.IsNullOrEmpty(toolName)
        && ToolCategories.TryClassify(toolName, out ToolCategory category)
        && (category == ToolCategory.Read || category == ToolCategory.Network);

    private static bool IsToolEntry(ChatEntry entry, out string? toolName)
    {
        if ((string.Equals(entry.Role, "tool", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Role, "tool-result", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrEmpty(entry.ToolName))
        {
            toolName = entry.ToolName;
            return true;
        }

        toolName = null;
        return false;
    }

    private static IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> PartitionChildren(
        TranscriptRow.Group group,
        ISet<string> pending)
    {
        // Only exploration groups re-partition, and only their direct call
        // children. Activity groups render blocked tools outside the summary
        // without reordering — settled order never shuffles.
        if (group.Kind != TranscriptGroupKind.Exploration
            || group.Children.Any(n => n is not GroupNode<ChatEntry, TranscriptGroupKind>.Entry))
        {
            return group.Children;
        }

        bool Blocked(GroupNode<ChatEntry, TranscriptGroupKind> node) =>
            node is GroupNode<ChatEntry, TranscriptGroupKind>.Entry leaf
            && leaf.Value.Role.Equals("tool", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(leaf.Value.ToolCallId)
            && pending.Contains(leaf.Value.ToolCallId);

        return group.Children.Where(n => !Blocked(n))
            .Concat(group.Children.Where(Blocked)).ToArray();
    }

    private static void Visit(
        IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> nodes,
        List<string> refs,
        HashSet<string> pending)
    {
        foreach (GroupNode<ChatEntry, TranscriptGroupKind> node in nodes)
        {
            if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Group nested)
            {
                Visit(nested.Children, refs, pending);
            }
            else if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Entry leaf
                && !string.IsNullOrEmpty(leaf.Value.ToolCallId)
                && !pending.Contains(leaf.Value.ToolCallId))
            {
                refs.Add(leaf.Value.ToolCallId);
            }
        }
    }

    private static IEnumerable<ChatEntry> VisitEntries(
        IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> nodes)
    {
        foreach (GroupNode<ChatEntry, TranscriptGroupKind> node in nodes)
        {
            if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Group nested)
            {
                foreach (ChatEntry entry in VisitEntries(nested.Children))
                {
                    yield return entry;
                }
            }
            else if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Entry leaf)
            {
                yield return leaf.Value;
            }
        }
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>Low-verbosity activity summary: counts label plus liveness (opencode <c>activitySummary</c> result).</summary>
/// <param name="Label">Finished-work counts, e.g. "2 commands, 1 thought".</param>
/// <param name="Active">Something in the group is still running or blocked.</param>
public sealed record TranscriptActivitySummary(string Label, bool Active);
