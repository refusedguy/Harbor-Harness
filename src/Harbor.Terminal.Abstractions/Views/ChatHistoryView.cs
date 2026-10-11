using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views.Grouping;
namespace Harbor.Terminal.Abstractions.Views;
/// <summary>
///     Builtin chat history view — renders <see cref="ChatHistoryViewModel" /> state: accumulated
///     <see cref="ChatEntry" /> records plus optional live streaming text and thinking buffer.
/// </summary>
/// <remarks>
///     <para>
///         Each entry is rendered with a role-colored prefix (<c>[user]</c>, <c>[assistant]</c>,
///         <c>[tool]</c>, <c>[result]</c>). When <see cref="ChatHistoryViewModel.IsStreaming" /> is
///         active, the in-progress <see cref="ChatHistoryViewModel.StreamingText" /> is appended as a
///         trailing assistant entry. When <see cref="ChatHistoryViewModel.IsThinking" /> is active,
///         the thinking buffer is rendered dimmed/italic so users can follow reasoning.
///     </para>
///     <para>
///         Adjacent reasoning and read-only inspection entries fold into group
///         headers ([steal/opencode] #1171, epic #1155): the snapshot is
///         projected through <see cref="TranscriptGrouping"/> (the same
///         production rules as opencode <c>grouping/session.ts</c>) and headers
///         are additive — every entry still renders exactly once, so the
///         stream shape below is unchanged. <see cref="Verbosity"/> Low
///         collapses tool/thought runs into one activity summary;
///         <see cref="MountBudget"/> caps mounted weight (a collapsed group
///         costs 1) from the live tail.
///     </para>
///     <para>
///         This view is renderer-agnostic and writes only through <see cref="ITuiRenderContext" />.
///     </para>
/// </remarks>
public sealed class ChatHistoryView : TuiViewBase<ChatHistoryViewModel>
{
    /// <inheritdoc />
    public override string Id => "chat-history";

    /// <inheritdoc />
    public override string DisplayName => "Chat History";

    /// <inheritdoc />
    public override TuiViewPlacement Placement => TuiViewPlacement.ChatHistory;

    /// <summary>
    ///     Transcript detail (opencode <c>Verbosity</c>). Default
    ///     <see cref="TranscriptVerbosity.Medium"/> — the production rules.
    /// </summary>
    public TranscriptVerbosity Verbosity { get; set; } = TranscriptGrouping.DefaultVerbosity;

    /// <summary>
    ///     Mounted weight budget from the live tail (see
    ///     <see cref="TranscriptMountBudget"/>). Default uncapped — renders
    ///     the whole snapshot, as before.
    /// </summary>
    public int MountBudget { get; set; } = int.MaxValue;

