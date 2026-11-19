namespace Harbor.Ui.Framework.ViewModels;

/// <summary>
///     #2682 Z01: command-palette query history with prefix-scoped arrow
///     navigation. Pure and UI-free so the behaviour can be pinned without an
///     Avalonia event loop.
///     <para>
///         Enter records the query the user typed. Up walks toward older
///         queries that start with that same prefix; Down walks back toward
///         newer ones and finally restores the text the user typed. Editing
///         the query by hand drops any in-flight navigation (Z10), so Up/Down
///         can never overwrite text the user just typed.
///     </para>
/// </summary>
public sealed class QueryHistory
{
    private readonly List<string> _entries = new();
    private string? _typedQuery;
    private List<string>? _matches;
    private int _cursor = -1;

    /// <summary>Committed queries, oldest first.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>True while Up/Down is walking a prefix-scoped match set.</summary>
    public bool IsNavigating => _cursor >= 0;

    /// <summary>
    ///     Record a committed query. The most recent entry goes last; a
    ///     repeated query moves to the end so walking back never shows the
    ///     same text twice. Empty queries are ignored.
    /// </summary>
    public void Add(string query)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return;
        }

        if (_entries.Count > 0 && string.Equals(_entries[^1], q, StringComparison.Ordinal))
        {
            ResetNavigation();
            return;
        }

        _entries.RemoveAll(e => string.Equals(e, q, StringComparison.Ordinal));
        _entries.Add(q);
        ResetNavigation();
    }

    /// <summary>
    ///     #2682 Z10: forget an in-flight navigation. Called when the query
    ///     box changes because the user typed, not because Up/Down set it.
    /// </summary>
    public void ResetNavigation()
    {
        _typedQuery = null;
        _matches = null;
        _cursor = -1;
    }

    /// <summary>
    ///     Move toward older queries starting with the typed prefix.
    ///     Returns the query to place in the box, or <see langword="null" />
    ///     when there is nothing to navigate to (the caller then falls back
    ///     to moving the result selection).
    /// </summary>
    public string? MoveBack(string currentQuery)
    {
        BeginIfNeeded(currentQuery);
        if (_matches is null || _matches.Count == 0)
        {
            return null;
        }

        if (_cursor < 0)
        {
            _cursor = 0;
        }
        else if (_cursor < _matches.Count - 1)
        {
            _cursor++;
        }

        return _matches[_cursor];
    }

    /// <summary>
    ///     Move toward newer queries; at the front of the history, restore
    ///     the text the user typed before navigation started and end the
    ///     navigation.
    /// </summary>
    public string? MoveForward(string currentQuery)
    {
        if (_matches is null || _cursor < 0)
        {
            return null;
        }

        if (_cursor > 0)
        {
            _cursor--;
            return _matches[_cursor];
        }

        string? typed = _typedQuery;
        ResetNavigation();
        return typed;
    }

    private void BeginIfNeeded(string currentQuery)
    {
        if (_cursor >= 0)
        {
            return; // prefix already fixed for this navigation session
        }

        string scope = (currentQuery ?? string.Empty).Trim();
        if (scope.Length == 0)
        {
            return; // history navigation needs a typed prefix
        }

        _typedQuery = currentQuery;
        _matches = _entries
            .Where(e => e.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _matches.Reverse(); // most recently used first
    }
}
