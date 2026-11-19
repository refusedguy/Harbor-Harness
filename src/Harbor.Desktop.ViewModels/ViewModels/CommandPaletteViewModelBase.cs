using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;

namespace Harbor.Desktop.ViewModels;

public abstract partial class CommandPaletteViewModelBase : StoreSubscriberViewModel
{
    protected readonly List<CommandResultViewModel> AllCommands = new();

    // #3629: history, usage ranking, aliases and field weights are BCL-only
    // policies shared with the CellForge palette. No Avalonia state lives here.
    private readonly CommandPalettePolicies _policies = new();

    // Kept as a protected surface: derived palettes fill it from their command
    // catalog, and their existing code assigns through the indexer.
    protected IDictionary<string, string> CommandAliases => _policies.Aliases;

    private bool _suppressHistoryReset;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    [ObservableProperty]
    private bool _isAgentRunning;

    [ObservableProperty]
    private string _status = string.Empty;

    protected CommandPaletteViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => state.Chat.IsAgentRunning, v => IsAgentRunning = v);
        Select(state => state.Chat.Status, v => Status = v);
    }

    public ObservableCollection<CommandResultViewModel> Results { get; } = new();

    /// <summary>#2682 Z01: queries committed this session, oldest first.</summary>
    public IReadOnlyList<string> RecentQueries => _policies.RecentQueries;

    partial void OnQueryChanged(string value)
    {
        // Z10: a manual edit invalidates any in-flight history walk. The flag
        // is set only while Up/Down itself assigns Query, so programmatic
        // navigation does not reset the prefix it just established.
        if (!_suppressHistoryReset)
        {
            _policies.ResetHistoryNavigation();
        }
        Refilter(value);
    }

    /// <summary>
    ///     #2682 Z01: let Up/Down walk prefix-matching query history. Returns
    ///     true when a history entry was applied to <see cref="Query" />; the
    ///     caller then leaves the result selection alone. With no matching
    ///     history the call is a no-op and the caller moves the selection.
    /// </summary>
    public bool TryNavigateHistory(bool up)
    {
        if (!_policies.TryNavigateHistory(up, Query, out string next))
        {
            return false;
        }

        _suppressHistoryReset = true;
        Query = next;
        _suppressHistoryReset = false;
        return true;
    }

    protected void Refilter(string query)
    {
        Dispatcher.Post(() =>
        {
            Results.Clear();
            string q = _policies.ResolveQuery(query);
            var matches = string.IsNullOrEmpty(q)
                ? AllCommands
                : AllCommands
                    .Where(c => c.Label.ToLowerInvariant().Contains(q) || c.Hint.ToLowerInvariant().Contains(q))
                    .ToList();
            // #2682 Z02: commands the user has already run lead their match
            // group; the weighted fuzzy score breaks ties. OrderByDescending is
            // stable, so never-invoked commands keep their registration order.
            var ranked = matches
                .OrderByDescending(c => _policies.InvocationCount(c.Kind, c.Label))
                .ThenByDescending(c => string.IsNullOrEmpty(q)
                    ? 0d
                    : CommandPalettePolicies.WeightedScore(FuzzyScore, c.Label, c.Hint, c.Kind, q))
                .ToList();
            foreach (var m in ranked)
            {
                Results.Add(m);
            }
            SelectedIndex = Results.Count > 0 ? 0 : -1;
        });
    }

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
            // #2682 Z01: a successful invocation commits the typed query to
            // history. The action's own failure must not pollute history.
            _policies.NoteQuery(Query);
            _policies.NoteInvocation(cmd.Kind, cmd.Label);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Command '{Label}' threw", cmd.Label);
        }
    }

    public void MoveUp()
    {
        if (SelectedIndex > 0)
        {
            SelectedIndex--;
        }
    }

    public void MoveDown()
    {
        if (SelectedIndex < Results.Count - 1)
        {
            SelectedIndex++;
        }
    }

    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }
}
