namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Host-side seed row for the session-tree panel. The host maps it from
///     the session store; the model stays BCL-pure so the human-readable
///     rows, grouping and filter are unit-testable without any session
///     infrastructure.
/// </summary>
/// <param name="SessionId">Stable session id (passed to <c>OpenSessionAsync</c> on confirm).</param>
/// <param name="Title">Human-readable session title (shown first — never the bare hash).</param>
/// <param name="Directory">Absolute working directory (project grouping hint).</param>
/// <param name="Agent">Bound agent name.</param>
/// <param name="Model">Bound model id.</param>
/// <param name="CreatedAt">UTC creation time (deterministic ordering).</param>
/// <param name="UpdatedAt">UTC last-activity time (date grouping).</param>
/// <param name="ParentSessionId">Fork parent id, if any (lineage indent).</param>
/// <param name="IsCurrent">Whether this is the active session.</param>
public sealed record SessionTreeSeed(
    string SessionId,
    string Title,
    string Directory,
    string Agent,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ParentSessionId,
    bool IsCurrent);

/// <summary>
///     One session-tree row: short title first, short id + agent/model +
///     project in the detail line (painted dimmed, second plan). Grouped by
///     date bucket; fork depth indents the title.
/// </summary>
/// <param name="SessionId">Stable session id.</param>
/// <param name="Title">Indented human title (<c>(untitled)</c> fallback).</param>
/// <param name="ShortId">First 8 id chars for the dimmed detail.</param>
/// <param name="Detail">Dimmed second plan: short id, agent/model, project.</param>
/// <param name="Group">Date bucket (<c>1 · Today</c> … <c>4 · Older</c>).</param>
/// <param name="Depth">Fork-lineage depth (title indent level).</param>
/// <param name="IsCurrent">Whether this is the active session.</param>
public sealed record SessionTreeEntry(
    string SessionId,
    string Title,
    string ShortId,
    string Detail,
    string Group,
    int Depth,
    bool IsCurrent)
{
    /// <summary>Single-line row text for tests: title, detail.</summary>
    public string RowText => $"{Title}  {Detail}";
}

/// <summary>
///     Pure session-tree seeding + panel model: fork/branch lineage rendered
///     as human rows (title first, hash dimmed away), grouped by date,
///     fuzzy-filtered by title/id, Enter-confirm hands the session id to the
///     host's <c>OpenSessionAsync</c>. Mirrors
///     <see cref="WorktreeJumpPaletteModel" /> query semantics.
/// </summary>
public static class SessionTreeModel
{
    /// <summary>Max title indent levels (pathological fork chains stay readable).</summary>
    public const int MaxDepth = 6;

    /// <summary>Short-id length in the dimmed detail (same 8 as the switch list).</summary>
    public const int ShortIdLength = 8;

    /// <summary>
    ///     Date bucket for grouping (numbered prefixes keep the palette's
    ///     group-then-title sort chronological). Shared with the sessions
    ///     switch list so both group identically.
    /// </summary>
    public static string DateBucket(DateTimeOffset ts)
    {
        var local = ts.ToLocalTime().Date;
        var today = DateTime.Today;
        if (local == today)
        {
            return "1 · Today";
        }

        if (local == today.AddDays(-1))
        {
            return "2 · Yesterday";
        }

        if (local >= today.AddDays(-7))
        {
            return "3 · Previous 7 days";
        }

        return "4 · Older";
    }

    /// <summary>
    ///     Build human rows from seeds. Sessions keep deterministic order
    ///     (<c>CreatedAt</c>, then id — same as the text forest); fork depth
    ///     indents the title; parent-chain cycles are cut (the looping row is
    ///     kept with a note instead of recursing forever); components outside
    ///     any root chain are appended as roots so no session ever vanishes.
    /// </summary>
    public static IReadOnlyList<SessionTreeEntry> BuildEntries(IReadOnlyList<SessionTreeSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        var byId = new Dictionary<string, SessionTreeSeed>(seeds.Count, StringComparer.Ordinal);
        for (int i = 0; i < seeds.Count; i++)
        {
            byId[seeds[i].SessionId] = seeds[i];
        }

        var children = new Dictionary<string, List<SessionTreeSeed>>(StringComparer.Ordinal);
        var roots = new List<SessionTreeSeed>();
        for (int i = 0; i < seeds.Count; i++)
        {
            var s = seeds[i];
            if (s.ParentSessionId is { } parent && byId.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = new List<SessionTreeSeed>();
                }

                list.Add(s);
            }
            else
            {
                roots.Add(s);
            }
        }

        SortByAge(roots);
        foreach (var list in children.Values)
        {
            SortByAge(list);
        }

