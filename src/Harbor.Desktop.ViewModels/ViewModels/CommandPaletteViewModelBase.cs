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

    partial void OnQueryChanged(string value) => Refilter(value);

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
            // group; the fuzzy score breaks ties. OrderByDescending is stable,
            // so never-invoked commands keep their registration order.
            var ranked = matches
                .OrderByDescending(c => InvocationCount(c))
                .ThenByDescending(c => string.IsNullOrEmpty(q) ? 0 : FuzzyScore(c.Label.ToLowerInvariant(), q))
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
