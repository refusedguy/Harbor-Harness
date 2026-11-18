using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using FrameworkStatusMappers = Harbor.Ui.Framework.Converters.StatusMappers;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// CF-D-002 projector seam: builds <see cref="StatusSeg"/> rows from the
/// shared <see cref="UiState"/> source of truth instead of hand-assembled
/// view-model strings. #488: every cell comes from
/// <see cref="StatusBarFacts"/> — the same derivation
/// <see cref="StatusProjector.ProjectStatusBar"/> packs for the
/// <c>UiStatusBarModel</c> surface — so the two status bars cannot disagree and
/// the row allocates no segment it will not paint. Only <c>elapsed</c> is
/// formatted here (<c>StatusMappers.DurationToText</c>), since the run
/// duration is not part of <see cref="UiState"/>. The spinner
/// (<see cref="SpinnerStrip"/>) and retry (<see cref="RetryCountdown"/>) slots
/// stay tick-/host-driven — this projector only owns their segment placement,
/// not their clocks. Zero cost and zero tokens are hidden (no data ⇒ no
/// segment, never <c>$0.0000</c>). #384: the default-on
/// <c>skills ●N changed</c> freshness pill is a fixed segment between the
/// retry warning and the scroll percentage; a clean snapshot passes
/// <see langword="null" /> and costs the row nothing.
/// </summary>
public static class StatusProjectorPanel
{
    /// <summary>Capacity: chrome, status, agent, retry, skills, scroll, elapsed, tokens, cost.</summary>
    public const int MaxSegments = 9;

    /// <summary>Maps <see cref="UiState.Chat.Status"/> text to the footer machine mode.</summary>
    public static StatusBarMode MapMode(string? status) => status switch
    {
        "running" => StatusBarMode.Running,
        "compacting" => StatusBarMode.Compacting,
        _ => StatusBarMode.Idle,
    };

    /// <summary>Spinner rhythm for a footer mode (null = no spinner slot).</summary>
    public static SpinnerRhythm? MapRhythm(StatusBarMode mode) => mode switch
    {
        StatusBarMode.Running or StatusBarMode.Compacting => SpinnerRhythm.Working,
        StatusBarMode.AwaitingApproval => SpinnerRhythm.Awaiting,
        _ => null,
    };

    /// <summary>Maps a projector span style to a footer accent.</summary>
    public static StatusAccent MapAccent(UiSpanStyle? style) => style switch
    {
        UiSpanStyle.Dim => StatusAccent.Dim,
        UiSpanStyle.Accent => StatusAccent.Accent,
        UiSpanStyle.Danger => StatusAccent.Error,
        UiSpanStyle.Success => StatusAccent.Success,
        _ => StatusAccent.Neutral,
    };

