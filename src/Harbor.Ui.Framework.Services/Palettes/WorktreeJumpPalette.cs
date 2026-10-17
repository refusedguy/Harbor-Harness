using Ensure = Harbor.Abstractions.Extensions.Ensure;

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
/// <remarks>
///     <para>
///         <b>O8 steal (#1177, epic #1155):</b> selection math runs through
///         <see cref="PaletteSelectController" /> (the opencode
///         <c>select-controller.ts</c> port): ↑↓ wrap around instead of
///         clamping, and <see cref="Reseed" /> preserves the selection by
///         entry identity across re-seeds (opencode <c>preserveSelection</c>) —
///         a refresh keeps the cursor where it was instead of jumping to the
///         top. Typing a new filter still resets to the top hit (opencode
///         resets on filter change; only the <em>options</em> change preserves).
///     </para>
///     <para>
///         <b>Suggested section</b> (opencode <c>command-palette.tsx</c>): the
///         host hands entry ids that deserve attention
///         (<see cref="Show(IReadOnlyList{WorktreeJumpEntry}, IReadOnlySet{string})" /> —
///         the panel passes working/dirty sessions). On an empty query those
///         rows lead <see cref="Results" /> as <see cref="SuggestedCount" />
///         duplicates under the host's <c>"Suggested"</c> header, then the full
///         list follows — the same duplication opencode renders (suggested
///         copies plus every option). A non-empty query filters flat with no
///         Suggested block, exactly like the source.
///     </para>
/// </remarks>
public sealed class WorktreeJumpPaletteModel
{
    private static readonly HashSet<string> NoSuggested = new(StringComparer.Ordinal);

    private readonly List<WorktreeJumpEntry> _all = new();
    private List<WorktreeJumpEntry> _results = new();
    private IReadOnlySet<string> _suggested = NoSuggested;
    private string _query = string.Empty;
    private int _selected = -1;
    private int _suggestedCount;

    /// <summary>Whether the palette is open.</summary>
    public bool Visible { get; private set; }

    /// <summary>Current filter text.</summary>
    public string Query => _query;

    /// <summary>Filtered/ranked rows (all entries when the query is empty).</summary>
    public IReadOnlyList<WorktreeJumpEntry> Results => _results;

    /// <summary>
    ///     Leading <see cref="Results" /> rows rendered under the host's
    ///     <c>"Suggested"</c> header (<c>0</c> when the query is non-empty or
    ///     nothing was marked suggested).
    /// </summary>
    public int SuggestedCount => _suggestedCount;

    /// <summary>Index into <see cref="Results" /> (<c>-1</c> when empty).</summary>
    public int SelectedIndex => _selected;

    /// <summary>Selected row, or null when hidden or empty.</summary>
    public WorktreeJumpEntry? Selected =>
        Visible && _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    /// <summary>Open the palette over <paramref name="entries" /> (resets query + selection).</summary>
    public void Show(IReadOnlyList<WorktreeJumpEntry> entries)
    {
        Show(entries, null);
    }

    /// <summary>
    ///     Open the palette over <paramref name="entries" /> (resets query +
    ///     selection). <paramref name="suggestedSessionIds" /> marks the rows
    ///     that lead <see cref="Results" /> under the <c>"Suggested"</c> header
    ///     while the query is empty (entries with an empty session id are never
    ///     suggested — there is no session to switch to).
    /// </summary>
    public void Show(IReadOnlyList<WorktreeJumpEntry> entries, IReadOnlySet<string>? suggestedSessionIds)
    {
        Ensure.NotNull(entries);
        _all.Clear();
        _all.AddRange(entries);
        _suggested = suggestedSessionIds ?? NoSuggested;
        _query = string.Empty;
        Visible = true;
        Refilter(preserveSelection: false);
    }

    /// <summary>
    ///     Replace the backing entries without touching the query (the
    ///     <c>r</c> refresh path): the selection follows its entry by identity
    ///     when it survives, else reconciles into range — an empty list still
    ///     parks at <c>-1</c>.
    /// </summary>
    public void Reseed(IReadOnlyList<WorktreeJumpEntry> entries, IReadOnlySet<string>? suggestedSessionIds = null)
    {
        Ensure.NotNull(entries);
        _all.Clear();
        _all.AddRange(entries);
        _suggested = suggestedSessionIds ?? NoSuggested;
        Refilter(preserveSelection: true);
    }

    /// <summary>Close the palette (drops entries, query and selection).</summary>
    public void Hide()
    {
        _all.Clear();
        _results = new();
        _suggested = NoSuggested;
        _query = string.Empty;
        _selected = -1;
        _suggestedCount = 0;
        Visible = false;
    }

    /// <summary>Replace the filter text and re-rank (resets selection to the top hit).</summary>
    public void SetQuery(string? query)
    {
        _query = query ?? string.Empty;
        Refilter(preserveSelection: false);
    }

    /// <summary>Move selection one row up (wraps to the bottom).</summary>
    public void MoveUp()
    {
        if (_results.Count == 0)
        {
            return;
        }

        _selected = PaletteSelectController.MoveSelection(
            _selected, _results.Count, -1, PaletteSelectController.MovePolicy.Wrap);
    }

    /// <summary>Move selection one row down (wraps to the top).</summary>
    public void MoveDown()
    {
        if (_results.Count == 0)
        {
            return;
        }

        _selected = PaletteSelectController.MoveSelection(
            _selected, _results.Count, 1, PaletteSelectController.MovePolicy.Wrap);
    }

    /// <summary>Enter-confirm: the selected row, or null when hidden or empty.</summary>
    public WorktreeJumpEntry? Confirm() => Selected;

    private void Refilter(bool preserveSelection)
    {
        string? keepSession = null;
        string? keepPath = null;
        int previous = _selected;
        if (preserveSelection && previous >= 0 && previous < _results.Count)
        {
            keepSession = _results[previous].SessionId;
            keepPath = _results[previous].Path;
        }

        string q = _query.Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            if (_suggested.Count > 0)
            {
                var merged = new List<WorktreeJumpEntry>(_all.Count + _suggested.Count);
                int leading = 0;
                for (int i = 0; i < _all.Count; i++)
                {
                    var entry = _all[i];
                    if (entry.SessionId.Length > 0 && _suggested.Contains(entry.SessionId))
                    {
                        merged.Add(entry);
                        leading++;
                    }
                }

                _suggestedCount = leading;
                merged.AddRange(_all);
                _results = merged;
            }
            else
            {
                _suggestedCount = 0;
                _results = new List<WorktreeJumpEntry>(_all);
            }
        }
        else
        {
            _suggestedCount = 0;
            _results = _all
                .Select(e => (Entry: e, Score: BestScore(e, q)))
                .Where(p => p.Score >= 0)
                .OrderByDescending(p => p.Score)
                .Select(p => p.Entry)
                .ToList();
        }

        if (_results.Count == 0)
        {
            _selected = -1;
            return;
        }

        if (preserveSelection && keepSession is not null)
        {
            for (int i = 0; i < _results.Count; i++)
            {
                if (_results[i].SessionId == keepSession && _results[i].Path == keepPath)
                {
                    _selected = i;
                    return;
                }
            }

            _selected = PaletteSelectController.ReconcileSelection(previous, _results.Count);
            return;
        }

        _selected = 0;
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
