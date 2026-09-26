namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     One worktree/session row in the jump palette (KILLER_FEATURES §2.7 Feature 3,
///     Orca <c>WorktreeJumpPalette.tsx</c>). Carries everything a row renders:
///     session identity, human title, worktree path, git branch and a short
///     agent/git status string — plus the preformatted <see cref="RowText" />
///     so every renderer paints identical rows.
/// </summary>
/// <param name="SessionId">Stable session id; the host passes it to <c>ISessionManager.OpenSessionAsync</c> on confirm.</param>
/// <param name="Title">Human-readable session title.</param>
/// <param name="Path">Absolute worktree directory of the session.</param>
/// <param name="Branch">Git branch of the worktree, or null outside a repo.</param>
/// <param name="Status">Short status: agent state plus dirtiness (e.g. <c>"working ●"</c>, <c>"idle"</c>, <c>"error"</c>).</param>
public sealed record WorktreeJumpEntry(
    string SessionId,
    string Title,
    string Path,
    string? Branch,
    string Status)
{
    /// <summary>Branch display text (never empty — <c>"no-branch"</c> outside a repo).</summary>
    public string BranchText => string.IsNullOrWhiteSpace(Branch) ? "no-branch" : Branch;

    /// <summary>Single-line row text: title, branch, status, path.</summary>
    public string RowText => $"{Title}  {BranchText}  {Status}  {Path}";
}

/// <summary>
///     Pure worktree jump-palette model (KILLER_FEATURES §2.7 Feature 3).
///     BCL-only, zero Harbor dependencies, zero rendering — any host
///     (CellForge <c>CommandPaletteView</c>, Spectre overlay, Avalonia flyout)
///     drives it and paints from <see cref="Results" />.
///     Open/close, fuzzy filter, ↑↓ selection and Enter-confirm live here,
///     mirroring <c>CommandPaletteViewModelBase</c> query semantics; the host
///     owns session switching (<c>ISessionManager.OpenSessionAsync</c> with
///     <see cref="WorktreeJumpEntry.SessionId" /> from <see cref="Confirm" />).
/// </summary>
public sealed class WorktreeJumpPaletteModel
{
    private readonly List<WorktreeJumpEntry> _all = new();
    private List<WorktreeJumpEntry> _results = new();
    private string _query = string.Empty;
    private int _selected = -1;

    /// <summary>Whether the palette is open.</summary>
    public bool Visible { get; private set; }

    /// <summary>Current filter text.</summary>
    public string Query => _query;

    /// <summary>Filtered/ranked rows (all entries when the query is empty).</summary>
    public IReadOnlyList<WorktreeJumpEntry> Results => _results;

    /// <summary>Index into <see cref="Results" /> (<c>-1</c> when empty).</summary>
    public int SelectedIndex => _selected;

    /// <summary>Selected row, or null when hidden or empty.</summary>
    public WorktreeJumpEntry? Selected =>
        Visible && _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    /// <summary>Open the palette over <paramref name="entries" /> (resets query + selection).</summary>
    public void Show(IReadOnlyList<WorktreeJumpEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _all.Clear();
        _all.AddRange(entries);
        _query = string.Empty;
        Visible = true;
        Refilter();
    }

    /// <summary>Close the palette (drops entries, query and selection).</summary>
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
    public WorktreeJumpEntry? Confirm() => Selected;

    private void Refilter()
    {
        string q = _query.Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            _results = new List<WorktreeJumpEntry>(_all);
        }
        else
        {
            _results = _all
                .Select(e => (Entry: e, Score: BestScore(e, q)))
                .Where(p => p.Score >= 0)
                .OrderByDescending(p => p.Score)
                .Select(p => p.Entry)
                .ToList();
        }

        _selected = _results.Count > 0 ? 0 : -1;
    }

    private static int BestScore(WorktreeJumpEntry entry, string query)
    {
        int best = FuzzyScore(entry.Title.ToLowerInvariant(), query);
        best = Math.Max(best, FuzzyScore(entry.Path.ToLowerInvariant(), query));
        if (!string.IsNullOrEmpty(entry.Branch))
        {
            best = Math.Max(best, FuzzyScore(entry.Branch.ToLowerInvariant(), query));
        }

        return best;
    }

    // Same semantics as CommandPaletteViewModelBase.FuzzyScore: substring wins
    // (shorter text ranks first), otherwise subsequence match, else -1.
    private static int FuzzyScore(string text, string query)
    {
        if (text.Contains(query, StringComparison.Ordinal))
        {
            return 100 - text.Length;
        }

        int ti = 0, qi = 0, score = 0;
        while (ti < text.Length && qi < query.Length)
        {
            if (text[ti] == query[qi])
            {
                score += 1;
                qi++;
            }

            ti++;
        }

        return qi == query.Length ? score - (text.Length - query.Length) : -1;
    }
}
