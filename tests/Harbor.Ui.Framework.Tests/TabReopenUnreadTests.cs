using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #1173 (opencode steal, epic #1155): the tab reopen stack and the
///     two-colour unread model. Pure reducer transitions — every expectation
///     is about <see cref="ChatAppReducer.Update" /> and the
///     <see cref="TuiEffect" /> it asks the host to run.
/// </summary>
public class TabReopenUnreadTests
{
    // ── reopen stack ─────────────────────────────────────────────────────

    [Test]
    public async Task CloseTab_PushesDescriptor_WithItsPosition()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s2")));

        var stack = result.State.Chat.TabStrip.ClosedStack;
        await Assert.That(stack.Length).IsEqualTo(1);
        await Assert.That(stack[0].SessionId.Value).IsEqualTo("s2");
        await Assert.That(stack[0].Index).IsEqualTo(1);
        await Assert.That(stack[0].Tab.Title).IsEqualTo("t");
    }

    [Test]
    public async Task ReopenTab_RestoresAtOriginalPosition_AndActivates()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));
        state = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s2"))).State;

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ReopenTab());

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s2");
        await Assert.That(result.State.Chat.TabStrip.ClosedStack.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ReopenTab_EmptyStack_IsNoOp()
    {
        var state = WithTabs(Tab("s1"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ReopenTab());

        await Assert.That(result.State).IsSameReferenceAs(state);
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task ReopenTab_ClampsIndex_WhenStripShrank()
    {
        // s3 was closed from a 3-tab strip; the strip now holds one tab, so
        // the recorded index (2) cannot apply — it restores at the end.
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));
        state = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s3"))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s2"))).State;

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ReopenTab());

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
    }

    [Test]
    public async Task ReopenTab_SkipsAlreadyOpenSessions_AndPrunesThem()
    {
        // A stale stack entry (session open again via hydration, which swaps
        // tabs without touching the stack) is consumed, not restored.
        var state = WithTabs(Tab("s1"), Tab("s2"));
        state = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1"))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s2"))).State;
        state = ChatAppReducer.Update(
            state,
            new ChatAppMsg.HydrateTabStrip(
                new TabStripSnapshot(["s2"], "s2"),
                [Tab("s2")])).State;

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ReopenTab());

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
        await Assert.That(result.State.Chat.TabStrip.ClosedStack.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ClosedStack_KeepsOnlyTheLast25()
    {
        var state = new UiState();
        for (int i = 0; i < 30; i++)
            state = ChatAppReducer.Update(state, new ChatAppMsg.OpenTab(Tab($"s{i}"))).State;
        for (int i = 0; i < 30; i++)
            state = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id($"s{i}"))).State;

        var stack = state.Chat.TabStrip.ClosedStack;
        await Assert.That(stack.Length).IsEqualTo(TabStripState.ReopenLimit);
        await Assert.That(stack[0].SessionId.Value).IsEqualTo("s5");

        for (int i = 0; i < TabStripState.ReopenLimit; i++)
            state = ChatAppReducer.Update(state, new ChatAppMsg.ReopenTab()).State;

        await Assert.That(state.Chat.TabStrip.ClosedStack.Length).IsEqualTo(0);
        await Assert.That(state.Chat.TabStrip.Tabs.Length).IsEqualTo(25);
        await Assert.That(state.Chat.TabStrip.Contains(Id("s0"))).IsFalse();
    }

    [Test]
    public async Task BulkCloses_PushEveryClosedTab()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"), Tab("s3"), Tab("s4"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTabsToRight(Id("s1")));

        var stack = result.State.Chat.TabStrip.ClosedStack;
        await Assert.That(stack.Length).IsEqualTo(3);
        await Assert.That(Ids(result.State)).IsEqualTo("s1");

        var reopened = ChatAppReducer.Update(result.State, new ChatAppMsg.ReopenTab());
        await Assert.That(Ids(reopened.State)).IsEqualTo("s1,s4");
    }

    [Test]
    public async Task OpenTab_PrunesTheSessionsReopenEntry()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"));
        state = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1"))).State;
        await Assert.That(state.Chat.TabStrip.ClosedStack.Length).IsEqualTo(1);

        var result = ChatAppReducer.Update(state, new ChatAppMsg.OpenTab(Tab("s1")));

        await Assert.That(result.State.Chat.TabStrip.ClosedStack.Length).IsEqualTo(0);
        await Assert.That(Ids(result.State)).IsEqualTo("s2,s1");
    }

    [Test]
    public async Task PushClosed_ReplacesSameSessionEntry_AndCapsTheTail()
    {
        var stack = ImmutableArray<ClosedTab>.Empty;
        stack = ChatAppReducer.PushClosed(stack, Tab("s1"), 0);
        stack = ChatAppReducer.PushClosed(stack, Tab("s2"), 1);
        stack = ChatAppReducer.PushClosed(stack, Tab("s1", "new"), 5);

        await Assert.That(stack.Length).IsEqualTo(2);
        await Assert.That(stack[1].SessionId.Value).IsEqualTo("s1");
        await Assert.That(stack[1].Tab.Title).IsEqualTo("new");
        await Assert.That(stack[1].Index).IsEqualTo(5);
    }

    // ── unread signals ───────────────────────────────────────────────────

    [Test]
    public async Task UnreadKind_MapsFlags_ToNoneActivityError()
    {
        await Assert.That(new SessionTab(Id("a")).Unread).IsEqualTo(TabUnread.None);
        await Assert.That(Tab("b", dirty: true).Unread).IsEqualTo(TabUnread.Activity);
        await Assert.That(new SessionTab(Id("c"), HasError: true).Unread).IsEqualTo(TabUnread.Error);
        await Assert.That(new SessionTab(Id("d"), "t", "/w", "idle", true, false, true).Unread).IsEqualTo(TabUnread.Error);
    }

    [Test]
    public async Task MarkTabUnread_SetsActivity_ThenUpgradesToError()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"));

        var activity = ChatAppReducer.MarkTabUnread(state, Id("s1"), isError: false);
        await Assert.That(activity.Chat.TabStrip.Find(Id("s1"))!.Unread).IsEqualTo(TabUnread.Activity);

        var error = ChatAppReducer.MarkTabUnread(activity, Id("s1"), isError: true);
        await Assert.That(error.Chat.TabStrip.Find(Id("s1"))!.Unread).IsEqualTo(TabUnread.Error);

        // An error subsumes activity: re-marking activity over an error is a no-op.
        var again = ChatAppReducer.MarkTabUnread(error, Id("s1"), isError: false);
        await Assert.That(again).IsSameReferenceAs(error);
    }

    [Test]
    public async Task MarkTabUnread_OnActiveOrUnknownTab_IsNoOp()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"));

        var active = ChatAppReducer.MarkTabUnread(state, Id("s2"), isError: true);
        var unknown = ChatAppReducer.MarkTabUnread(state, Id("nope"), isError: false);

        await Assert.That(active).IsSameReferenceAs(state);
        await Assert.That(unknown).IsSameReferenceAs(state);
    }

    [Test]
    public async Task ActivateTab_ClearsUnread_AndKeepsOrder()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"));
        state = ChatAppReducer.MarkTabUnread(state, Id("s1"), isError: true);

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTab(Id("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(result.State.Chat.TabStrip.Find(Id("s1"))!.HasUnread).IsFalse();
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
    }

    [Test]
    public async Task ActivateTab_AlreadyActiveUnread_ClearsWithoutSwitchEffect()
    {
        // A freshly opened dirty tab is focused AND unread; acknowledging it
        // switches nothing, so the host gets no activate effect.
        var state = ChatAppReducer.Update(new UiState(), new ChatAppMsg.OpenTab(Tab("s1", dirty: true))).State;

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTab(Id("s1")));

        await Assert.That(result.State.Chat.TabStrip.Find(Id("s1"))!.HasUnread).IsFalse();
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task MarkTabRead_ClearsWithoutFocusing()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"));
        state = ChatAppReducer.MarkTabUnread(state, Id("s1"), isError: false);

        var read = ChatAppReducer.MarkTabRead(state, Id("s1"));

        await Assert.That(read.Chat.TabStrip.Find(Id("s1"))!.HasUnread).IsFalse();
        await Assert.That(ActiveId(read)).IsEqualTo("s2");

        var clean = ChatAppReducer.MarkTabRead(read, Id("s1"));
        var unknown = ChatAppReducer.MarkTabRead(read, Id("nope"));
        await Assert.That(clean).IsSameReferenceAs(read);
        await Assert.That(unknown).IsSameReferenceAs(read);
    }

    // ── unread navigation ────────────────────────────────────────────────

    [Test]
    public async Task CycleUnreadTab_SkipsReadTabs_AndWraps()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"), Tab("s3"), Tab("s4"));
        state = ChatAppReducer.MarkTabUnread(state, Id("s2"), isError: false);
        state = ChatAppReducer.MarkTabUnread(state, Id("s4"), isError: true);

        var next = ChatAppReducer.Update(state, new ChatAppMsg.CycleNextUnreadTab());
        await Assert.That(ActiveId(next.State)).IsEqualTo("s2");
        await Assert.That(ActivatedSession(next.Effect)).IsEqualTo("s2");

        // s2 was acknowledged by the jump; the next unread forward is s4.
        var wrapped = ChatAppReducer.Update(next.State, new ChatAppMsg.CycleNextUnreadTab());
        await Assert.That(ActiveId(wrapped.State)).IsEqualTo("s4");

        var back = ChatAppReducer.Update(wrapped.State, new ChatAppMsg.CyclePreviousUnreadTab());
        await Assert.That(ActiveId(back.State)).IsEqualTo("s2");
    }

    [Test]
    public async Task CycleUnreadTab_WithNoOtherUnread_IsNoOp()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"));

        var next = ChatAppReducer.Update(state, new ChatAppMsg.CycleNextUnreadTab());
        var previous = ChatAppReducer.Update(state, new ChatAppMsg.CyclePreviousUnreadTab());

        await Assert.That(next.State).IsSameReferenceAs(state);
        await Assert.That(next.Effect).IsTypeOf<TuiEffect.None>();
        await Assert.That(previous.State).IsSameReferenceAs(state);
        await Assert.That(previous.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CycleUnreadTab_OnlyActiveUnread_DoesNotAcknowledge()
    {
        // The active tab itself is never the answer: pressing next-unread
        // while looking at the only unread tab must not clear its marker.
        var state = ChatAppReducer.Update(new UiState(), new ChatAppMsg.OpenTab(Tab("s1"))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.OpenTab(Tab("s2", dirty: true))).State;

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CycleNextUnreadTab());

        await Assert.That(result.State).IsSameReferenceAs(state);
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
        await Assert.That(state.Chat.TabStrip.Find(Id("s2"))!.HasUnread).IsTrue();
    }

    // ── quick slots ──────────────────────────────────────────────────────

    [Test]
    public async Task ActivateTabSlot_FocusesNthTab_OneBased()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTabSlot(3));

        await Assert.That(ActiveId(result.State)).IsEqualTo("s3");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s3");
    }

    [Test]
    public async Task ActivateTabSlot_OutOfRange_IsNoOp()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"));

        var zero = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTabSlot(0));
        var past = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTabSlot(9));

        await Assert.That(zero.State).IsSameReferenceAs(state);
        await Assert.That(zero.Effect).IsTypeOf<TuiEffect.None>();
        await Assert.That(past.State).IsSameReferenceAs(state);
        await Assert.That(past.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task ActivateSlotFromKey_RejectsNonDigits()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"));

        var letter = ChatAppReducer.ActivateSlotFromKey(state, UiKey.ForChar('q', KeyModifierSet.Ctrl));
        var digit = ChatAppReducer.ActivateSlotFromKey(state, UiKey.ForChar('2', KeyModifierSet.Ctrl));

        await Assert.That(letter.State).IsSameReferenceAs(state);
        await Assert.That(letter.Effect).IsTypeOf<TuiEffect.None>();
        await Assert.That(ActiveId(digit.State)).IsEqualTo("s2");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static SessionId Id(string value) => SessionId.Create(value);

    private static SessionTab Tab(
        string sessionId,
        string title = "t",
        bool dirty = false,
        bool pinned = false)
    {
        return new SessionTab(Id(sessionId), title, "/w", "idle", dirty, pinned);
    }

    private static UiState Open(UiState state, SessionTab tab) =>
        ChatAppReducer.Update(state, new ChatAppMsg.OpenTab(tab)).State;

    private static UiState WithTabs(params SessionTab[] tabs)
    {
        var state = new UiState();
        foreach (var tab in tabs)
            state = Open(state, tab);
        return state;
    }

    private static UiState WithActiveTab(string sessionId, params SessionTab[] tabs)
    {
        var state = WithTabs(tabs);
        return ChatAppReducer.Update(state, new ChatAppMsg.ActivateTab(Id(sessionId))).State;
    }

    private static string Ids(UiState state) =>
        string.Join(",", state.Chat.TabStrip.Tabs.Select(t => t.SessionId.Value));

    private static string ActiveId(UiState state) => state.Chat.TabStrip.ActiveTabId?.Value ?? "<none>";

    private static string ActivatedSession(TuiEffect effect) =>
        effect is TuiEffect.ActivateSession activate ? activate.SessionId.Value : "<none>";
}
