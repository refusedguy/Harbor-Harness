using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Key-path behaviour of the tab strip (#389, slice 2/3) and the
///     session-isolation guarantee a tab switch has to keep.
/// </summary>
/// <remarks>
///     The bindings themselves are pinned in <see cref="ChatKeyMapTests" />;
///     what matters here is that a chord resolves, reaches the #388 tab
///     transitions, and — for close — cannot be confused with quitting.
/// </remarks>
public class TabStripKeyBindingTests
{
    private static readonly ChatKeyMap Map = new();

    private static SessionId Sid(string id) => SessionId.Create(id);

    /// <summary>
    ///     Store with <paramref name="count" /> open tabs and focus on the first.
    ///     <see cref="ChatAppMsg.OpenTab" /> focuses whatever it opens, so the
    ///     activation has to be explicit — otherwise "focus" would silently mean
    ///     "last opened" and every direction assertion below would be ambiguous.
    /// </summary>
    private static UiStore StoreWithTabs(int count)
    {
        var store = new UiStore();
        for (int i = 0; i < count; i++)
            _ = store.Dispatch(new ChatAppMsg.OpenTab(new SessionTab(Sid($"s{i}"), $"session {i}")));

        if (count > 0)
            _ = store.Dispatch(new ChatAppMsg.ActivateTab(Sid("s0")));

        return store;
    }

    private static TuiEffect Press(UiStore store, UiKey key) =>
        store.Dispatch(new AppMsg.KeyInput(Map.Resolve(key, store.State), key));

    private static UiKey CtrlTab => new(UiKeyCode.Tab, KeyModifierSet.Ctrl);

    private static UiKey CtrlShiftTab => new(UiKeyCode.Tab, KeyModifierSet.Ctrl | KeyModifierSet.Shift);

    private static UiKey Ctrl(char c) => UiKey.ForChar(c, KeyModifierSet.Ctrl);

    // ── next / previous ────────────────────────────────────────────────────

    [Test]
    public async Task CtrlTab_MovesFocusForward_AndAsksForTheSession()
    {
        var store = StoreWithTabs(3);
        var effect = Press(store, CtrlTab);

        await Assert.That(store.State.Chat.TabStrip.ActiveTabId!.Value).IsEqualTo("s1");
        await Assert.That(effect).IsTypeOf<TuiEffect.ActivateSession>();
        await Assert.That(((TuiEffect.ActivateSession)effect).SessionId.Value).IsEqualTo("s1");
    }

    [Test]
    public async Task CtrlShiftTab_MovesFocusBackward_AndWraps()
    {
        var store = StoreWithTabs(3);
        // Walk to the last tab, then step back past the start.
        _ = Press(store, CtrlTab);
        _ = Press(store, CtrlTab);
        await Assert.That(store.State.Chat.TabStrip.ActiveTabId!.Value).IsEqualTo("s2");

        _ = Press(store, CtrlShiftTab);
        await Assert.That(store.State.Chat.TabStrip.ActiveTabId!.Value).IsEqualTo("s1");
    }

    [Test]
    public async Task Cycling_DoesNotReorder_TheStripIsOrder()
    {
        var store = StoreWithTabs(4);
        _ = Press(store, CtrlTab);
        _ = Press(store, CtrlShiftTab);

        var ids = store.State.Chat.TabStrip.Tabs.Select(t => t.SessionId.Value).ToArray();
        await Assert.That(ids).IsEquivalentTo(new[] { "s0", "s1", "s2", "s3" });
    }

    [Test]
    public async Task SingleTab_CtrlTabDoesNotTouchTheStrip()
    {
        // One tab is not a strip to cycle: the guard hands the chord back to the
        // panel cycle, so the tab strip is untouched and no activate effect fires.
        var store = StoreWithTabs(1);
        var effect = Press(store, CtrlTab);

        await Assert.That(store.State.Chat.TabStrip.ActiveTabId!.Value).IsEqualTo("s0");
        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(1);
        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
    }

    // ── close: the binding must never quit the app ─────────────────────────

    [Test]
    public async Task CtrlW_ClosesTheTabAndNeverAsksToQuit()
    {
        var store = StoreWithTabs(2);
        var effect = Press(store, Ctrl('w'));

        // The distinction the whole binding exists for: a tab, not the app.
        await Assert.That(effect is TuiEffect.QuitApp).IsFalse();
        await Assert.That(store.State.Ui.ShouldQuit).IsFalse();
        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(1);
    }

    [Test]
    public async Task CtrlW_OnLastTab_StillDoesNotQuit()
    {
        var store = StoreWithTabs(1);
        var effect = Press(store, Ctrl('w'));

        await Assert.That(effect is TuiEffect.QuitApp).IsFalse();
        await Assert.That(store.State.Ui.ShouldQuit).IsFalse();
        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CtrlW_WithNoTabs_IsInert()
    {
        var store = new UiStore();
        var effect = Press(store, Ctrl('w'));

        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
        await Assert.That(store.State.Ui.ShouldQuit).IsFalse();
    }

    [Test]
    public async Task ExplicitQuitPath_StillWorks()
    {
        // The escape hatch must survive the new Ctrl+W binding.
        var store = StoreWithTabs(3);
        var quit = store.Dispatch(new AppMsg.KeyInput(ChatAction.Quit, UiKey.Unknown));
        await Assert.That(quit).IsTypeOf<TuiEffect.QuitApp>();
    }

    // ── open / switch ──────────────────────────────────────────────────────

    [Test]
    public async Task CtrlT_AsksTheHostToOpenASession()
    {
        var store = StoreWithTabs(2);
        var effect = Press(store, Ctrl('t'));

        await Assert.That(effect).IsTypeOf<TuiEffect.RequestOpenSession>();
        // A pure request: the reducer must not guess which session the user
        // meant, so the tab strip is untouched.
        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(2);
    }

    // ── session isolation on the tab-switch path ────────────────────────────
    //
    // The leak mode documented on SessionEventRouter: events for session A
    // landing in session B's transcript because the "active" store was used as
    // a fallback. A tab switch is exactly the moment that fallback becomes
    // reachable, so the routing must be asserted, not assumed.

    [Test]
    public async Task TabSwitch_DoesNotLeakMessagesAcrossSessions()
    {
        var router = new SessionEventRouter();
        var a = router.GetOrCreateContext(MakeSession("s0", "alpha"));
        var b = router.GetOrCreateContext(MakeSession("s1", "beta"));

        // The strip itself lives in the host store — tabs are workspace chrome,
        // not transcript. Transcripts live in the router's per-session stores.
        var host = new UiStore();
        _ = host.Dispatch(new ChatAppMsg.OpenTab(new SessionTab(Sid(a.Session.Id), "alpha")));
        _ = host.Dispatch(new ChatAppMsg.OpenTab(new SessionTab(Sid(b.Session.Id), "beta")));

        _ = host.Dispatch(new ChatAppMsg.ActivateTab(Sid(a.Session.Id)));
        router.ActiveContext = a;
        _ = a.Store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "for alpha only"));

        // The tab-switch path: the reducer resolves the target and asks; it never
        // moves messages itself.
        var effect = host.Dispatch(new ChatAppMsg.ActivateTab(Sid(b.Session.Id)));
        await Assert.That(effect).IsTypeOf<TuiEffect.ActivateSession>();
        await Assert.That(((TuiEffect.ActivateSession)effect).SessionId.Value).IsEqualTo("s1");

        // The host performs the switch by rebinding the active context.
        router.ActiveContext = b;
        _ = b.Store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "for beta only"));

        // A late event for the tab we just left must still land in ITS store.
        _ = router.GetContext("s0")!.Store
            .Dispatch(new ChatAppMsg.AppendLine(ChatRole.Assistant, "late alpha event"));

        await Assert.That(Text(a)).Contains("for alpha only");
        await Assert.That(Text(a)).Contains("late alpha event");
        await Assert.That(Text(a)).DoesNotContain("beta");
        await Assert.That(Text(b)).Contains("for beta only");
        await Assert.That(Text(b)).DoesNotContain("alpha");
    }

    [Test]
    public async Task TabSwitch_KeepsPerSessionStores_SoBackgroundEventsStayHome()
    {
        // The mechanism the test above depends on: routing keys off the session
        // id the event carries, never off "whatever is active". If that ever
        // regresses to an active-store fallback, a background agent in the tab
        // we left writes into the transcript now on screen.
        var router = new SessionEventRouter();
        var a = router.GetOrCreateContext(MakeSession("s0", "alpha"));
        var b = router.GetOrCreateContext(MakeSession("s1", "beta"));

        _ = router.GetContext("s0")!.Store
            .Dispatch(new ChatAppMsg.AppendLine(ChatRole.Assistant, "late event for alpha"));
        router.ActiveContext = b;

        await Assert.That(Text(a)).Contains("late event for alpha");
        await Assert.That(Text(b)).DoesNotContain("alpha");
    }

    [Test]
    public async Task ClosingATab_LeavesTheSurvivorFocused()
    {
        var store = StoreWithTabs(3);
        _ = Press(store, CtrlTab); // focus s1
        _ = store.Dispatch(new ChatAppMsg.CloseTab(Sid("s1")));

        await Assert.That(store.State.Chat.TabStrip.ActiveTabId!.Value).IsEqualTo("s2");
        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(2);
    }

    [Test]
    public async Task TabStrip_SurvivesClearScreen()
    {
        // Workspace chrome, not transcript: clearing the feed must not close
        // the user's tabs (pinned in #388, re-asserted here because #389 makes
        // the strip visible enough to notice).
        var store = StoreWithTabs(2);
        _ = store.Dispatch(new AppMsg.Reset());

        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(2);
    }

    // Named MakeSession, not Session: a method called `Session` would shadow the
    // type of the same name and leave the target-typed `new` below with no type
    // to infer from (CS1526).
    private static Session MakeSession(string id, string title) =>
        new(
            Id: id,
            ProjectId: "p",
            Directory: "/tmp",
            Title: title,
            Agent: "code",
            Model: "test-model",
            ProviderId: "test",
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            Metadata: SessionMetadata.Empty);

    private static string Text(SessionContext ctx) =>
        string.Join("|", ctx.Store.State.Chat.Lines.Select(l => l.Text));
}
