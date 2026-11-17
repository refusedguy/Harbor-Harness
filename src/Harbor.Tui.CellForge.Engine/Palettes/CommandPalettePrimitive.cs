// Issue #2321: domain-free command-palette primitive (k9s / fzf CTRL-R /
// lazygit / navi pattern, from the TUI primitives research catalog).
//
// The chat layer owns CommandPaletteView (painting, group headers, drill-down
// frames) and CellForgeJumpPalettePanel (sessions, worktrees, git status).
// Both are domain-bound. This type is the primitive underneath them: a command
// catalog, an alias table, fuzzy ranking and input history with older/newer
// navigation. It paints nothing, owns no store, imports no chat/session type,
// and reuses the engine's own FuzzyMatcher. Any host can drive it and render
// the rows its own way.

using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Palettes;

/// <summary>
/// One command the palette can search and run. Domain-free: identity, display
/// text and a handful of alias tokens. The host decides what running it means.
/// </summary>
/// <param name="Id">Stable command id, returned by commit and stored in history.</param>
/// <param name="Title">Human-readable label; the primary fuzzy-search target.</param>
/// <param name="Detail">Optional secondary search text (description, shortcut, group).</param>
/// <param name="Aliases">Optional alias tokens; an exact alias match outranks a title match.</param>
public sealed record PaletteCommand(
    string Id,
    string Title,
    string Detail = "",
    IReadOnlyList<string>? Aliases = null);

/// <summary>One committed palette invocation, kept for older/newer history navigation.</summary>
/// <param name="Query">The query text that was active at commit time.</param>
/// <param name="CommandId">Id of the command that was run.</param>
public sealed record PaletteHistoryEntry(string Query, string CommandId);

/// <summary>One ranked search result.</summary>
/// <param name="Command">The matching command.</param>
/// <param name="Score">Fuzzy score of the source that won (title or alias).</param>
/// <param name="MatchedAlias">The alias that won, or null when the title/detail/id matched.</param>
public sealed record PaletteHit(PaletteCommand Command, int Score, string? MatchedAlias);

/// <summary>
/// A modal command-palette model: fuzzy search over commands, an alias table
/// and input history you can walk forward and back. No rendering, no domain
/// types, no extension axes (#555) — hosts subscribe by reading
/// <see cref="Results"/> / <see cref="Selected"/> and calling <see cref="Commit"/>.
/// </summary>
/// <remarks>
/// History is a list of committed <see cref="PaletteHistoryEntry"/> values,
/// oldest first. The cursor sits at <see cref="HistoryCount"/> while the user
/// edits a fresh query; <see cref="HistoryOlder"/> walks toward the oldest
/// entry and <see cref="HistoryNewer"/> back toward the fresh buffer. Both
/// set the query to the recalled entry's text so the host can paint it.
/// Consecutive identical commits are collapsed, and the list is capped at the
/// capacity passed to the constructor.
/// </remarks>
public sealed class CommandPalettePrimitive
{
    /// <summary>Default maximum number of retained history entries.</summary>
    public const int DefaultHistoryCapacity = 100;

    private const int ExactAliasTier = 2;
    private const int AliasTier = 1;
    private const int TextTier = 0;

    private readonly int _historyCapacity;
    private readonly List<PaletteCommand> _commands = new();
    private readonly Dictionary<string, string> _extraAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliasOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _aliasesByCommand = new(StringComparer.Ordinal);
    private readonly List<PaletteHistoryEntry> _history = new();
    private List<PaletteHit> _results = new();
    private string _query = string.Empty;
    private int _selected = -1;
    private int _historyCursor;