        var entries = new List<SessionTreeEntry>(seeds.Count);
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < roots.Count; i++)
        {
            RenderNode(roots[i], 0, string.Empty, entries, children, byId, onPath, visited);
        }

        // Pure-cycle components never hang off a root — append them so every
        // session stays reachable (the cycle note lands in the detail).
        for (int i = 0; i < seeds.Count; i++)
        {
            if (visited.Add(seeds[i].SessionId))
            {
                RenderNode(seeds[i], 0, "cycle — ", entries, children, byId, onPath, visited);
            }
        }

        return entries;
    }

    private static void SortByAge(List<SessionTreeSeed> list) =>
        list.Sort(static (a, b) =>
        {
            int byTime = a.CreatedAt.CompareTo(b.CreatedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.SessionId, b.SessionId);
        });

    private static void RenderNode(
        SessionTreeSeed node,
        int depth,
        string prefixNote,
        List<SessionTreeEntry> entries,
        Dictionary<string, List<SessionTreeSeed>> children,
        Dictionary<string, SessionTreeSeed> byId,
        HashSet<string> onPath,
        HashSet<string> visited)
    {
        if (!onPath.Add(node.SessionId))
        {
            entries.Add(Row(node, depth, prefixNote + "cycle — parent chain loops"));
            return;
        }

        try
        {
            visited.Add(node.SessionId);
            string note = prefixNote;
            if (node.ParentSessionId is { } parent && !byId.ContainsKey(parent))
            {
                note += $"orphan (parent {parent} not found) · ";
            }

            entries.Add(Row(node, depth, note.TrimEnd(' ', '·')));
            if (!children.TryGetValue(node.SessionId, out var kids))
            {
                return;
            }

            for (int i = 0; i < kids.Count; i++)
            {
                RenderNode(kids[i], depth + 1, string.Empty, entries, children, byId, onPath, visited);
            }
        }
        finally
        {
            onPath.Remove(node.SessionId);
        }
    }

    private static SessionTreeEntry Row(SessionTreeSeed node, int depth, string note)
    {
        string title = string.IsNullOrWhiteSpace(node.Title) ? "(untitled)" : node.Title.Trim();
        int indent = Math.Min(depth, MaxDepth);
        string indented = indent == 0 ? title : new string(' ', indent * 2) + title;
        if (node.IsCurrent)
        {
            indented += " ●";
        }

        string shortId = node.SessionId.Length <= ShortIdLength
            ? node.SessionId
            : node.SessionId.Substring(0, ShortIdLength);
        string project = Path.GetFileName(
            node.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(project))
        {
            project = node.Directory;
        }

        string detail = $"{shortId} · {node.Agent}/{node.Model} · {project}";
        if (note.Length > 0)
        {
            detail += $" · {note}";
        }

        return new SessionTreeEntry(
            node.SessionId, indented, shortId, detail, DateBucket(node.UpdatedAt), depth, node.IsCurrent);
    }
}

/// <summary>
///     Pure session-tree panel model: human rows with fuzzy filter, ↑↓
///     selection and Enter-confirm. The host seeds it via
///     <see cref="SessionTreeModel.BuildEntries" /> and switches to
///     <see cref="SessionTreeEntry.SessionId" /> on confirm.
/// </summary>
public sealed class SessionTreePanelModel
{
    private readonly List<SessionTreeEntry> _all = new();
    private List<SessionTreeEntry> _results = new();
    private string _query = string.Empty;
    private int _selected = -1;

    /// <summary>Whether the panel is open.</summary>
    public bool Visible { get; private set; }

    /// <summary>Current filter text.</summary>
    public string Query => _query;

    /// <summary>Filtered rows (all entries when the query is empty).</summary>
    public IReadOnlyList<SessionTreeEntry> Results => _results;

    /// <summary>Index into <see cref="Results" /> (<c>-1</c> when empty).</summary>
    public int SelectedIndex => _selected;

    /// <summary>Selected row, or null when hidden or empty.</summary>
    public SessionTreeEntry? Selected =>
        Visible && _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    /// <summary>Open the panel over prebuilt <paramref name="entries" /> (resets query + selection).</summary>
    public void Show(IReadOnlyList<SessionTreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _all.Clear();
        _all.AddRange(entries);
        _query = string.Empty;
        Visible = true;
        Refilter();
    }

    /// <summary>Close the panel (drops entries, query and selection).</summary>
    public void Hide()
    {
        _all.Clear();
        _results = new();
        _query = string.Empty;
        _selected = -1;
        Visible = false;
    }

    /// <summary>Replace the filter text and re-rank (resets selection to the top hit).</summary>
    public void SetQuery(string? query)
    {
        _query = query ?? string.Empty;
        Refilter();
    }

    /// <summary>Move selection one row up (clamped).</summary>
    public void MoveUp()
    {
        if (_selected > 0)
        {
            _selected--;
        }
    }

    /// <summary>Move selection one row down (clamped).</summary>
    public void MoveDown()
    {
        if (_selected >= 0 && _selected < _results.Count - 1)
        {
            _selected++;
        }
    }

    /// <summary>Enter-confirm: the selected row, or null when hidden or empty.</summary>
    public SessionTreeEntry? Confirm() => Selected;

    private void Refilter()
    {
        string q = _query.Trim();
        if (q.Length == 0)
        {
            _results = new List<SessionTreeEntry>(_all);
        }
        else
        {
            var scored = new List<(SessionTreeEntry Entry, int Score)>(_all.Count);
            for (int i = 0; i < _all.Count; i++)
            {
                var e = _all[i];
                int best = PanelFuzzy.Score(e.Title, q);
                best = Math.Max(best, PanelFuzzy.Score(e.SessionId, q));
                best = Math.Max(best, PanelFuzzy.Score(e.Detail, q));
                if (best >= 0)
                {
                    scored.Add((e, best));
                }
            }

            scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            _results = scored.Select(p => p.Entry).ToList();
        }

        _selected = _results.Count > 0 ? 0 : -1;
    }
}
