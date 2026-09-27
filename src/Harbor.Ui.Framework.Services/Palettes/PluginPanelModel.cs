namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Plugin-panel status. The host derives it from the installed files on
///     disk overlaid with the last reload result: a file that the loader
///     bound this session is <see cref="Loaded" />; a <c>.cs.disabled</c>
///     file is <see cref="Disabled" /> (the <c>*.cs</c> glob skips it, so it
///     never loads); anything else present on disk is <see cref="Installed" />
///     (needs a reload, a restart, or a trust approval to bind).
/// </summary>
public enum PluginPanelStatus
{
    /// <summary>Bound by the plugin loader this session.</summary>
    Loaded,

    /// <summary>On disk, never bound (new, trust-skipped, or edited since load).</summary>
    Installed,

    /// <summary>Renamed to <c>.cs.disabled</c> — the loader skips it entirely.</summary>
    Disabled,
}

/// <summary>
///     Host-side seed row for the plugins panel. The host maps it from the
///     installed <c>*.cs</c> / <c>*.cs.disabled</c> files in both plugin
///     scopes plus the last reload result; the model stays BCL-pure so the
///     list, filter and toggle UX are unit-testable without any plugin
///     infrastructure.
/// </summary>
/// <param name="Name">Display file name (<c>Alpha.cs</c>, never the <c>.disabled</c> suffix).</param>
/// <param name="Version">Loaded version, or null when never bound this session.</param>
/// <param name="Scope">Install scope (<c>global</c> / <c>project</c>).</param>
/// <param name="FullPath">Absolute source path (toggle target).</param>
/// <param name="Enabled">False for <c>.cs.disabled</c> files.</param>
/// <param name="Loaded">Whether the loader bound this file this session.</param>
public sealed record PluginSeed(
    string Name,
    string? Version,
    string Scope,
    string FullPath,
    bool Enabled,
    bool Loaded);

/// <summary>
///     One plugins-panel row: installed name first, version/scope/status in
///     the detail line (painted dimmed, second plan). The host maps it to a
///     palette item 1:1 (title/detail/group) and applies Enter-toggles by
///     renaming the source file.
/// </summary>
/// <param name="Name">Display file name.</param>
/// <param name="Version">Loaded version, or null when never bound.</param>
/// <param name="Scope">Install scope (<c>global</c> / <c>project</c>).</param>
/// <param name="FullPath">Absolute source path (toggle target).</param>
/// <param name="Enabled">False for <c>.cs.disabled</c> files.</param>
/// <param name="Loaded">Whether the loader bound this file this session.</param>
public sealed record PluginPanelEntry(
    string Name,
    string? Version,
    string Scope,
    string FullPath,
    bool Enabled,
    bool Loaded)
{
    /// <summary>Derived status: disabled wins over loaded (a renamed file never loads).</summary>
    public PluginPanelStatus Status => !Enabled
        ? PluginPanelStatus.Disabled
        : Loaded ? PluginPanelStatus.Loaded : PluginPanelStatus.Installed;

    /// <summary>Palette title: the file name.</summary>
    public string Title => Name;

    /// <summary>Palette detail (dimmed): version, scope, status.</summary>
    public string Detail
    {
        get
        {
            string version = string.IsNullOrEmpty(Version) ? "unversioned" : "v" + Version;
            string status = Status switch
            {
                PluginPanelStatus.Loaded => "loaded",
                PluginPanelStatus.Disabled => "disabled",
                _ => "installed",
            };
            return $"{version} · {Scope} · {status}";
        }
    }

    /// <summary>Palette group: the install scope.</summary>
    public string Group => Scope;

    /// <summary>Single-line row text for tests: title, detail.</summary>
    public string RowText => $"{Title}  {Detail}";
}

/// <summary>
///     Pure plugins-panel model: installed-plugin list with fuzzy filter,
///     ↑↓ selection and Enter-confirm. Mirrors
///     <see cref="WorktreeJumpPaletteModel" /> query semantics; the host owns
///     reloading and the enable/disable file rename and reseeds via
///     <see cref="Show" /> afterwards (restart fully rebinds).
/// </summary>
public sealed class PluginPanelModel
{
    private readonly List<PluginPanelEntry> _all = new();
    private List<PluginPanelEntry> _results = new();
    private string _query = string.Empty;
    private int _selected = -1;

    /// <summary>Whether the panel is open.</summary>
    public bool Visible { get; private set; }

    /// <summary>Current filter text.</summary>
    public string Query => _query;

    /// <summary>Filtered rows (all entries when the query is empty).</summary>
    public IReadOnlyList<PluginPanelEntry> Results => _results;

    /// <summary>Index into <see cref="Results" /> (<c>-1</c> when empty).</summary>
    public int SelectedIndex => _selected;

    /// <summary>Selected row, or null when hidden or empty.</summary>
    public PluginPanelEntry? Selected =>
        Visible && _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    /// <summary>Open the panel over <paramref name="seeds" /> (resets query + selection).</summary>
    public void Show(IReadOnlyList<PluginSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        _all.Clear();
        for (int i = 0; i < seeds.Count; i++)
        {
            var s = seeds[i];
            _all.Add(new PluginPanelEntry(s.Name, s.Version, s.Scope, s.FullPath, s.Enabled, s.Loaded));
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
    public PluginPanelEntry? Confirm() => Selected;

    private void Refilter()
    {
        string q = _query.Trim();
        if (q.Length == 0)
        {
            _results = new List<PluginPanelEntry>(_all);
        }
        else
        {
            var scored = new List<(PluginPanelEntry Entry, int Score)>(_all.Count);
            for (int i = 0; i < _all.Count; i++)
            {
                var e = _all[i];
                int best = PanelFuzzy.Score(e.Name, q);
                best = Math.Max(best, PanelFuzzy.Score(e.Scope, q));
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
