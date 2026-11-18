using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Desktop.ViewModels;

public abstract partial class CommandPaletteViewModelBase : StoreSubscriberViewModel
{
    protected readonly List<CommandResultViewModel> AllCommands = new();

    // #2682 Z02: invocation counts for the current session. Keyed by kind +
    // label so a slash command's description changing does not reset its rank.
    private readonly Dictionary<string, int> _invocations = new(StringComparer.Ordinal);

    // #2682 Z03: exact-query aliases. A synonym ("quit") is rewritten to the
    // canonical command text ("/exit") before the normal filter runs, so a
    // user-configured vocabulary can reach a row without duplicating it.
    // Case-insensitive by construction: palette queries are lower-cased.
    protected readonly Dictionary<string, string> CommandAliases = new(StringComparer.OrdinalIgnoreCase);

    // #2681 G85: the filter matches several fields, so ranking has to weigh
    // them instead of scoring the label alone. Label is the primary match,
    // Hint the secondary one (shortcut / identifier), Kind the category.
    private const double LabelWeight = 1.0;
    private const double HintWeight = 0.5;
    private const double KindWeight = 0.3;

    // #2682 Z01: typed queries, scoped by prefix for Up/Down history walking.
    private readonly QueryHistory _history = new();
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
    public IReadOnlyList<string> RecentQueries => _history.Entries;

    partial void OnQueryChanged(string value)
    {
        // Z10: a manual edit invalidates any in-flight history walk. The flag
        // is set only while Up/Down itself assigns Query, so programmatic
        // navigation does not reset the prefix it just established.
        if (!_suppressHistoryReset)
        {
            _history.ResetNavigation();
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
        string? next = up ? _history.MoveBack(Query) : _history.MoveForward(Query);
        if (next is null)
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
            string q = (query ?? string.Empty).Trim().ToLowerInvariant();
            // #2682 Z03: resolve an exact alias before matching. Single-step
            // only: an alias value is a command query, never another alias,
            // so a bad settings entry cannot create a rewrite cycle.
            if (q.Length > 0 && CommandAliases.TryGetValue(q, out string? canonical))
            {
                q = canonical.Trim().ToLowerInvariant();
            }
            var matches = string.IsNullOrEmpty(q)
                ? AllCommands
                : AllCommands
                    .Where(c => c.Label.ToLowerInvariant().Contains(q) || c.Hint.ToLowerInvariant().Contains(q))
                    .ToList();
            // #2682 Z02: commands the user has already run lead their match
            // group; the weighted fuzzy score breaks ties. OrderByDescending is
            // stable, so never-invoked commands keep their registration order.
            var ranked = matches
                .OrderByDescending(c => InvocationCount(c))
                .ThenByDescending(c => string.IsNullOrEmpty(q) ? 0d : WeightedScore(c, q))
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

    /// <summary>
    ///     #2681 G85: best weighted score across the fields a palette row
    ///     exposes. A field that does not match stays at -1 so a weak hint
    ///     cannot outrank a non-matching label; every field that does match
    ///     keeps its own weight instead of collapsing into one label score.
    /// </summary>
    private static double WeightedScore(CommandResultViewModel command, string query) =>
        Math.Max(
            FieldScore(command.Label, query, LabelWeight),
            Math.Max(FieldScore(command.Hint, query, HintWeight), FieldScore(command.Kind, query, KindWeight)));

    private static double FieldScore(string text, string query, double weight)
    {
        int score = FuzzyScore(text.ToLowerInvariant(), query);
        return score < 0 ? -1d : score * weight;
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
            _history.Add(Query);
            NoteInvocation(cmd);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Command '{Label}' threw", cmd.Label);
        }
    }

    /// <summary>
    ///     #2682 Z02: stable identity of a result row for usage counting.
    /// </summary>
    private static string RankKey(CommandResultViewModel command) =>
        string.Concat(command.Kind, ":", command.Label);

    /// <summary>
    ///     #2682 Z02: how often this row was invoked in the current session.
    /// </summary>
    private int InvocationCount(CommandResultViewModel command) =>
        _invocations.TryGetValue(RankKey(command), out var count) ? count : 0;

    /// <summary>
    ///     #2682 Z02: record a successful invocation. A command whose action
    ///     threw is deliberately not counted, so a broken row cannot promote
    ///     itself to the top of the palette.
    /// </summary>
    private void NoteInvocation(CommandResultViewModel command)
    {
        string key = RankKey(command);
        _invocations[key] = InvocationCount(command) + 1;
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
