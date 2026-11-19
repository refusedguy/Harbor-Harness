// SessionPromptHistory.cs: #2680 F121. Per-conversation prompt history for a
// REPL. The upstream feature keeps at most 100 prompts per conversation and
// remembers at most 50 conversations. This is that storage policy as a
// standalone leaf: keyed by SessionId, bounded in both directions, and free of
// IO, clocks and REPL wiring. A later REPL slice can call it directly without
// this helper touching a product path.
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Bounded, session-scoped prompt history (#2680 F121). Prompts are stored
///     oldest first, so the last element of <see cref="For" /> is the newest.
///     The instance is safe to share across threads.
/// </summary>
/// <remarks>
///     <para>
///         Each session keeps at most <see cref="MaxEntriesPerSession" />
///         prompts; recording an older entry past that bound drops the oldest
///         one. At most <see cref="MaxSessions" /> sessions are remembered. When
///         a new session pushes the count past that bound, the least recently
///         used session is evicted entirely. A read counts as use.
///     </para>
///     <para>
///         Nothing here opens a store or starts a timer. The helper is the data
///         half of F121, the part that makes history conversation-scoped before
///         a REPL renders the up/down navigation.
///     </para>
/// </remarks>
public sealed class SessionPromptHistory
{
    /// <summary>Longest prompt list kept for one session.</summary>
    public const int MaxEntriesPerSession = 100;

    /// <summary>Largest number of sessions kept at once.</summary>
    public const int MaxSessions = 50;

    private readonly object _gate = new();
    private readonly Dictionary<SessionId, List<string>> _entries = new();

    // Front is the most recently used session. Nodes are reused on touch so
    // navigation does not allocate.
    private readonly LinkedList<SessionId> _recency = new();
    private readonly Dictionary<SessionId, LinkedListNode<SessionId>> _recencyNodes = new();

    /// <summary>Number of sessions currently held.</summary>
    public int SessionCount
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>
    ///     Appends <paramref name="prompt" /> to <paramref name="session" />'s
    ///     history. Null, empty and whitespace-only prompts are ignored.
    /// </summary>
    /// <returns><see langword="true" /> when a prompt was recorded.</returns>
    public bool Record(SessionId session, string? prompt)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(prompt)) return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(session, out List<string>? list))
            {
                list = new List<string>(MaxEntriesPerSession);
                _entries.Add(session, list);
                _recencyNodes.Add(session, _recency.AddFirst(session));
            }
            else
            {
                Touch(session);
            }

            list.Add(prompt);
            if (list.Count > MaxEntriesPerSession)
                list.RemoveRange(0, list.Count - MaxEntriesPerSession);

            while (_entries.Count > MaxSessions) EvictLeastRecentlyUsed();
        }

        return true;
    }

    /// <summary>
    ///     Returns a snapshot of <paramref name="session" />'s prompts, oldest
    ///     first, or an empty list when the session is unknown. The snapshot is
    ///     detached from the store, so the caller may keep or mutate it.
    /// </summary>
    public IReadOnlyList<string> For(SessionId session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (!_entries.TryGetValue(session, out List<string>? list))
                return Array.Empty<string>();

            Touch(session);
            return list.ToArray();
        }
    }

    /// <summary>
    ///     Returns the prompt <paramref name="indexFromNewest" /> places back,
    ///     where 0 is the most recent prompt, or <see langword="null" /> when
    ///     the session is unknown or the index is out of range.
    /// </summary>
    public string? At(SessionId session, int indexFromNewest)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (indexFromNewest < 0) return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(session, out List<string>? list)) return null;

            Touch(session);
            int index = list.Count - 1 - indexFromNewest;
            return index >= 0 ? list[index] : null;
        }
    }

    /// <summary>Number of prompts held for <paramref name="session" />.</summary>
    public int Count(SessionId session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate) return _entries.TryGetValue(session, out List<string>? list) ? list.Count : 0;
    }

    /// <summary>
    ///     Forgets one session in full. Returns <see langword="true" /> when
    ///     the session was present.
    /// </summary>
    public bool Remove(SessionId session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (!_entries.Remove(session)) return false;
            RemoveRecencyNode(session);
            return true;
        }
    }

    /// <summary>Forgets every session and prompt.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
            _recencyNodes.Clear();
        }
    }

    private void Touch(SessionId session)
    {
        LinkedListNode<SessionId> node = _recencyNodes[session];
        _recency.Remove(node);
        _recency.AddFirst(node);
    }

    private void EvictLeastRecentlyUsed()
    {
        LinkedListNode<SessionId>? least = _recency.Last;
        if (least is null) return;

        _entries.Remove(least.Value);
        _recencyNodes.Remove(least.Value);
        _recency.RemoveLast();
    }

    private void RemoveRecencyNode(SessionId session)
    {
        if (!_recencyNodes.Remove(session, out LinkedListNode<SessionId>? node)) return;
        _recency.Remove(node);
    }
}