    /// <summary>
    ///     Permission-blocked tool-call ids, parked at their group end without
    ///     reordering (opencode <c>partitionPending</c>). Fed by the host
    ///     approval flow; empty by default.
    /// </summary>
    public ISet<string> PendingToolCalls { get; set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    ///     Stable group keys (<see cref="TranscriptMountBudget.GroupKey"/>) the
    ///     host forces collapsed. Low-mode activity groups collapse by
    ///     default; everything else expands unless listed here.
    /// </summary>
    public ISet<string> CollapsedGroups { get; set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
    {
        var vm = this.ViewModel;
        if (vm is null)
        {
            return Task.CompletedTask;
        }

        // ENG12 #284 (TGui snapshot pattern): iterate a copy — the event
        // thread can append entries while this draw pass runs.
        ChatEntry[] snapshot = vm.SnapshotEntries();
        List<TranscriptRow> rows = TranscriptGrouping.ProjectEntries(snapshot, Verbosity);
        TranscriptGrouping.PartitionPending(rows, PendingToolCalls);

        bool IsExpanded(string key, TranscriptGroupKind kind) =>
            !CollapsedGroups.Contains(key)
            && !(Verbosity == TranscriptVerbosity.Low && kind == TranscriptGroupKind.Activity);

        int[] weights = new int[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            weights[i] = TranscriptMountBudget.RowWeight(rows[i], IsExpanded, static _ => true);
        }

        // Mount window from the live tail by weight; whole rows only, so a
        // group is never cut in half (opencode completeGroupBoundary intent
        // for hosts without a page loader).
        int start = MountBudget == int.MaxValue
            ? 0
            : TranscriptMountBudget.RowsBefore(weights, rows.Count, MountBudget);

        for (int i = start; i < rows.Count; i++)
        {
            RenderRow(context, rows[i], 0, Verbosity, CollapsedGroups);
        }

        // Render the live streaming text (if any) as a trailing assistant entry. This is
        // primarily useful in full-screen TUIs that repaint the whole pane; streaming
        // renderers (Ansi/Plain) typically emit tokens directly and skip placement-driven
        // repaints for MessageUpdateEvent.
        if (vm.IsStreaming && !string.IsNullOrEmpty(vm.StreamingText))
        {
            RenderEntry(context, "assistant", vm.StreamingText);
        }

        if (vm.IsThinking && !string.IsNullOrEmpty(vm.ThinkingText))
        {
            if (context.SupportsColor)
            {
                context.WriteStyled($"[thinking] {vm.ThinkingText}", TuiStyle.Dim | TuiStyle.Italic);
            }
            else
            {
                context.Write($"[thinking] {vm.ThinkingText}");
            }
            context.WriteLine();
        }

        return Task.CompletedTask;
    }

    private static void RenderRow(
        ITuiRenderContext context,
        TranscriptRow row,
        int level,
        TranscriptVerbosity verbosity,
        ISet<string> collapsed)
    {
        if (row is TranscriptRow.Single single)
        {
            RenderEntry(context, single.Entry.Role, single.Entry.Content);
            return;
        }

        var group = (TranscriptRow.Group)row;
        string key = TranscriptMountBudget.GroupKey(group, level);
        bool expanded = !collapsed.Contains(key)
            && !(verbosity == TranscriptVerbosity.Low && group.Kind == TranscriptGroupKind.Activity);

        RenderHeader(context, group);

        if (expanded)
        {
            RenderChildren(context, group, level, verbosity, collapsed);
        }
        else
        {
            // Blocked tools render outside the summary — approval attention
            // must never hide inside a collapsed header.
            var pending = new HashSet<string>(group.PendingToolCallIds, StringComparer.Ordinal);
            foreach (ChatEntry entry in TranscriptGroupingEntries(group.Children))
            {
                if (!string.IsNullOrEmpty(entry.ToolCallId) && pending.Contains(entry.ToolCallId))
                {
                    RenderEntry(context, entry.Role, entry.Content);
                }
            }
        }
    }

    private static void RenderChildren(
        ITuiRenderContext context,
        TranscriptRow.Group group,
        int level,
        TranscriptVerbosity verbosity,
        ISet<string> collapsed)
    {
        foreach (GroupNode<ChatEntry, TranscriptGroupKind> child in group.Children)
        {
            if (child is GroupNode<ChatEntry, TranscriptGroupKind>.Entry leaf)
            {
                RenderEntry(context, leaf.Value.Role, leaf.Value.Content);
            }
            else if (child is GroupNode<ChatEntry, TranscriptGroupKind>.Group nested)
            {
                RenderRow(context, new TranscriptRow.Group(
                    nested.Kind, nested.Children, nested.Size,
                    Completed: group.Completed, PendingToolCallIds: group.PendingToolCallIds), level + 1, verbosity, collapsed);
            }
        }
    }

    private static void RenderHeader(ITuiRenderContext context, TranscriptRow.Group group)
    {
        string prefix = group.Kind switch
        {
            TranscriptGroupKind.Reasoning => "[thinking] ",
            TranscriptGroupKind.Exploration => "[tool] ",
            TranscriptGroupKind.Activity => "[activity] ",
            TranscriptGroupKind.Instructions => "[system] ",
            _ => "[group] ",
        };
        TranscriptActivitySummary summary = TranscriptGrouping.SummarizeActivity(group);
        string line = $"{prefix}{summary.Label}";

        if (context.SupportsColor)
        {
            context.WriteStyled(line, TuiStyle.Dim);
        }
        else
        {
            context.Write(line);
        }
        context.WriteLine();
    }

    private static IEnumerable<ChatEntry> TranscriptGroupingEntries(
        IReadOnlyList<GroupNode<ChatEntry, TranscriptGroupKind>> nodes)
    {
        foreach (GroupNode<ChatEntry, TranscriptGroupKind> node in nodes)
        {
            if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Entry leaf)
            {
                yield return leaf.Value;
            }
            else if (node is GroupNode<ChatEntry, TranscriptGroupKind>.Group nested)
            {
                foreach (ChatEntry entry in TranscriptGroupingEntries(nested.Children))
                {
                    yield return entry;
                }
            }
        }
    }

    private static void RenderEntry(ITuiRenderContext context, string role, string content)
    {
        string prefix = role switch
        {
            "user" => "[user] ",
            "assistant" => "[assistant] ",
            "tool" => "[tool] ",
            "tool-result" => "[result] ",
            "thinking" => "[thinking] ",
            "system" => "",
            "error" => "[error] ",
            _ => $"[{role}] "
        };

        if (string.Equals(role, "thinking", StringComparison.OrdinalIgnoreCase))
        {
            if (context.SupportsColor)
            {
                context.WriteStyled($"[thinking] {content}", TuiStyle.Dim | TuiStyle.Italic);
            }
            else
            {
                context.Write(prefix);
                context.Write(content);
            }

            context.WriteLine();
            return;
        }

        if (context.SupportsColor)
        {
            var color = role switch
            {
                "user" => TuiColor.Green,
                "assistant" => TuiColor.Cyan,
                "tool" => TuiColor.Blue,
                "tool-result" => TuiColor.Gray,
                "system" => TuiColor.Yellow,
                "error" => TuiColor.Red,
                _ => TuiColor.Default
            };
            context.WriteColored(prefix, color);
        }
        else
        {
            context.Write(prefix);
        }

        context.WriteLine(content);
    }
}
