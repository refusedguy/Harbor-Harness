namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Bounded prompt-history rail behind the composer (readline semantics):
/// Up walks backwards from the newest entry saving the in-flight draft,
/// Down walks forwards and finally restores that draft exactly once.
/// The controller decides WHEN to recall (first-line/last-line gates); this
/// class owns only the walk state, so both single-line and multi-line
/// composers share identical semantics.
///
/// CF-B-005: this rail is also the walk owner for the store path — Up/Down
/// arrive as <c>InputMsg.HistoryUp/Down</c> (see ComposerController) and are
/// mapped onto this walk, so the in-flight draft survives the round-trip.
/// </summary>
public sealed class PromptHistory
{
    /// <summary>
    /// MRU cap: mirrors <c>RecentItemsService</c> (<c>maxItems: 50</c>).
    /// In-memory only — no file persist (see TODO below).
    /// </summary>
    public const int DefaultCapacity = 50;

    // TODO(CF-B-005): file persist like RecentItemsService (~/.harbor/recent.json).
    // Currently the rail is per-session in-memory only; nothing is written to disk.
    private readonly int _capacity;
    private readonly List<string> _entries = [];

    /// <summary>Walk index into <see cref="_entries"/>; -1 means «live draft».</summary>
    private int _index = -1;

    /// <summary>Draft captured on the first Up stroke, restored by the final Down.</summary>
    private string? _savedDraft;

    public PromptHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    /// <summary>Whether a recall walk is in flight (an Up without the final Down yet).</summary>
    public bool IsWalking => _index != -1;

    /// <summary>
    /// Record a submitted prompt: whitespace trimmed, empties dropped,
    /// repeated strokes (resubmits / edit-then-resend) collapse to one slot.
    /// Any active recall walk resets to the live-draft state.
    /// </summary>
    public void Push(string entry)
    {
        Reset();

        var text = entry.Trim();
        if (text.Length == 0 || (_entries.Count > 0 && _entries[^1] == text))
        {
            return;
        }

        _entries.Add(text);
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
        }
    }

    /// <summary>
    ///     Submit choke point for the composer (CF-B-005): records a submitted
    ///     prompt into history. Currently delegates to <see cref="Push" />;
    ///     the future store-wiring hook lives here, not at call sites.
    /// </summary>
    public void PushSubmitted(string entry) => Push(entry);

    /// <summary>
    /// Step one entry back from the current position. On the first call the
    /// supplied draft is captured for <see cref="TryRecallNext" /> restoration.
    /// False at the oldest boundary — caller falls back to caret movement.
    /// </summary>
    public bool TryRecallPrevious(string currentDraft, out string entry)
    {
        entry = string.Empty;
        if (_entries.Count == 0)
        {
            return false;
        }

        if (_index == -1)
        {
            _savedDraft = currentDraft;
            _index = _entries.Count - 1;
            entry = _entries[_index];
            return true;
        }

        if (_index == 0)
        {
            return false;
        }

        entry = _entries[--_index];
        return true;
    }

    /// <summary>
    /// Step one entry forward. When past the newest entry, restores the saved
    /// draft exactly once and ends the walk. False when not walking — the
    /// caller falls back to caret movement.
    /// </summary>
    public bool TryRecallNext(out string entry)
    {
        entry = string.Empty;
        if (_index == -1)
        {
            return false;
        }

        if (_index == _entries.Count - 1)
        {
            entry = _savedDraft ?? string.Empty;
            _savedDraft = null;
            _index = -1;
            return true;
        }

        entry = _entries[++_index];
        return true;
    }

    /// <summary>Abandon an in-flight walk without changing the buffer contents.</summary>
    public void Reset()
    {
        _index = -1;
        _savedDraft = null;
    }
}
