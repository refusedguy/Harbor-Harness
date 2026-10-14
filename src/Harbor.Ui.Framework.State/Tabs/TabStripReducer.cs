using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Panels;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Tab-strip transitions of the chat reducer (#388, #390, #1173): open,
///     activate, close, reorder, pin, reopen and unread — pure folds over
///     <c>Chat.TabStrip</c>, extracted verbatim from
///     <see cref="ChatAppReducer" /> (#95). The host runs the effects; nothing
///     here touches I/O or DI.
/// </summary>
/// <remarks>
///     <see cref="ChatAppReducer" /> keeps a thin forwarder over every public
///     member, so existing call sites, tests and doc references keep resolving
///     to the same signatures with the same behaviour.
/// </remarks>
public static class TabStripReducer
{
    // ── tab transitions (#388, slice 1/3 — pure; the host runs the effect) ──

    /// <summary>
    ///     Open a tab at the right end of the strip and focus it, emitting
    ///     <see cref="TuiEffect.ActivateSession" /> so the host switches session.
    ///     <b>Idempotent:</b> a session that already has a tab is activated
    ///     instead of duplicated, and its position and fields are left alone.
    /// </summary>
    public static ReduceResult OpenTab(UiState state, SessionTab tab)
    {
        var strip = state.Chat.TabStrip;
        if (strip.Contains(tab.SessionId))
            return ActivateTab(state, tab.SessionId);

        var next = state with
        {
            Chat = state.Chat with
            {
                // Selecting a session drops its reopen entry (opencode's
                // reopenSessionTab-by-id rule): it is open again, so the
                // stack must not offer to restore it a second time.
                TabStrip = strip with
                {
                    Tabs = strip.Tabs.Add(tab),
                    ActiveTabId = tab.SessionId,
                    ClosedStack = PruneClosed(strip.ClosedStack, tab.SessionId)
                }
            }
        };
        return new ReduceResult(next, new TuiEffect.ActivateSession(tab.SessionId));
    }

    /// <summary>
    ///     Focus an already-open tab and ask the host to switch to its session.
    ///     Order is untouched (activating never reorders). Focusing also
    ///     acknowledges the tab: viewing is reading, so its unread signal is
    ///     cleared (#1173, opencode's view watermark). Unknown session or an
    ///     already-active clean tab → same state instance and no effect, so the
    ///     store does not bump the revision on a no-op.
    /// </summary>
    public static ReduceResult ActivateTab(UiState state, SessionId sessionId)
    {
        var strip = state.Chat.TabStrip;
        int index = strip.IndexOf(sessionId);
        if (index < 0)
            return ReduceResult.NoOp(state);

        var tab = strip.Tabs[index];
        bool refocus = !SameSession(strip.ActiveTabId, sessionId);
        bool acknowledge = tab.HasUnread;
        if (!refocus && !acknowledge)
            return ReduceResult.NoOp(state);

        var tabs = acknowledge
            ? strip.Tabs.SetItem(index, tab with { IsDirty = false, HasError = false })
            : strip.Tabs;
        var next = state with
        {
            Chat = state.Chat with { TabStrip = strip with { Tabs = tabs, ActiveTabId = sessionId } }
        };
        // An acknowledge-only transition switches nothing — the host is
        // already showing this session — so no activate effect fires.
        TuiEffect effect = refocus ? new TuiEffect.ActivateSession(sessionId) : new TuiEffect.None();
        return new ReduceResult(next, effect);
    }

    /// <summary>
    ///     Close a tab, applying the neighbour rule
    ///     (<see cref="TabNeighbourAfterClose" />) when the active tab was the
    ///     one closed, and the panel-ownership rule
    ///     (<see cref="ReleaseOwnedPanels" />).
    /// </summary>
    public static ReduceResult CloseTab(UiState state, SessionId sessionId)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(sessionId);
        if (index < 0)
            return ReduceResult.NoOp(state);

        var remaining = tabs.RemoveAt(index);
        bool closedActive = SameSession(strip.ActiveTabId, sessionId);

        var released = ReleaseOwnedPanels(state, tabs[index], remaining);
        var activeId = closedActive ? TabNeighbourAfterClose(remaining, index) : strip.ActiveTabId;

        var next = released with
        {
            Chat = released.Chat with
            {
                TabStrip = strip with
                {
                    Tabs = remaining,
                    ActiveTabId = activeId,
                    ClosedStack = PushClosed(strip.ClosedStack, tabs[index], index)
                }
            }
        };
        // The neighbour (if any) is the session the host must now open.
        TuiEffect effect = activeId is { } target && closedActive
            ? new TuiEffect.ActivateSession(target)
            : new TuiEffect.None();
        return new ReduceResult(next, effect);
    }

    /// <summary>
    ///     Close every tab except <paramref name="keep" /> and focus the
    ///     survivor. The survivor owns the focus unconditionally — it is the tab
    ///     the gesture was invoked on, so the strip must not end up pointing at a
    ///     tab that no longer exists. The effect fires only when the active tab
    ///     actually changed.
    /// </summary>
    /// <remarks>
    ///     Pinned tabs survive this gesture (#390): the survivor set is the
    ///     keep target plus every pinned tab, in tab order. An explicit
    ///     <see cref="CloseTab" /> still closes a pinned tab — pin guards bulk
    ///     gestures, not intent.
    /// </remarks>
    public static ReduceResult CloseOtherTabs(UiState state, SessionId keep)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int keepIndex = strip.IndexOf(keep);
        if (keepIndex < 0 || tabs.Length == 1)
            return ReduceResult.NoOp(state);

        var survivors = ImmutableArray.CreateBuilder<SessionTab>(tabs.Length);
        for (int i = 0; i < tabs.Length; i++)
        {
            if (i == keepIndex || tabs[i].IsPinned)
                survivors.Add(tabs[i]);
        }

        var remaining = survivors.ToImmutable();
        if (remaining.Length == tabs.Length)
        {
            // Nothing would close — still honour the focus rule when the keep
            // target is not active, without touching order or panels.
            if (SameSession(strip.ActiveTabId, keep))
                return ReduceResult.NoOp(state);
            var focusOnly = state with
            {
                Chat = state.Chat with { TabStrip = strip with { ActiveTabId = keep } }
            };
            return new ReduceResult(focusOnly, new TuiEffect.ActivateSession(keep));
        }

        var released = state;
        var closedStack = strip.ClosedStack;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (i != keepIndex && !tabs[i].IsPinned)
            {
                released = ReleaseOwnedPanels(released, tabs[i], remaining);
                closedStack = PushClosed(closedStack, tabs[i], i);
            }
        }

        var next = released with
        {
            Chat = released.Chat with
            {
                TabStrip = strip with { Tabs = remaining, ActiveTabId = keep, ClosedStack = closedStack }
            }
        };
        TuiEffect effect = SameSession(strip.ActiveTabId, keep)
            ? new TuiEffect.None()
            : new TuiEffect.ActivateSession(keep);
        return new ReduceResult(next, effect);
    }

    /// <summary>
    ///     Close every tab strictly to the right of <paramref name="from" /> and
    ///     focus <paramref name="from" />, mirroring
    ///     <see cref="CloseOtherTabs" />'s focus and effect rules. Closing the
    ///     last tab to the right is a no-op.
    /// </summary>
    /// <remarks>
    ///     Pinned tabs to the right survive (#390) — same rule as
    ///     <see cref="CloseOtherTabs" />. When every tab on the right is
    ///     pinned, nothing closes and the state is returned untouched.
    /// </remarks>
    public static ReduceResult CloseTabsToRight(UiState state, SessionId from)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(from);
        if (index < 0 || index == tabs.Length - 1)
            return ReduceResult.NoOp(state);

        bool closesAnything = false;
        for (int i = index + 1; i < tabs.Length; i++)
        {
            if (!tabs[i].IsPinned)
            {
                closesAnything = true;
                break;
            }
        }

        if (!closesAnything)
            return ReduceResult.NoOp(state);

        var survivors = ImmutableArray.CreateBuilder<SessionTab>(tabs.Length);
        for (int i = 0; i <= index; i++)
            survivors.Add(tabs[i]);
        for (int i = index + 1; i < tabs.Length; i++)
        {
            if (tabs[i].IsPinned)
                survivors.Add(tabs[i]);
        }

        var remaining = survivors.ToImmutable();
        var released = state;
        var closedStack = strip.ClosedStack;
        for (int i = index + 1; i < tabs.Length; i++)
        {
            if (!tabs[i].IsPinned)
            {
                released = ReleaseOwnedPanels(released, tabs[i], remaining);
                closedStack = PushClosed(closedStack, tabs[i], i);
            }
        }

        var next = released with
        {
            Chat = released.Chat with
            {
                TabStrip = strip with { Tabs = remaining, ActiveTabId = from, ClosedStack = closedStack }
            }
        };
        TuiEffect effect = SameSession(strip.ActiveTabId, from)
            ? new TuiEffect.None()
            : new TuiEffect.ActivateSession(from);
        return new ReduceResult(next, effect);
    }

    /// <summary>
    ///     Pin or unpin a tab. Flag only: pinning never reorders, so the tab the
    ///     user is looking at cannot jump under the cursor.
    /// </summary>
    public static UiState PinTab(UiState state, SessionId sessionId, bool pinned)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(sessionId);
        if (index < 0)
            return state;

        var tab = tabs[index];
        if (tab.IsPinned == pinned)
            return state;
        return state with
        {
            Chat = state.Chat with { TabStrip = strip with { Tabs = tabs.SetItem(index, tab with { IsPinned = pinned }) } }
        };
    }

    /// <summary>
    ///     Move a tab to <paramref name="toIndex" />, clamped to the current
    ///     range. Remove-then-insert lands the tab exactly at the clamped index
    ///     (later tabs shift left once), and the active tab never changes —
    ///     reordering is presentation, not focus.
    /// </summary>
    public static UiState ReorderTab(UiState state, SessionId sessionId, int toIndex)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int from = strip.IndexOf(sessionId);
        if (from < 0 || tabs.Length < 2)
            return state;

        int to = Math.Clamp(toIndex, 0, tabs.Length - 1);
        if (to == from)
            return state;
        return state with
        {
            Chat = state.Chat with { TabStrip = strip with { Tabs = tabs.RemoveAt(from).Insert(to, tabs[from]) } }
        };
    }

    /// <summary>Focus the next tab in tab order (wraps around; no-op with fewer than two tabs).</summary>
    public static ReduceResult CycleNextTab(UiState state) => CycleTab(state, forward: true);

    /// <summary>Focus the previous tab in tab order (wraps around; no-op with fewer than two tabs).</summary>
    public static ReduceResult CyclePreviousTab(UiState state) => CycleTab(state, forward: false);

    // ── reopen stack + unread model (#1173, opencode steal — pure) ──────────

    /// <summary>
    ///     Push a closed tab onto the reopen stack: same-session entries are
    ///     replaced (a session is either open or on the stack, never both
    ///     twice) and the tail past <see cref="TabStripState.ReopenLimit" />
    ///     falls off oldest-first. Cold path — clarity over cleverness.
    /// </summary>
    public static ImmutableArray<ClosedTab> PushClosed(
        ImmutableArray<ClosedTab> stack, SessionTab tab, int index)
    {
        var builder = ImmutableArray.CreateBuilder<ClosedTab>(stack.Length + 1);
        foreach (var entry in stack)
        {
            if (!SameSession(entry.SessionId, tab.SessionId))
                builder.Add(entry);
        }

        builder.Add(new ClosedTab(tab, index));
        int drop = builder.Count - TabStripState.ReopenLimit;
        for (int i = 0; i < drop; i++)
            builder.RemoveAt(0);
        return builder.ToImmutable();
    }

    /// <summary>
    ///     Drop a session's reopen entry, if any (at most one — see
    ///     <see cref="PushClosed" />). Returns the input array untouched when
    ///     the session has no entry.
    /// </summary>
    public static ImmutableArray<ClosedTab> PruneClosed(ImmutableArray<ClosedTab> stack, SessionId sessionId)
    {
        for (int i = 0; i < stack.Length; i++)
        {
            if (SameSession(stack[i].SessionId, sessionId))
                return stack.RemoveAt(i);
        }

        return stack;
    }

    /// <summary>
    ///     Restore the most recently closed tab at its original position
    ///     (#1173 — <c>Ctrl+Shift+T</c>, opencode's
    ///     <c>reopenSessionTab</c>). Entries whose session is already open are
    ///     consumed and skipped, so repeated reopens walk the stack instead of
    ///     stalling on a stale head. The insert index is clamped: a tab closed
    ///     from a longer strip restores at the end. Nothing restorable → the
    ///     stack is pruned (or already empty) and the state is a no-op.
    /// </summary>
    /// <remarks>
    ///     The restored tab becomes the focused one, so its unread signal is
    ///     cleared on the way in — focusing is acknowledging (see
    ///     <see cref="ActivateTab" />), and a stale marker on the tab now on
    ///     screen would be a lie.
    /// </remarks>
    public static ReduceResult ReopenTab(UiState state)
    {
        var strip = state.Chat.TabStrip;
        var remaining = strip.ClosedStack;
        if (remaining.IsEmpty)
            return ReduceResult.NoOp(state);

        while (!remaining.IsEmpty)
        {
            var entry = remaining[remaining.Length - 1];
            remaining = remaining.RemoveAt(remaining.Length - 1);
            if (strip.Contains(entry.SessionId))
                continue;

            int at = Math.Clamp(entry.Index, 0, strip.Tabs.Length);
            var tabs = strip.Tabs.Insert(at, entry.Tab with { IsDirty = false, HasError = false });
            var next = state with
            {
                Chat = state.Chat with
                {
                    TabStrip = strip with
                    {
                        Tabs = tabs,
                        ActiveTabId = entry.SessionId,
                        ClosedStack = remaining
                    }
                }
            };
            return new ReduceResult(next, new TuiEffect.ActivateSession(entry.SessionId));
        }

        // Every entry was stale (all sessions already open): the pruned stack
        // is still a real change, so it commits instead of being rediscovered
        // on the next reopen.
        return ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { TabStrip = strip with { ClosedStack = remaining } }
        });
    }

    /// <summary>
    ///     Focus the next (or previous) tab with an unread signal (#1173).
    ///     No other unread tab → same state instance, no effect.
    /// </summary>
    public static ReduceResult CycleUnreadTab(UiState state, bool forward)
    {
        var strip = state.Chat.TabStrip;
        var target = strip.NextUnread(strip.ActiveTabId, forward);
        return target is null ? ReduceResult.NoOp(state) : ActivateTab(state, target);
    }

    /// <summary>
    ///     Focus the Nth tab in tab order (#1173 — <c>Ctrl+1</c>..<c>Ctrl+9</c>).
    ///     Slots are one-based; anything outside the open strip is a no-op.
    /// </summary>
    public static ReduceResult ActivateTabSlot(UiState state, int slot)
    {
        var tabs = state.Chat.TabStrip.Tabs;
        int index = slot - 1;
        return index < 0 || index >= tabs.Length
            ? ReduceResult.NoOp(state)
            : ActivateTab(state, tabs[index].SessionId);
    }

    /// <summary>
    ///     <see cref="ChatAction.ActivateTabSlot" /> — the <c>Ctrl+digit</c>
    ///     key path. The slot rides the pressed key's character (like
    ///     <see cref="ChatAction.TogglePanelSlot" />); a non-digit character
    ///     is a no-op, which cannot happen through the keymap guard but keeps
    ///     a hand-built <c>KeyInput</c> honest.
    /// </summary>
    public static ReduceResult ActivateSlotFromKey(UiState state, UiKey key)
    {
        if (key.Character is not { } c || c < '1' || c > '9')
            return ReduceResult.NoOp(state);
        return ActivateTabSlot(state, c - '0');
    }

    /// <summary>
    ///     Mark a background tab unread (#1173). Unknown session, an already
    ///     sufficient mark (an error subsumes activity), or the active tab
    ///     (viewing is reading) → the input state back, untouched.
    /// </summary>
    public static UiState MarkTabUnread(UiState state, SessionId sessionId, bool isError)
    {
        var strip = state.Chat.TabStrip;
        int index = strip.IndexOf(sessionId);
        if (index < 0 || SameSession(strip.ActiveTabId, sessionId))
            return state;

        var tab = strip.Tabs[index];
        bool error = tab.HasError || isError;
        if (tab.IsDirty && tab.HasError == error)
            return state;

        return state with
        {
            Chat = state.Chat with
            {
                TabStrip = strip with
                {
                    Tabs = strip.Tabs.SetItem(index, tab with { IsDirty = true, HasError = error })
                }
            }
        };
    }

    /// <summary>Clear a tab's unread signal without focusing it (#1173).</summary>
    public static UiState MarkTabRead(UiState state, SessionId sessionId)
    {
        var strip = state.Chat.TabStrip;
        int index = strip.IndexOf(sessionId);
        if (index < 0)
            return state;

        var tab = strip.Tabs[index];
        if (!tab.HasUnread)
            return state;

        return state with
        {
            Chat = state.Chat with
            {
                TabStrip = strip with
                {
                    Tabs = strip.Tabs.SetItem(index, tab with { IsDirty = false, HasError = false })
                }
            }
        };
    }

    // ── tab strip from the keyboard (#389) ──────────────────────────────────
    // ChatAction → tab-transition aliases. They exist so the keymap stays the
    // only key→meaning table (no shell-local branches) and every tab mutation
    // arrives through the one reducer path instead of a renderer's own
    // selection field.

    /// <summary>
    ///     <see cref="ChatAction.NextTab" /> — the <c>Ctrl+Tab</c> key path.
    ///     Delegates to <see cref="CycleNextTab" />, which already no-ops with
    ///     fewer than two tabs rather than re-emitting an activate effect for
    ///     the session already on screen.
    /// </summary>
    public static ReduceResult NextTabFromKey(UiState state) => CycleNextTab(state);

    /// <summary>
    ///     <see cref="ChatAction.PreviousTab" /> — the <c>Ctrl+Shift+Tab</c>
    ///     key path. Delegates to <see cref="CyclePreviousTab" />.
    /// </summary>
    public static ReduceResult PreviousTabFromKey(UiState state) => CyclePreviousTab(state);

    /// <summary>
    ///     <see cref="ChatAction.CloseTab" /> (<c>Ctrl+W</c>) — close the
    ///     <i>focused</i> tab, not the app.
    /// </summary>
    /// <remarks>
    ///     The distinction is the whole point of the binding, so it is enforced
    ///     here rather than in a shell: this method can only ever return
    ///     <see cref="TuiEffect.ActivateSession" /> or
    ///     <see cref="TuiEffect.None" /> — never
    ///     <see cref="TuiEffect.QuitApp" />. With no tab open it is a no-op, and
    ///     the explicit quit path (<see cref="ChatAction.Quit" />) is untouched.
    /// </remarks>
    public static ReduceResult CloseFocusedTab(UiState state)
    {
        if (state.Chat.TabStrip.ActiveTabId is not { } active)
            return ReduceResult.NoOp(state);

        return CloseTab(state, active);
    }

    /// <summary>
    ///     <see cref="ChatAction.OpenTab" /> (<c>Ctrl+T</c>) — ask the host to
    ///     open or switch a session. Pure state change: the reducer does not know
    ///     which session the user means (that is a picker decision), so it only
    ///     asks, and hosts without a picker wired ignore the effect.
    /// </summary>
    public static ReduceResult RequestOpenTab(UiState state) =>
        new(state, new TuiEffect.RequestOpenSession());

    /// <summary>
    ///     <see cref="ChatAction.MoveTabLeft" /> / <see cref="ChatAction.MoveTabRight" /> —
    ///     move the <i>focused</i> tab one step (<paramref name="delta" /> of -1/+1)
    ///     through the existing <see cref="ReorderTab" /> transition (#390).
    ///     Focus follows the tab (reorder never changes the active id), the
    ///     effect is always <see cref="TuiEffect.None" /> — moving is
    ///     presentation, not a session switch — and the edges are no-ops.
    /// </summary>
    public static ReduceResult MoveFocusedTab(UiState state, int delta)
    {
        if (state.Chat.TabStrip.ActiveTabId is not { } active)
            return ReduceResult.NoOp(state);

        var strip = state.Chat.TabStrip;
        int from = strip.IndexOf(active);
        if (from < 0)
            return ReduceResult.NoOp(state);

        int to = from + delta;
        if (to < 0 || to >= strip.Tabs.Length)
            return ReduceResult.NoOp(state);

        return ReduceResult.NoOp(ReorderTab(state, active, to));
    }

    /// <summary>
    ///     Restore the open-tab order + active tab from a persisted
    ///     <see cref="TabStripSnapshot" /> (#390, slice 3/3 — pure; the host
    ///     persists debounced and dispatches after first paint).
    /// </summary>
    /// <remarks>
    ///     Snapshot ids with no descriptor (sessions deleted since the persist)
    ///     are dropped with a single system note — one line no matter how many
    ///     vanished. A repeated id restores once (first occurrence wins).
    ///     Descriptors the snapshot does not order are appended at the end, so
    ///     a session the store knows is never lost to a stale payload. An
    ///     unknown active id falls back to the first restored tab; nothing
    ///     restored at all means an empty strip, no focus, no crash — the host
    ///     then opens the default tab through the existing open path.
    /// </remarks>
    public static ReduceResult HydrateTabStrip(UiState state, ChatAppMsg.HydrateTabStrip h)
    {
        var byId = new Dictionary<string, SessionTab>(StringComparer.Ordinal);
        foreach (var tab in h.Tabs)
        {
            if (!byId.ContainsKey(tab.SessionId.Value))
                byId[tab.SessionId.Value] = tab;
        }

        var restored = ImmutableArray.CreateBuilder<SessionTab>(byId.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in h.Snapshot.Order)
        {
            if (!seen.Add(id))
                continue;
            if (byId.TryGetValue(id, out var tab))
                restored.Add(tab);
        }

        foreach (var tab in h.Tabs)
        {
            if (seen.Add(tab.SessionId.Value))
                restored.Add(tab);
        }

        var tabs = restored.ToImmutable();
        int dropped = seen.Count - tabs.Length;

        SessionId? active = null;
        if (tabs.Length > 0)
        {
            active = tabs[0].SessionId;
            var want = h.Snapshot.ActiveSessionId;
            if (want is not null)
            {
                foreach (var tab in tabs)
                {
                    if (string.Equals(tab.SessionId.Value, want, StringComparison.Ordinal))
                    {
                        active = tab.SessionId;
                        break;
                    }
                }
            }
        }

        var next = state with
        {
            Chat = state.Chat with
            {
                TabStrip = state.Chat.TabStrip with { Tabs = tabs, ActiveTabId = active }
            }
        };

        if (dropped > 0)
        {
            next = next.AddLine(
                ChatRole.System,
                $"Restored {tabs.Length} tab(s); dropped {dropped} missing session(s).");
        }

        return ReduceResult.NoOp(next);
    }

    /// <summary>
    ///     Neighbour rule for a closed tab — documented once, pinned by
    ///     <c>TabStripReducerTests</c>: after removing the tab at
    ///     <paramref name="removedIndex" />, focus lands on the tab that slid
    ///     into that slot (the one on its right); when the closed tab was last
    ///     there is nothing on the right, so focus falls back to the tab on its
    ///     left; with no tabs left nothing is focused.
    /// </summary>
    public static SessionId? TabNeighbourAfterClose(ImmutableArray<SessionTab> remaining, int removedIndex)
    {
        if (remaining.Length == 0)
            return null;
        int target = removedIndex < remaining.Length ? removedIndex : remaining.Length - 1;
        return remaining[target].SessionId;
    }

    private static ReduceResult CycleTab(UiState state, bool forward)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        if (tabs.Length < 2)
            return ReduceResult.NoOp(state);

        // No active tab yet → the first tab going forward, the last going back.
        int current = strip.ActiveTabId is { } active ? strip.IndexOf(active) : -1;
        int target = current < 0
            ? (forward ? 0 : tabs.Length - 1)
            : (forward ? current + 1 : current - 1 + tabs.Length) % tabs.Length;
        return ActivateTab(state, tabs[target].SessionId);
    }

    /// <summary>
    ///     Panel-ownership rule — documented once: a panel dies with the tab only
    ///     when that tab was its <i>sole</i> owner, in which case it goes
    ///     <see cref="TuiPanelState.Hidden" /> and gives up focus (chat takes it
    ///     back). A panel still listed by a surviving tab is <i>reassigned</i> to
    ///     the nearest surviving owner in tab order and keeps its state, because
    ///     panel state is global while ownership is per tab. Ids the host never
    ///     seeded into <see cref="UiState.PanelStates" /> are ignored.
    /// </summary>
    private static UiState ReleaseOwnedPanels(UiState state, SessionTab closed, ImmutableArray<SessionTab> survivors)
    {
        var owned = closed.PanelIds;
        if (owned.Length == 0 || state.Ui.PanelStates.Count == 0)
            return state;

        var panelStates = state.Ui.PanelStates;
        string? focused = state.Ui.FocusedPanelId;
        bool changed = false;

        for (int i = 0; i < owned.Length; i++)
        {
            string id = owned[i];
            if (string.IsNullOrEmpty(id) || !panelStates.ContainsKey(id))
                continue;
            if (IsOwnedBy(survivors, id))
                continue;

            if (panelStates[id] != TuiPanelState.Hidden)
            {
                panelStates = panelStates.SetItem(id, TuiPanelState.Hidden);
                changed = true;
            }
            if (string.Equals(focused, id, StringComparison.Ordinal))
            {
                focused = null;
                changed = true;
            }
        }

        if (!changed)
            return state;
        return state with { Ui = state.Ui with { PanelStates = panelStates, FocusedPanelId = focused } };
    }

    private static bool IsOwnedBy(ImmutableArray<SessionTab> tabs, string panelId)
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            var owned = tabs[i].PanelIds;
            for (int j = 0; j < owned.Length; j++)
            {
                if (string.Equals(owned[j], panelId, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     Session-id equality by value — <see cref="SessionId" /> is a
    ///     reference-typed value object, so <c>==</c> would compare references and
    ///     miss a tab built from an equal-but-distinct id.
    /// </summary>
    private static bool SameSession(SessionId? left, SessionId right) =>
        left is not null && string.Equals(left.Value, right.Value, StringComparison.Ordinal);
}