    /// <summary>Create a palette over an optional catalog.</summary>
    /// <param name="commands">Initial commands; null means an empty catalog.</param>
    /// <param name="historyCapacity">Maximum retained history entries; must be positive.</param>
    public CommandPalettePrimitive(IEnumerable<PaletteCommand>? commands = null, int historyCapacity = DefaultHistoryCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyCapacity);
        _historyCapacity = historyCapacity;
        if (commands is not null)
        {
            SetCommands(commands);
        }
    }

    /// <summary>All commands in the catalog, in seed order.</summary>
    public IReadOnlyList<PaletteCommand> Commands => _commands;

    /// <summary>Current filter text.</summary>
    public string Query => _query;

    /// <summary>Current ranked results for <see cref="Query"/>.</summary>
    public IReadOnlyList<PaletteHit> Results => _results;

    /// <summary>Index into <see cref="Results"/>, or -1 when there are no results.</summary>
    public int SelectedIndex => _selected;

    /// <summary>Currently selected hit, or null when there are no results.</summary>
    public PaletteHit? Selected => _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    /// <summary>Explicit aliases (alias to command id), case-insensitive keys.</summary>
    public IReadOnlyDictionary<string, string> Aliases => _extraAliases;

    /// <summary>Committed history entries, oldest first.</summary>
    public IReadOnlyList<PaletteHistoryEntry> History => _history;

    /// <summary>Number of retained history entries.</summary>
    public int HistoryCount => _history.Count;

    /// <summary>
    /// History cursor: an index into <see cref="History"/>, or
    /// <see cref="HistoryCount"/> when editing a fresh query.
    /// </summary>
    public int HistoryIndex => _historyCursor;

    /// <summary>True when <see cref="HistoryOlder"/> can move back.</summary>
    public bool CanGoOlder => _historyCursor > 0;

    /// <summary>True when <see cref="HistoryNewer"/> can move forward to a newer entry or the fresh buffer.</summary>
    public bool CanGoNewer => _historyCursor < _history.Count;

    /// <summary>Replace the catalog. Command-declared aliases are reindexed; results and selection reset.</summary>
    public void SetCommands(IEnumerable<PaletteCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.Clear();
        foreach (var command in commands)
        {
            ArgumentNullException.ThrowIfNull(command);
            _commands.Add(command);
        }

        RebuildAliases();
        SetQuery(_query);
    }

    /// <summary>Add an alias mapping to a command that exists in the catalog. Case-insensitive.</summary>
    /// <returns>False when the alias is blank or the command id is unknown.</returns>
    public bool AddAlias(string alias, string commandId)
    {
        if (string.IsNullOrWhiteSpace(alias) || !ContainsCommand(commandId))
        {
            return false;
        }

        _extraAliases[alias] = commandId;
        RebuildAliases();
        SetQuery(_query);
        return true;
    }

    /// <summary>Remove an explicit alias. Returns false when it was not present.</summary>
    public bool RemoveAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || !_extraAliases.Remove(alias))
        {
            return false;
        }

        RebuildAliases();
        SetQuery(_query);
        return true;
    }

    /// <summary>Resolve a token through the alias table (explicit aliases win over command declarations).</summary>
    public bool TryResolveAlias(string token, out string commandId)
    {
        commandId = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (_extraAliases.TryGetValue(token, out var explicitOwner))
        {
            commandId = explicitOwner;
            return true;
        }

        if (_aliasOwner.TryGetValue(token, out var declaredOwner))
        {
            commandId = declaredOwner;
            return true;
        }

        return false;
    }

    /// <summary>Set the filter text. Recomputes results and resets the selection to the top hit.</summary>
    public void SetQuery(string query)
    {
        _query = query ?? string.Empty;
        Refilter();
    }

    /// <summary>Move the selection by <paramref name="delta"/>, wrapping at both ends. No-op without results.</summary>
    public void Move(int delta)
    {
        if (_results.Count == 0)
        {
            _selected = -1;
            return;
        }

        int count = _results.Count;
        _selected = ((_selected + delta) % count + count) % count;
    }

    /// <summary>
    /// Run the current selection: record it in history and return the command.
    /// Returns null when nothing is selected.
    /// </summary>
    public PaletteCommand? Commit()
    {
        var hit = Selected;
        if (hit is null)
        {
            return null;
        }

        Record(hit.Command.Id, _query.Trim());
        return hit.Command;
    }

    /// <summary>
    /// Append a history entry (used by hosts that run a command without going
    /// through the result list). Collapses consecutive duplicates and evicts
    /// the oldest entry past capacity. The cursor returns to the fresh buffer.
    /// </summary>
    public void Record(string commandId, string? query = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        var entry = new PaletteHistoryEntry(query ?? string.Empty, commandId);
        if (_history.Count > 0 && _history[^1] == entry)
        {
            _historyCursor = _history.Count;
            return;
        }

        _history.Add(entry);
        while (_history.Count > _historyCapacity)
        {
            _history.RemoveAt(0);
        }

        _historyCursor = _history.Count;
    }

    /// <summary>Move to the older history entry and adopt its query. Returns null at the oldest end.</summary>
    public string? HistoryOlder()
    {
        if (!CanGoOlder)
        {
            return null;
        }

        _historyCursor--;
        string query = _history[_historyCursor].Query;
        SetQuery(query);
        return query;
    }

    /// <summary>
    /// Move to the newer history entry and adopt its query. At the newest end
    /// this returns to the empty fresh buffer. Returns null at the fresh end.
    /// </summary>
    public string? HistoryNewer()
    {
        if (!CanGoNewer)
        {
            return null;
        }

        _historyCursor++;
        string query = _historyCursor == _history.Count ? string.Empty : _history[_historyCursor].Query;
        SetQuery(query);
        return query;
    }

    /// <summary>Drop all history entries and reset the cursor to a fresh buffer.</summary>
    public void ClearHistory()
    {
        _history.Clear();
        _historyCursor = 0;
    }

    private bool ContainsCommand(string commandId)
    {
        if (string.IsNullOrEmpty(commandId))
        {
            return false;
        }

        for (int i = 0; i < _commands.Count; i++)
        {
            if (string.Equals(_commands[i].Id, commandId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void RebuildAliases()
    {
        _aliasOwner.Clear();
        _aliasesByCommand.Clear();

        for (int i = 0; i < _commands.Count; i++)
        {
            var command = _commands[i];
            var list = new List<string>();
            var declared = command.Aliases;
            if (declared is not null)
            {
                for (int j = 0; j < declared.Count; j++)
                {
                    string alias = declared[j];
                    if (string.IsNullOrWhiteSpace(alias))
                    {
                        continue;
                    }

                    list.Add(alias);
                    _aliasOwner[alias] = command.Id;
                }
            }

            _aliasesByCommand[command.Id] = list;
        }

        foreach (var (alias, owner) in _extraAliases)
        {
            if (!_aliasesByCommand.TryGetValue(owner, out var list))
            {
                continue;
            }

            if (!list.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(alias);
            }

            _aliasOwner[alias] = owner;
        }
    }

    private void Refilter()
    {
        string query = _query.Trim();
        var next = new List<PaletteHit>(_commands.Count);
        if (query.Length == 0)
        {
            for (int i = 0; i < _commands.Count; i++)
            {
                next.Add(new PaletteHit(_commands[i], 0, null));
            }

            _results = next;
            _selected = _results.Count > 0 ? 0 : -1;
            return;
        }

        var scored = new List<(PaletteHit Hit, int Tier, int Score, int Index)>(_commands.Count);
        for (int i = 0; i < _commands.Count; i++)
        {
            var command = _commands[i];
            string text = command.Title + " " + command.Detail + " " + command.Id;
            int? textScore = FuzzyMatcher.Score(query, text);

            int bestAliasScore = int.MinValue;
            string? bestAlias = null;
            bool exact = false;
            if (_aliasesByCommand.TryGetValue(command.Id, out var aliases))
            {
                for (int j = 0; j < aliases.Count; j++)
                {
                    string alias = aliases[j];
                    int? aliasScore = FuzzyMatcher.Score(query, alias);
                    if (!aliasScore.HasValue)
                    {
                        continue;
                    }

                    bool isExact = string.Equals(query, alias, StringComparison.OrdinalIgnoreCase);
                    if (bestAlias is null || aliasScore.Value > bestAliasScore || (isExact && !exact))
                    {
                        bestAliasScore = aliasScore.Value;
                        bestAlias = alias;
                        exact = isExact;
                    }
                }
            }

            bool aliasWins = bestAlias is not null
                && (exact || !textScore.HasValue || bestAliasScore > textScore.Value);
            if (!textScore.HasValue && !aliasWins)
            {
                continue;
            }

            int score = aliasWins ? bestAliasScore : textScore!.Value;
            int tier = aliasWins ? (exact ? ExactAliasTier : AliasTier) : TextTier;
            var hit = new PaletteHit(command, score, aliasWins ? bestAlias : null);
            scored.Add((hit, tier, score, i));
        }

        scored.Sort(static (a, b) =>
        {
            int byTier = b.Tier.CompareTo(a.Tier);
            if (byTier != 0)
            {
                return byTier;
            }

            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Index.CompareTo(b.Index);
        });

        for (int i = 0; i < scored.Count; i++)
        {
            next.Add(scored[i].Hit);
        }

        _results = next;
        _selected = _results.Count > 0 ? 0 : -1;
    }
}
