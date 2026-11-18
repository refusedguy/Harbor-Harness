// #1627 batch B (adapted): same body as the Chat leaf
// (Harbor.Tui.CellForge/Chat/Widgets/AutocompletePalette.cs, now deleted)
// except the four `Ensure.NotNull(…)` alias hits — the engine is BCL-only (no
// Harbor.Extensions), and `Ensure.NotNull(value, name)` delegates to exactly
// `ArgumentNullException.ThrowIfNull(value, name)` with the caller-expression
// name, so the retype is behavior-identical. `AutocompleteMode` moved beside
// this family (its own file) because <see cref="AutocompleteItem.Mode"/> and
// <see cref="AutocompleteFilter.Filter"/> bind it; the trigger record, the
// trigger scan and the PromptBuffer extensions stay in the chat file they came
// from (KeyGateFamilyRules pins that path).
namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// One autocomplete row (opencode <c>autocomplete.tsx</c> option shape,
/// trimmed to what the palette needs): identity, display text, the mode it
/// belongs to, whether picking it is destructive (two-step confirm) and
/// whether it may be queued while the agent is busy.
/// </summary>
/// <param name="Id">Stable option id — the destructive gate arms on it.</param>
/// <param name="Title">Row text scored by <see cref="FuzzyMatcher" />.</param>
/// <param name="Detail">Subtitle shown under the title.</param>
/// <param name="Mode">Which trigger mode offers this row.</param>
/// <param name="IsDestructive">True when the first commit only arms a confirm.</param>
/// <param name="IsQueueable">True when the row may run while the agent streams.</param>
public sealed record AutocompleteItem(
    string Id,
    string Title,
    string Detail,
    AutocompleteMode Mode,
    bool IsDestructive = false,
    bool IsQueueable = false);

/// <summary>
/// Destructive two-step stolen from opencode's autocomplete
/// (<c>{id, confirm, run}</c>): the first commit on a destructive row arms
/// the confirm state and the host shows "press Enter again to confirm";
/// the second commit on the same id runs it. Non-destructive rows always
/// run immediately and clear any armed state.
/// </summary>
public sealed class AutocompleteDestructiveGate
{
    private string? _armedId;

    /// <summary>Whether a confirm is currently armed.</summary>
    public bool IsArmed => _armedId is not null;

    /// <summary>Id the armed confirm belongs to (empty when disarmed).</summary>
    public string ArmedId => _armedId ?? string.Empty;

    /// <summary>
    /// Commits <paramref name="item" />.
    /// </summary>
    /// <returns>
    /// True when the host should run the row now; false when it should show
    /// the confirm hint and wait for a second commit.
    /// </returns>
    public bool Commit(AutocompleteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsDestructive)
        {
            _armedId = null;
            return true;
        }

        if (string.Equals(_armedId, item.Id, StringComparison.Ordinal))
        {
            _armedId = null;
            return true;
        }

        _armedId = item.Id;
        return false;
    }

    /// <summary>Drops the armed confirm (selection moved away, palette closed).</summary>
    public void Cancel() => _armedId = null;
}

/// <summary>
/// Mode-aware ranking over <see cref="FuzzyMatcher" />: rows outside the
/// active trigger mode never reach the scorer, so a <c>/</c> query cannot
/// surface <c>@</c> references and vice versa. Ties keep catalog order
/// (the fuzzy ranking is stable).
/// </summary>
public static class AutocompleteFilter
{
    /// <summary>
    /// Rows of <paramref name="mode" /> matching <paramref name="query" />,
    /// best score first (all rows in input order when the query is empty).
    /// The result is a thread-local view over <see cref="FuzzyMatcher" />'s
    /// reusable buffer: valid until the next filter on this thread.
    /// </summary>
    public static IReadOnlyList<AutocompleteItem> Filter(
        string query,
        IReadOnlyList<AutocompleteItem> candidates,
        AutocompleteMode mode)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        // The mode check rides along in FuzzyMatcher's scoring pass, so the
        // old mode-scoped List allocation is gone. An empty query keeps input
        // order; a non-empty one is ranked by Score as before.
        return FuzzyMatcher.Filter(
            query,
            candidates,
            static item => item.Title,
            mode,
            static (item, wanted) => item.Mode == wanted);
    }

    /// <summary>
    /// Whether <paramref name="item" /> may be submitted while the agent is
    /// busy (opencode's queueable flag: submit-during-stream goes to the
    /// prompt queue instead of being rejected).
    /// </summary>
    public static bool CanQueueWhileBusy(AutocompleteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsQueueable;
    }
}
