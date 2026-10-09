using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #390 (tab-strip slice 3/3): gestures + restoration on top of the #388
///     model. Keyboard reorder through the existing <c>ReorderTab</c>
///     transition, the pinned-tab bulk-close guard, and the restore payload
///     round-trip. Mouse drag / middle-click / popover need a mouse route the
///     strip does not have yet (<c>ActivateAt</c>/<c>CloseAt</c> have no
///     callers) and ride the next slice — nothing here assumes them.
/// </summary>
public class TabStripGesturesTests
{
    private static readonly ChatKeyMap Map = new();

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

    private static string Ids(UiState state) =>
        string.Join(",", state.Chat.TabStrip.Tabs.Select(t => t.SessionId.Value));

    private static string ActiveId(UiState state) => state.Chat.TabStrip.ActiveTabId?.Value ?? "<none>";

    private static UiKey AltLeft => new(UiKeyCode.Left, KeyModifierSet.Alt);

    private static UiKey AltRight => new(UiKeyCode.Right, KeyModifierSet.Alt);

    private static TuiEffect Press(UiStore store, UiKey key) =>
        store.Dispatch(new AppMsg.KeyInput(Map.Resolve(key, store.State), key));

    private static UiStore StoreWithActive(string active, params SessionTab[] tabs)
    {
        var store = new UiStore();
        foreach (var tab in tabs)
            _ = store.Dispatch(new ChatAppMsg.OpenTab(tab));
        _ = store.Dispatch(new ChatAppMsg.ActivateTab(Id(active)));
        return store;
    }

    // ── keyboard reorder (Alt+Left / Alt+Right → ReorderTab) ────────────────