    /// <summary>
    /// Fills <paramref name="workspace"/> left-to-right from the shared cell
    /// declaration; returns segment count.
    /// <para>
    ///     The cells that come from <see cref="UiState" /> are read from
    ///     <see cref="StatusProjector.ProjectStatusBar" /> — the one place a
    ///     status cell is named, with its order and its truncation priority
    ///     (#568). This method used to spell that row out cell by cell instead,
    ///     which is why adding a segment meant editing this file as well as the
    ///     projection, and why the two could disagree: the projection ranked
    ///     scroll last among right-aligned cells while this row put it before
    ///     tokens and cost, so the same session truncated differently depending
    ///     on which backend painted it.
    /// </para>
    /// <para>
    ///     Retry, the freshness pill and run duration stay here: they are driven
    ///     by the host and by the clock, not by the snapshot, so they are not
    ///     cells the projection knows about. They interleave with the projected
    ///     cells at the same two points as before — warnings right after the
    ///     agent cell, elapsed just before the token/cost tail.
    /// </para>
    /// </summary>
    /// <param name="state">Projected UI snapshot (source of truth).</param>
    /// <param name="workspace">Target span (at least <see cref="MaxSegments"/> cells).</param>
    /// <param name="retryLine">Precomputed <see cref="RetryCountdown.Line"/> text; null when no retry is pending.</param>
    /// <param name="elapsed">Optional run duration, formatted via <c>DurationToText</c> (sub-ms hides).</param>
    /// <param name="skills">
    ///   Aggregate skill-freshness pill (#384) from
    ///   <see cref="SkillFreshnessAggregate.Of"/>; null when every installed
    ///   skill is <c>current</c> (no segment is emitted at all).
    /// </param>
    public static int BuildSegments(
        UiState state,
        Span<StatusSeg> workspace,
        string? retryLine = null,
        TimeSpan? elapsed = null,
        SkillFreshnessSummary? skills = null)
    {
        // #488: read the cells, don't rebuild them. The footer used to call
        // StatusProjector.ProjectStatusBar, drop the token/cost pair on the
        // floor, and re-derive them from raw state under a second formatting
        // rule — so it read the cells only to disagree with them.
        //
        // #568: it reads them for real now. The row takes the projected
        // segments and maps each one — style to accent, FixedPriority through —
        // so a cell added in StatusProjector arrives here with no edit to this
        // file. The ImmutableArray the #488 note complained about is one per
        // snapshot, not one per cell: BuildSegments is reached only from
        // StatusPanel.ProjectProjected, which caches the row against the
        // snapshot and rebuilds when the snapshot actually changes.
        var segments = StatusProjector.ProjectStatusBar(state).Segments;

        string? elapsedText = elapsed.HasValue
            ? FrameworkStatusMappers.DurationToText(elapsed.Value)
            : null;
        if (string.IsNullOrEmpty(elapsedText))
        {
            elapsedText = null;
        }

        // One ordering for the whole row, and one accent per cell.
        //
        // Host-driven slots are declared here because the host owns them: the
        // snapshot does not know about a pending retry, a stale skill or how
        // long the run has been going. They are given the same coordinates as
        // every projected cell and placed by the SAME ordering function, so
        // where they land is a number rather than a position in a hand-written
        // sequence. The projected cells around them come from the projection —
        // not from a second reading of the facts.
        //
        // Importance, right-aligned group, painted left to right:
        //   agent 6 › retry 5 › skills 5 › scroll 4 › elapsed 4 › tokens 3 › cost 2
        // The sort is stable, so equal numbers keep declaration order — which
        // is how retry precedes skills, and scroll precedes elapsed, without
        // either pair needing a rank of its own.
        // Cells go straight into the caller's workspace; the List and its
        // backing array are gone. `StatusSeg` carries a string, so it cannot be
        // stack-allocated; only the sort coordinates need scratch space, and
        // those are unmanaged values. At most MaxSegments cells fit a row.
        Span<RowKey> keys = stackalloc RowKey[MaxSegments];
        int count = 0;

        for (int i = 0; i < segments.Count && count < workspace.Length && count < keys.Length; i++)
        {
            UiStatusSegment seg = segments[i];
            AddCell(workspace, keys, ref count, seg, MapAccent(seg.Style));
        }

        if (!string.IsNullOrEmpty(retryLine) && count < workspace.Length && count < keys.Length)
        {
            // Warning is a CellForge accent with no word in the shared style
            // enum: `UiSpanStyle` is closed and every backend maps it, so
            // widening it to express one host's slot would be the wrong trade.
            // The accent belongs to the host that owns the slot; its ORDER and
            // its truncation priority are declared the same way as everyone
            // else's.
            AddCell(workspace, keys, ref count,
                new UiStatusSegment(
                    Text: retryLine!,
                    Align: Alignment.Right,
                    Importance: 5,
                    Style: UiSpanStyle.Default,
                    // #384: a warning-class signal, so fixed priority —
                    // truncation drops the counters before it (same rule as
                    // the freshness pill).
                    FixedPriority: true),
                StatusAccent.Warning);
        }

        if (skills is not null && count < workspace.Length && count < keys.Length)
        {
            AddCell(workspace, keys, ref count,
                new UiStatusSegment(
                    Text: skills.Text,
                    Align: Alignment.Right,
                    Importance: 5,
                    Style: skills.Style,
                    FixedPriority: true),
                MapAccent(skills.Style));
        }

        if (elapsedText is not null && count < workspace.Length && count < keys.Length)
        {
            AddCell(workspace, keys, ref count,
                new UiStatusSegment(
                    Text: elapsedText,
                    Align: Alignment.Right,
                    Importance: 4,
                    Style: UiSpanStyle.Dim),
                StatusAccent.Dim);
        }

        // Sort the row in place with the ONE rule. `StatusSegmentOrdering` sorts
        // `UiStatusSegment`; this is the same three-group comparison over the
        // same coordinates, applied to the cells already in the workspace. A
        // stable insertion pass over a handful of cells allocates nothing and
        // keeps the comparison next to the declaration it implements.
        SortRow(workspace, keys, count);
        return count;
    }

    /// <summary>
    ///     Writes one mapped cell into the workspace and records the sort
    ///     coordinates that <see cref="StatusSeg" /> does not carry.
    /// </summary>
    private static void AddCell(
        Span<StatusSeg> row,
        Span<RowKey> keys,
        ref int count,
        UiStatusSegment seg,
        StatusAccent accent)
    {
        int at = count++;
        row[at] = new StatusSeg(seg.Text, accent, seg.FixedPriority);
        keys[at] = new RowKey(seg.Align, seg.Importance);
    }

    /// <summary>
    ///     Left → center → right, ascending by importance except the right
    ///     group, which descends. Identical to
    ///     <see cref="StatusSegmentOrdering.Ordered" />, over the cells already
    ///     packed into the workspace.
    /// </summary>
    /// <remarks>
    ///     Stable, so equal importances keep declaration order — that is what
    ///     puts retry before skills and scroll before elapsed without either
    ///     pair needing a rank of its own. A status row is a handful of cells,
    ///     so insertion sort is the right algorithm; each swap moves the cell
    ///     and its key together, in place, with no allocation.
    /// </remarks>
    private static void SortRow(Span<StatusSeg> row, Span<RowKey> keys, int count)
    {
        for (int i = 1; i < count; i++)
        {
            StatusSeg cell = row[i];
            RowKey key = keys[i];
            int j = i - 1;
            while (j >= 0 && Compare(keys[j], key) > 0)
            {
                row[j + 1] = row[j];
                keys[j + 1] = keys[j];
                j--;
            }

            row[j + 1] = cell;
            keys[j + 1] = key;
        }
    }

    /// <summary>Negative when <paramref name="a" /> paints before <paramref name="b" />.</summary>
    private static int Compare(in RowKey a, in RowKey b)
    {
        if (a.Align != b.Align)
        {
            return ((int)a.Align).CompareTo((int)b.Align);
        }

        // Left and center ascend; right descends, so the rightmost cell — the
        // one Fit drops first — is the least important.
        return a.Align == Alignment.Right
            ? b.Importance.CompareTo(a.Importance)
            : a.Importance.CompareTo(b.Importance);
    }

    /// <summary>Unmanaged sort coordinates that ride beside each workspace cell.</summary>
    private readonly record struct RowKey(Alignment Align, int Importance);
}
