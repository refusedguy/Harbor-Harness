using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #388 (tab-strip slice 1/3): the tab state model and its reducer
///     transitions. State only — no widget is touched here, so every expectation
///     below is about <see cref="ChatAppReducer.Update" /> and the
///     <see cref="TuiEffect" /> it asks the host to run.
/// </summary>
public class TabStripReducerTests
{
    // ── open / activate ────────────────────────────────────────────────────

    [Test]
    public async Task OpenTab_AppendsAndActivates_AndAsksHostToSwitch()
    {
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.OpenTab(Tab("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.ActivateSession>();
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
    }

    [Test]
    public async Task OpenTab_AlreadyOpen_ActivatesWithoutDuplicating()
    {
        // Idempotent open: no duplicate tab, and the descriptor already in place
        // is left alone (a re-open is not a metadata refresh).
        var state = WithTabs(Tab("s1", "first"), Tab("s2", "second"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.OpenTab(Tab("s1", "rewritten", dirty: true)));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(result.State.Chat.TabStrip.Find(Id("s1"))!.Title).IsEqualTo("first");
        await Assert.That(result.State.Chat.TabStrip.Find(Id("s1"))!.IsDirty).IsFalse();
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
    }

    [Test]
    public async Task ActivateTab_FocusesTab_WithoutReordering()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTab(Id("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
    }

    [Test]
    public async Task ActivateTab_UnknownOrAlreadyActive_IsNoOp()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"));

        var current = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTab(Id("s2")));
        var unknown = ChatAppReducer.Update(state, new ChatAppMsg.ActivateTab(Id("nope")));

        await Assert.That(current.State).IsSameReferenceAs(state);
        await Assert.That(current.Effect).IsTypeOf<TuiEffect.None>();
        await Assert.That(unknown.State).IsSameReferenceAs(state);
        await Assert.That(unknown.Effect).IsTypeOf<TuiEffect.None>();
    }

    // ── close + the neighbour rule ─────────────────────────────────────────

    [Test]
    public async Task CloseTab_Inactive_KeepsActiveTab()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo("s2,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s3");
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CloseTab_ActiveInTheMiddle_FocusesTheNextTab()
    {
        // Neighbour rule, first arm: the tab that slid into the closed slot.
        var state = WithActiveTab("s2", Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s2")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s3");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s3");
    }

    [Test]
    public async Task CloseTab_ActiveLast_FocusesThePreviousTab()
    {
        // Neighbour rule, second arm: nothing slid into the slot, so the tab on
        // the left takes over.
        var state = WithActiveTab("s2", Tab("s1"), Tab("s2"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s2")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
    }

    [Test]
    public async Task CloseTab_LastRemaining_LeavesNoActiveTabAndNoEffect()
    {
        // Neighbour rule, last arm: no tabs left → nothing focused, and no
        // session for the host to open.
        var state = WithTabs(Tab("s1"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo(string.Empty);
        await Assert.That(result.State.Chat.TabStrip.ActiveTabId).IsNull();
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CloseTab_Unknown_IsNoOp()
    {
        var state = WithTabs(Tab("s1"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("nope")));

        await Assert.That(result.State).IsSameReferenceAs(state);
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CloseOtherTabs_KeepsAnActiveTarget_WithoutEmittingASwitch()
    {
        var state = WithActiveTab("s2", Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseOtherTabs(Id("s2")));

        await Assert.That(Ids(result.State)).IsEqualTo("s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CloseOtherTabs_KeepsAnInactiveTarget_AndEmitsTheSwitch()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseOtherTabs(Id("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
    }

    [Test]
    public async Task CloseTabsToRight_ClosesStrictlyRight_AndFocusesSource()
    {
        var state = WithActiveTab("s3", Tab("s1"), Tab("s2"), Tab("s3"), Tab("s4"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTabsToRight(Id("s2")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s2");
    }

    [Test]
    public async Task CloseTabsToRight_FromTheLastTab_IsNoOp()
    {
        var state = WithActiveTab("s2", Tab("s1"), Tab("s2"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTabsToRight(Id("s2")));

        await Assert.That(result.State).IsSameReferenceAs(state);
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    // ── pin / reorder / cycle ──────────────────────────────────────────────

    [Test]
    public async Task PinTab_SetsTheFlag_WithoutReordering()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));

        var pinned = ChatAppReducer.Update(state, new ChatAppMsg.PinTab(Id("s1"), true));
        var again = ChatAppReducer.Update(pinned.State, new ChatAppMsg.PinTab(Id("s1"), true));
        var unpinned = ChatAppReducer.Update(pinned.State, new ChatAppMsg.PinTab(Id("s1"), false));

        await Assert.That(Ids(pinned.State)).IsEqualTo("s1,s2,s3");
        await Assert.That(pinned.State.Chat.TabStrip.Find(Id("s1"))!.IsPinned).IsTrue();
        await Assert.That(again.State).IsSameReferenceAs(pinned.State);
        await Assert.That(unpinned.State.Chat.TabStrip.Find(Id("s1"))!.IsPinned).IsFalse();
    }

    [Test]
    public async Task ReorderTab_MovesToIndex_AndKeepsTheActiveTab()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.ReorderTab(Id("s3"), 0));

        await Assert.That(Ids(result.State)).IsEqualTo("s3,s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
    }

    [Test]
    public async Task ReorderTab_OutOfRange_ClampsToTheEnds()
    {
        var state = WithTabs(Tab("s1"), Tab("s2"), Tab("s3"));

        var left = ChatAppReducer.Update(state, new ChatAppMsg.ReorderTab(Id("s2"), -5));
        var right = ChatAppReducer.Update(state, new ChatAppMsg.ReorderTab(Id("s2"), 99));
        var sameSpot = ChatAppReducer.Update(state, new ChatAppMsg.ReorderTab(Id("s2"), 1));

        await Assert.That(Ids(left.State)).IsEqualTo("s2,s1,s3");
        await Assert.That(Ids(right.State)).IsEqualTo("s1,s3,s2");
        await Assert.That(sameSpot.State).IsSameReferenceAs(state);
    }

    [Test]
    public async Task CycleNextTab_WrapsAround_AndEmitsTheSwitch()
    {
        var state = WithActiveTab("s3", Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CycleNextTab());

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s1");
    }

    [Test]
    public async Task CyclePreviousTab_WrapsAround_Backwards()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CyclePreviousTab());

        await Assert.That(ActiveId(result.State)).IsEqualTo("s3");
        await Assert.That(ActivatedSession(result.Effect)).IsEqualTo("s3");
    }

    [Test]
    public async Task CycleTab_WithoutAnActiveTab_TakesAnEnd()
    {
        // Tabs restored from disk with no focus yet: forward lands on the first
        // tab, backward on the last.
        var state = new UiState
        {
            Chat = new ChatDomainState { TabStrip = TabStripState.Empty with { Tabs = [Tab("s1"), Tab("s2")] } }
        };

        var next = ChatAppReducer.Update(state, new ChatAppMsg.CycleNextTab());
        var previous = ChatAppReducer.Update(state, new ChatAppMsg.CyclePreviousTab());

        await Assert.That(ActiveId(next.State)).IsEqualTo("s1");
        await Assert.That(ActiveId(previous.State)).IsEqualTo("s2");
    }

    [Test]
    public async Task CycleTab_WithASingleTab_IsNoOp()
    {
        var state = WithTabs(Tab("s1"));

        var next = ChatAppReducer.Update(state, new ChatAppMsg.CycleNextTab());
        var previous = ChatAppReducer.Update(state, new ChatAppMsg.CyclePreviousTab());

        await Assert.That(next.State).IsSameReferenceAs(state);
        await Assert.That(next.Effect).IsTypeOf<TuiEffect.None>();
        await Assert.That(previous.State).IsSameReferenceAs(state);
        await Assert.That(previous.Effect).IsTypeOf<TuiEffect.None>();
    }

    // ── panel ownership ────────────────────────────────────────────────────

    [Test]
    public async Task CloseTab_SoleOwnerPanel_DiesWithTheTab_AndFocusFallsBackToChat()
    {
        // Ownership rule, "closes" arm: this tab is the only owner, so the panel
        // is hidden and the focused panel hands focus back to chat.
        var state = WithPanels(("tree", TuiPanelState.Focused), ("log", TuiPanelState.Visible));
        state = Open(state, Tab("s1", panels: ["tree"]));
        state = Open(state, Tab("s2", panels: ["log"]));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1")));

        await Assert.That(result.State.Ui.PanelStates["tree"]).IsEqualTo(TuiPanelState.Hidden);
        await Assert.That(result.State.Ui.FocusedPanelId).IsNull();
        await Assert.That(result.State.Ui.PanelStates["log"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task CloseTab_SharedPanel_IsReassigned_AndKeepsItsState()
    {
        // Ownership rule, "reassigns" arm: a surviving tab still lists the
        // panel, so it keeps its state (panel state is global, ownership is
        // per tab).
        var state = WithPanels(("tree", TuiPanelState.Visible), ("log", TuiPanelState.Visible));
        state = Open(state, Tab("s1", panels: ["tree"]));
        state = Open(state, Tab("s2", panels: ["tree", "log"]));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1")));

        await Assert.That(result.State.Ui.PanelStates["tree"]).IsEqualTo(TuiPanelState.Visible);
        await Assert.That(Ids(result.State)).IsEqualTo("s2");
    }

    [Test]
    public async Task CloseOtherTabs_ClosesPanelsOnlyTheClosedTabsOwned()
    {
        var state = WithPanels(("tree", TuiPanelState.Visible), ("log", TuiPanelState.Visible));
        state = Open(state, Tab("s1", panels: ["log"]));
        state = Open(state, Tab("s2", panels: ["tree"]));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseOtherTabs(Id("s2")));

        await Assert.That(result.State.Ui.PanelStates["log"]).IsEqualTo(TuiPanelState.Hidden);
        await Assert.That(result.State.Ui.PanelStates["tree"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task CloseTab_UnseededPanelId_IsIgnored()
    {
        // A descriptor may name a panel the host never registered — the reducer
        // must not invent one.
        var state = Open(new UiState(), Tab("s1", panels: ["ghost"]));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1")));

        await Assert.That(result.State.Ui.PanelStates.Count).IsEqualTo(0);
    }

    // ── store / projection invariants ──────────────────────────────────────

    [Test]
    public async Task TabTransitions_ThatChangeNothing_DoNotBumpTheRevision()
    {
        var store = new UiStore();
        store.Dispatch(new ChatAppMsg.OpenTab(Tab("s1")));
        await Assert.That(store.State.Revision).IsEqualTo(1);

        var noOp = store.Dispatch(new ChatAppMsg.ActivateTab(Id("s1")));
        store.Dispatch(new ChatAppMsg.PinTab(Id("s1"), false));
        store.Dispatch(new ChatAppMsg.ReorderTab(Id("s1"), 0));
        store.Dispatch(new ChatAppMsg.CloseTab(Id("nope")));
        store.Dispatch(new ChatAppMsg.CloseTabsToRight(Id("s1")));
        store.Dispatch(new ChatAppMsg.CycleNextTab());

        await Assert.That(store.State.Revision).IsEqualTo(1);
        await Assert.That(noOp).IsTypeOf<TuiEffect.None>();

        store.Dispatch(new ChatAppMsg.PinTab(Id("s1"), true));
        await Assert.That(store.State.Revision).IsEqualTo(2);
    }

    [Test]
    public async Task HydrateSession_KeepsTheOpenTabs()
    {
        // Hydration is a session switch, not a tab-strip reset.
        var state = WithTabs(Tab("s1"), Tab("s2"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.HydrateSession("m", "p", "code", []));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
        await Assert.That(result.State.Chat.Model).IsEqualTo("m");
    }

    [Test]
    public async Task Projection_RoundTrip_LosesNoTabState()
    {
        var store = new UiStore();
        store.Dispatch(new ChatAppMsg.OpenTab(Tab("s1", "one", dirty: true, panels: ["tree"])));
        store.Dispatch(new ChatAppMsg.OpenTab(Tab("s2", "two", pinned: true)));
        store.Dispatch(new ChatAppMsg.ReorderTab(Id("s2"), 0));

        var snapshot = store.State;
        var projector = new DefaultUiProjector();
        _ = projector.Project(snapshot);

        await Assert.That(store.State).IsSameReferenceAs(snapshot);
        await Assert.That(Ids(snapshot)).IsEqualTo("s2,s1");
        await Assert.That(snapshot.Chat.TabStrip.Find(Id("s1"))!.IsDirty).IsTrue();
        await Assert.That(snapshot.Chat.TabStrip.Find(Id("s1"))!.PanelIds.Length).IsEqualTo(1);
        await Assert.That(snapshot.Chat.TabStrip.Find(Id("s2"))!.IsPinned).IsTrue();
        await Assert.That(snapshot.Chat.TabStrip.Find(Id("s2"))!.WorkingDirectory).IsEqualTo("/w");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static SessionId Id(string value) => SessionId.Create(value);

    private static SessionTab Tab(
        string sessionId,
        string title = "t",
        bool dirty = false,
        bool pinned = false,
        string[]? panels = null)
    {
        var tab = new SessionTab(Id(sessionId), title, "/w", "idle", dirty, pinned);
        return panels is null ? tab : tab with { PanelIds = panels.ToImmutableArray() };
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

    private static UiState WithPanels(params (string Id, TuiPanelState State)[] panels)
    {
        var states = ImmutableDictionary<string, TuiPanelState>.Empty;
        var ids = ImmutableArray.CreateBuilder<string>();
        foreach (var panel in panels)
        {
            states = states.SetItem(panel.Id, panel.State);
            ids.Add(panel.Id);
        }

        return new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = ids.ToImmutable(),
                PanelStates = states,
                FocusedPanelId = panels.FirstOrDefault(p => p.State == TuiPanelState.Focused).Id,
            },
        };
    }

    private static string Ids(UiState state) =>
        string.Join(",", state.Chat.TabStrip.Tabs.Select(t => t.SessionId.Value));

    private static string ActiveId(UiState state) => state.Chat.TabStrip.ActiveTabId?.Value ?? "<none>";

    private static string ActivatedSession(TuiEffect effect) =>
        effect is TuiEffect.ActivateSession activate ? activate.SessionId.Value : "<none>";
}
