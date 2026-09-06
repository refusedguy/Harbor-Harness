using System.Collections.Generic;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Quick-switch slots (Kilo pattern): nine fixed slots for recent sessions,
/// resolved via leader chords <c>&lt;leader&gt;1..9</c>. Slot 0 is untouched by
/// <see cref="Push" /> (stable pin), slots 1..8 rotate most-recent-first — the
/// most recent session lands in slot 1. Pure state, host owns session switching.
/// </summary>
public sealed class QuickSwitchSlots
{
    /// <summary>Slot count (chords 1..9). Index 0 = chord "1" … index 8 = chord "9".</summary>
    public const int Count = 9;

    private readonly string?[] _slots = new string?[Count];

    /// <summary>Session id bound to a slot, or null when empty. <paramref name="slot" /> is 1..9.</summary>
    public string? Get(int slot) =>
        slot is >= 1 and <= Count ? _slots[slot - 1] : throw new ArgumentOutOfRangeException(nameof(slot));

    /// <summary>Binds a session id to a slot (1..9). Re-binding replaces.</summary>
    public void Assign(int slot, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (slot is < 1 or > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        _slots[slot - 1] = sessionId;
    }

    /// <summary>Clears a slot (1..9).</summary>
    public void Clear(int slot)
    {
        if (slot is < 1 or > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        _slots[slot - 1] = null;
    }

    /// <summary>
    /// MRU registration: shifts slots 1..8 down one and puts
    /// <paramref name="sessionId" /> in slot 1. The session's previous slot
    /// entry is removed so it never appears twice.
    /// </summary>
    public void Push(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        for (int i = 0; i < Count - 1; i++)
        {
            if (_slots[i] == sessionId)
            {
                for (int j = i; j < Count - 1; j++)
                {
                    _slots[j] = _slots[j + 1];
                }

                _slots[Count - 1] = null;
                break;
            }
        }

        for (int i = Count - 1; i > 0; i--)
        {
            _slots[i] = _slots[i - 1];
        }

        _slots[0] = sessionId;
    }

    /// <summary>
    ///     Resolves a leader chord digit ('1'..'9') to the bound session id —
    ///     null when the slot is empty or the chord is out of range.
    /// </summary>
    public string? Resolve(char chord) =>
        chord is >= '1' and <= '9' ? _slots[chord - '1'] : null;

    /// <summary>
    ///     Resolves the bound session ids to <see cref="SessionRowViewModel" />
    ///     rows from the provided session list. Empty slots are skipped; the
    ///     returned list preserves slot order (1 → 9).
    /// </summary>
    public List<SessionRowViewModel> ResolveRows(IReadOnlyList<SessionRowViewModel> all)
    {
        var result = new List<SessionRowViewModel>(Count);
        for (int i = 0; i < Count; i++)
        {
            var id = _slots[i];
            if (string.IsNullOrEmpty(id)) continue;
            for (int j = 0; j < all.Count; j++)
            {
                if (all[j].Id == id)
                {
                    result.Add(all[j]);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    ///     Rebuilds slot bindings from the store's session list. Slot 0 is pinned
    ///     to the active session (when present); slots 1..8 are filled
    ///     most-recent-first from the remaining sessions, up to 8 entries.
    /// </summary>
    public void SyncFromStore(UiState state)
    {
        Array.Clear(_slots, 0, Count);

        if (state.ActiveSessionId is { } activeId)
        {
            _slots[0] = activeId.Value;
        }

        int slot = 1;
        for (int i = state.Sessions.Length - 1; i >= 0 && slot < Count; i--)
        {
            var sid = state.Sessions[i].SessionId;
            if (sid != state.ActiveSessionId)
            {
                _slots[slot++] = sid.Value;
            }
        }
    }
}
