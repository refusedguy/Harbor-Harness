namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Host-side seed row for the providers panel. The host maps it 1:1 from
///     the onboarding provider presets (the same
///     <c>ProviderPresets.All</c> source the setup wizard picks from — no
///     duplicated list) enriched with the live auth status; the model stays
///     BCL-pure.
/// </summary>
/// <param name="Id">Stable lowercase provider id (<c>kilocode</c>, <c>ollama</c>, …).</param>
/// <param name="DisplayName">Human-readable name shown first.</param>
/// <param name="Description">One-line description (filter text, dimmed detail).</param>
/// <param name="RequiresKey">Whether the provider needs an API key.</param>
/// <param name="HasKey">Whether a key resolves (config file or env).</param>
public sealed record ProviderSeed(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresKey,
    bool HasKey);

/// <summary>
///     One providers-panel row: display name first, id + auth status in the
///     detail line (painted dimmed, second plan). Enter on a row opens the
///     key prompt (input frame); local key-less providers run the health
///     check straight away.
/// </summary>
/// <param name="Id">Stable lowercase provider id.</param>
/// <param name="DisplayName">Human-readable name shown first.</param>
/// <param name="Description">One-line description.</param>
/// <param name="RequiresKey">Whether the provider needs an API key.</param>
/// <param name="HasKey">Whether a key resolves (config file or env).</param>
public sealed record ProviderPanelEntry(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresKey,
    bool HasKey)
{
    /// <summary>Palette title: the display name.</summary>
    public string Title => DisplayName;

    /// <summary>Short auth status text.</summary>
    public string AuthText => HasKey ? "✓ key set" : RequiresKey ? "○ no key" : "local · no key needed";

    /// <summary>Palette detail (dimmed): id + auth status.</summary>
    public string Detail => $"{Id} · {AuthText}";

    /// <summary>Palette group: ready providers first, key-less ones second.</summary>
    public string Group => HasKey || !RequiresKey ? "1 · Ready" : "2 · Needs key";

    /// <summary>Single-line row text for tests: title, detail.</summary>
    public string RowText => $"{Title}  {Detail}";
}

/// <summary>
///     Pure providers-panel model: 13 preset rows with auth status, fuzzy
///     filter, ↑↓ selection and Enter-confirm. Mirrors
///     <see cref="WorktreeJumpPaletteModel" /> query semantics; the host owns
///     the key prompt and the health check.
/// </summary>
public sealed class ProviderPanelModel
{
    private readonly List<ProviderPanelEntry> _all = new();
    private List<ProviderPanelEntry> _results = new();
    private string _query = string.Empty;
    private int _selected = -1;

    /// <summary>Whether the panel is open.</summary>
    public bool Visible { get; private set; }

    /// <summary>Current filter text.</summary>
    public string Query => _query;

    /// <summary>Filtered rows (all entries when the query is empty).</summary>
    public IReadOnlyList<ProviderPanelEntry> Results => _results;

    /// <summary>Index into <see cref="Results" /> (<c>-1</c> when empty).</summary>
    public int SelectedIndex => _selected;

    /// <summary>Selected row, or null when hidden or empty.</summary>
    public ProviderPanelEntry? Selected =>
        Visible && _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    /// <summary>Open the panel over <paramref name="seeds" /> (resets query + selection).</summary>
    public void Show(IReadOnlyList<ProviderSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        _all.Clear();
        for (int i = 0; i < seeds.Count; i++)
        {
            var s = seeds[i];
            _all.Add(new ProviderPanelEntry(s.Id, s.DisplayName, s.Description, s.RequiresKey, s.HasKey));
        }

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
    public ProviderPanelEntry? Confirm() => Selected;

    private void Refilter()
    {
        string q = _query.Trim();
        if (q.Length == 0)
        {
            _results = new List<ProviderPanelEntry>(_all);
        }
        else
        {
            var scored = new List<(ProviderPanelEntry Entry, int Score)>(_all.Count);
            for (int i = 0; i < _all.Count; i++)
            {
                var e = _all[i];
                int best = PanelFuzzy.Score(e.DisplayName, q);
                best = Math.Max(best, PanelFuzzy.Score(e.Id, q));
                best = Math.Max(best, PanelFuzzy.Score(e.Description, q));
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
