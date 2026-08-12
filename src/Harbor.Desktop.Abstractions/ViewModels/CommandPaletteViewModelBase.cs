using Harbor.Ui.Framework.Services;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the command-palette (cmdk-style) view-model. Holds the
///     unfiltered command list, the visible results, the query, and the
///     selection; platform VMs add the actual command callbacks (which
///     bounce through the shell VM).
/// </summary>
/// <remarks>
///     The fuzzy filter runs through <see cref="Refilter" /> which marshals
///     to the UI thread via the dispatcher passed to the constructor — the
///     <see cref="Results" /> collection is bound and must be mutated on the
///     UI thread on every platform.
/// </remarks>
public abstract partial class CommandPaletteViewModelBase : ViewModelBase
{
    private readonly IDispatcherAdapter _dispatcher;

    /// <summary>All commands (unfiltered), populated by the derived class.</summary>
    protected readonly List<CommandResultViewModel> AllCommands = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>Construct a <see cref="CommandPaletteViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller for the results collection.</param>
    /// <param name="logger">Logger.</param>
    protected CommandPaletteViewModelBase(IDispatcherAdapter dispatcher, ILogger logger) : base(logger)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Visible search results.</summary>
    public ObservableCollection<CommandResultViewModel> Results { get; } = new();

    /// <summary>Recompute results when the query changes.</summary>
    partial void OnQueryChanged(string value) => Refilter(value);

    /// <summary>
    ///     Re-filter <see cref="AllCommands" /> into <see cref="Results" />
    ///     on the UI thread. Called by the query-changed partial and by the
    ///     derived constructor after the initial fill.
    /// </summary>
    protected void Refilter(string query)
    {
        _dispatcher.Post(() =>
        {
            Results.Clear();
            string q = (query ?? string.Empty).Trim().ToLowerInvariant();
            var matches = string.IsNullOrEmpty(q)
                ? AllCommands
                : AllCommands
                    .Where(c => c.Label.ToLowerInvariant().Contains(q) || c.Hint.ToLowerInvariant().Contains(q))
                    .OrderByDescending(c => FuzzyScore(c.Label.ToLowerInvariant(), q))
                    .ToList();
            foreach (var m in matches)
            {
                Results.Add(m);
            }
            SelectedIndex = Results.Count > 0 ? 0 : -1;
        });
    }

    /// <summary>Simple subsequence-match score. Higher = better match.</summary>
    protected static int FuzzyScore(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Run the command at the current <see cref="SelectedIndex" />.</summary>
    public void InvokeSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
        {
            return;
        }
        var cmd = Results[SelectedIndex];
        try
        {
            cmd.Action.Invoke();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Command '{Label}' threw", cmd.Label);
        }
    }

    /// <summary>Move selection up by one.</summary>
    public void MoveUp()
    {
        if (SelectedIndex > 0)
        {
            SelectedIndex--;
        }
    }

    /// <summary>Move selection down by one.</summary>
    public void MoveDown()
    {
        if (SelectedIndex < Results.Count - 1)
        {
            SelectedIndex++;
        }
    }
}

/// <summary>
///     One command-palette result row. Pure record — no UI-framework
///     dependency — so the shape can be reused by Avalonia/WPF/MAUI/Blazor
///     palette views.
/// </summary>
/// <param name="Kind">Category — "command", "slash", "file", or "session".</param>
/// <param name="Label">Primary text shown large.</param>
/// <param name="Hint">Secondary text shown muted (e.g. shortcut or category).</param>
/// <param name="Action">Callback invoked when the user activates this entry.</param>
public sealed record CommandResultViewModel(string Kind, string Label, string Hint, Action Action)
{
    /// <summary>Icon glyph based on kind.</summary>
    public string Icon => Kind switch
    {
        "command" => "⚡",
        "slash" => "/",
        "file" => "📄",
        "session" => "💬",
        _ => "•"
    };
}