    [Test]
    public async Task AltLeft_MovesFocusedTabLeft_AndKeepsFocus()
    {
        var store = StoreWithActive("s1", Tab("s0"), Tab("s1"), Tab("s2"));

        var effect = Press(store, AltLeft);

        await Assert.That(Ids(store.State)).IsEqualTo("s1,s0,s2");
        await Assert.That(ActiveId(store.State)).IsEqualTo("s1");
        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task AltRight_MovesFocusedTabRight_AndKeepsFocus()
    {
        var store = StoreWithActive("s1", Tab("s0"), Tab("s1"), Tab("s2"));

        var effect = Press(store, AltRight);

        await Assert.That(Ids(store.State)).IsEqualTo("s0,s2,s1");
        await Assert.That(ActiveId(store.State)).IsEqualTo("s1");
        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task MoveTab_AtTheEdge_IsNoOp()
    {
        var left = StoreWithActive("s0", Tab("s0"), Tab("s1"));
        var beforeLeft = left.State;
        var leftEffect = Press(left, AltLeft);

        var right = StoreWithActive("s1", Tab("s0"), Tab("s1"));
        var beforeRight = right.State;
        var rightEffect = Press(right, AltRight);

        await Assert.That(left.State).IsSameReferenceAs(beforeLeft);
        await Assert.That(leftEffect).IsTypeOf<TuiEffect.None>();
        await Assert.That(right.State).IsSameReferenceAs(beforeRight);
        await Assert.That(rightEffect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task MoveTab_WithASingleTab_FallsThroughToNoOp()
    {
        var store = StoreWithActive("s0", Tab("s0"));
        var before = store.State;

        await Assert.That(Map.Resolve(AltLeft, store.State)).IsEqualTo(ChatAction.None);
        var effect = Press(store, AltLeft);

        await Assert.That(store.State).IsSameReferenceAs(before);
        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task MoveTab_WithoutAStateSnapshot_DoesNotClaimAltArrows()
    {
        await Assert.That(Map.Resolve(AltLeft)).IsEqualTo(ChatAction.None);
        await Assert.That(Map.Resolve(AltRight)).IsEqualTo(ChatAction.None);
        await Assert.That(Map.Resolve(AltLeft, StoreWithActive("s0", Tab("s0"), Tab("s1")).State))
            .IsEqualTo(ChatAction.MoveTabLeft);
        await Assert.That(Map.Resolve(AltRight, StoreWithActive("s0", Tab("s0"), Tab("s1")).State))
            .IsEqualTo(ChatAction.MoveTabRight);
    }

    // ── pinned-tab bulk-close guard ─────────────────────────────────────────

    [Test]
    public async Task CloseOtherTabs_SkipsPinnedTabs()
    {
        var state = WithActiveTab("s3", Tab("s1", pinned: true), Tab("s2"), Tab("s3"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseOtherTabs(Id("s3")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s3");
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CloseOtherTabs_WhenOnlyPinnedRemain_BesidesKeep_ChangesNothingButFocus()
    {
        var state = WithActiveTab("s2", Tab("s1"), Tab("s2", pinned: true));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseOtherTabs(Id("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s1");
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.ActivateSession>();
    }

    [Test]
    public async Task CloseOtherTabs_WithNothingToClose_IsNoOp()
    {
        var state = WithActiveTab("s1", Tab("s1"), Tab("s2", pinned: true));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseOtherTabs(Id("s1")));

        await Assert.That(result.State).IsSameReferenceAs(state);
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CloseTabsToRight_SkipsPinnedTabs()
    {
        var state = WithActiveTab("s2", Tab("s1"), Tab("s2"), Tab("s3", pinned: true), Tab("s4"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTabsToRight(Id("s2")));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CloseTab_OnAPinnedTab_StillCloses()
    {
        // Pin guards bulk gestures only — an explicit close is intent.
        var state = WithTabs(Tab("s1", pinned: true), Tab("s2"));

        var result = ChatAppReducer.Update(state, new ChatAppMsg.CloseTab(Id("s1")));

        await Assert.That(Ids(result.State)).IsEqualTo("s2");
    }

    // ── restoration payload ─────────────────────────────────────────────────

    [Test]
    public async Task Snapshot_RoundTrip_PreservesOrderActiveAndFlags()
    {
        var state = WithActiveTab("s2", Tab("s1"), Tab("s2", pinned: true), Tab("s3", dirty: true));
        var snapshot = TabStripSnapshot.FromStrip(state.Chat.TabStrip);

        var result = ChatAppReducer.Update(
            new UiState(),
            new ChatAppMsg.HydrateTabStrip(snapshot, state.Chat.TabStrip.Tabs));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2,s3");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
        await Assert.That(result.State.Chat.TabStrip.Find(Id("s2"))!.IsPinned).IsTrue();
        await Assert.That(result.State.Chat.TabStrip.Find(Id("s3"))!.IsDirty).IsTrue();
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(0);
    }

    [Test]
    public async Task HydrateTabStrip_DropsUnknownSessions_WithASingleTranscriptLine()
    {
        var known = WithTabs(Tab("s1"), Tab("s2"));
        var snapshot = new TabStripSnapshot(["s1", "ghost", "s2"], "s2");

        var result = ChatAppReducer.Update(
            new UiState(),
            new ChatAppMsg.HydrateTabStrip(snapshot, known.Chat.TabStrip.Tabs));

        await Assert.That(Ids(result.State)).IsEqualTo("s1,s2");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Chat.Lines[0].Role).IsEqualTo(ChatRole.System);
    }

    [Test]
    public async Task HydrateTabStrip_DegenerateState_FallsBackToEmpty_WithoutCrashing()
    {
        var empty = ChatAppReducer.Update(
            new UiState(),
            new ChatAppMsg.HydrateTabStrip(new TabStripSnapshot([], null), []));

        await Assert.That(empty.State.Chat.TabStrip.Tabs.Length).IsEqualTo(0);
        await Assert.That(empty.State.Chat.TabStrip.ActiveTabId).IsNull();

        var ghosts = ChatAppReducer.Update(
            new UiState(),
            new ChatAppMsg.HydrateTabStrip(new TabStripSnapshot(["ghost"], "ghost"), []));

        await Assert.That(ghosts.State.Chat.TabStrip.Tabs.Length).IsEqualTo(0);
        await Assert.That(ghosts.State.Chat.TabStrip.ActiveTabId).IsNull();
        await Assert.That(ghosts.State.Chat.Lines.Length).IsEqualTo(1);
    }

    [Test]
    public async Task HydrateTabStrip_UnknownActive_FallsBackToTheFirstTab()
    {
        var known = WithTabs(Tab("s1"), Tab("s2"));
        var snapshot = new TabStripSnapshot(["s2", "s1"], "ghost");

        var result = ChatAppReducer.Update(
            new UiState(),
            new ChatAppMsg.HydrateTabStrip(snapshot, known.Chat.TabStrip.Tabs));

        await Assert.That(Ids(result.State)).IsEqualTo("s2,s1");
        await Assert.That(ActiveId(result.State)).IsEqualTo("s2");
    }
}
