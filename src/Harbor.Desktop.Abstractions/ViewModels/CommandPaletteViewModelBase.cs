using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

public abstract partial class CommandPaletteViewModelBase : StoreSubscriberViewModel
{
    protected readonly List<CommandResultViewModel> AllCommands = new();

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
        Select(state => state.IsAgentRunning, v => IsAgentRunning = v);
        Select(state => state.Status, v => Status = v);
    }

    public ObservableCollection<CommandResultViewModel> Results { get; } = new();

    partial void OnQueryChanged(string value) => Refilter(value);

    protected void Refilter(string query)
    {
        Dispatcher.Post(() =>
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
