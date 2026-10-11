using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Key-path behaviour of the #1173 tab model (epic #1155): reopen on
///     Ctrl+Shift+T, unread navigation on Alt+Shift+Up/Down, quick slots on
///     Ctrl+1..Ctrl+9. The bindings themselves are data in
///     <see cref="ChatKeyMap" />; what matters here is that each chord
///     resolves, reaches the transition, and never steals a legacy chord.
/// </summary>
public class TabModelKeyBindingTests
{
    private static readonly ChatKeyMap Map = new();

    private static SessionId Sid(string id) => SessionId.Create(id);

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

    private static UiKey Ctrl(char c) => UiKey.ForChar(c, KeyModifierSet.Ctrl);

    private static UiKey CtrlShift(char c) => UiKey.ForChar(c, KeyModifierSet.Ctrl | KeyModifierSet.Shift);

    private static UiKey AltShift(UiKeyCode code) => new(code, KeyModifierSet.Alt | KeyModifierSet.Shift);

    private static string Active(UiStore store) => store.State.Chat.TabStrip.ActiveTabId?.Value ?? "<none>";

    // ── reopen (Ctrl+Shift+T) ────────────────────────────────────────────

    [Test]
    public async Task CtrlShiftT_ReopensClosedTab_AtItsPosition()
    {
        var store = StoreWithTabs(2);
        _ = Press(store, Ctrl('w'));
        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(1);

        var effect = Press(store, CtrlShift('t'));

        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(2);
        await Assert.That(Active(store)).IsEqualTo("s0");
        await Assert.That(effect).IsTypeOf<TuiEffect.ActivateSession>();
    }

    [Test]
    public async Task CtrlT_StillAsksTheHostToOpen()
    {
        var store = StoreWithTabs(2);

        await Assert.That(Map.Resolve(Ctrl('t'), store.State)).IsEqualTo(ChatAction.OpenTab);
        var effect = Press(store, Ctrl('t'));

        await Assert.That(effect).IsTypeOf<TuiEffect.RequestOpenSession>();
        await Assert.That(store.State.Chat.TabStrip.Tabs.Length).IsEqualTo(2);
    }

    [Test]
    public async Task CtrlShiftT_WithEmptyStack_IsInert()
    {
        var store = StoreWithTabs(2);
        var before = store.State;

        await Assert.That(Map.Resolve(CtrlShift('t'), store.State)).IsEqualTo(ChatAction.ReopenTab);
        var effect = Press(store, CtrlShift('t'));

        await Assert.That(store.State).IsSameReferenceAs(before);
        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CtrlShiftT_ResolvesToReopen_WithoutAStateSnapshot()
    {
        // The reopen guard is chord-only (Shift), never strip-gated: a lone
        // tab over a deep closed stack still reopens, and a stateless host
        // resolves the same action the reducer then no-ops.
        await Assert.That(Map.Resolve(CtrlShift('t'))).IsEqualTo(ChatAction.ReopenTab);
        await Assert.That(Map.Resolve(Ctrl('t'))).IsEqualTo(ChatAction.OpenTab);
    }

    // ── unread navigation (Alt+Shift+Up/Down) ────────────────────────────

    [Test]
    public async Task AltShiftDown_FocusesNextUnreadTab()
    {
        var store = StoreWithTabs(3);
        _ = store.Dispatch(new ChatAppMsg.MarkTabUnread(Sid("s2"), IsError: false));

        await Assert.That(Map.Resolve(AltShift(UiKeyCode.Down), store.State))
            .IsEqualTo(ChatAction.NextUnreadTab);
        var effect = Press(store, AltShift(UiKeyCode.Down));

        await Assert.That(Active(store)).IsEqualTo("s2");
        await Assert.That(effect).IsTypeOf<TuiEffect.ActivateSession>();
    }

    [Test]
    public async Task AltShiftUp_FocusesPreviousUnreadTab()
    {
        var store = StoreWithTabs(3);
        _ = store.Dispatch(new ChatAppMsg.ActivateTab(Sid("s2")));
        _ = store.Dispatch(new ChatAppMsg.MarkTabUnread(Sid("s0"), IsError: true));

        await Assert.That(Map.Resolve(AltShift(UiKeyCode.Up), store.State))
            .IsEqualTo(ChatAction.PreviousUnreadTab);
        var effect = Press(store, AltShift(UiKeyCode.Up));

        await Assert.That(Active(store)).IsEqualTo("s0");
        await Assert.That(effect).IsTypeOf<TuiEffect.ActivateSession>();
    }

    [Test]
    public async Task AltShiftArrows_KeepLegacyMeanings_WithoutAnOpenStrip()
    {
        // Stateless resolve (every pre-#1173 caller) and a single-tab strip
        // both fail the guard, so Alt+Shift+arrows stay input history there.
        await Assert.That(Map.Resolve(AltShift(UiKeyCode.Down))).IsEqualTo(ChatAction.InputHistoryNext);
        await Assert.That(Map.Resolve(AltShift(UiKeyCode.Up))).IsEqualTo(ChatAction.InputHistoryPrev);
        await Assert.That(Map.Resolve(AltShift(UiKeyCode.Down), StoreWithTabs(1).State))
            .IsEqualTo(ChatAction.InputHistoryNext);
    }

    [Test]
    public async Task PlainAltArrows_StillResolveToHistory_WithAnOpenStrip()
    {
        // The Shift half of the guard is what splits the chord: no Shift, no
        // nav, even while three tabs are open.
        var state = StoreWithTabs(3).State;
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Down, KeyModifierSet.Alt), state))
            .IsEqualTo(ChatAction.InputHistoryNext);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Up, KeyModifierSet.Alt), state))
            .IsEqualTo(ChatAction.InputHistoryPrev);
    }

    // ── quick slots (Ctrl+1..Ctrl+9) ─────────────────────────────────────

    [Test]
    public async Task CtrlDigit_FocusesNthTab()
    {
        var store = StoreWithTabs(3);

        await Assert.That(Map.Resolve(Ctrl('2'), store.State)).IsEqualTo(ChatAction.ActivateTabSlot);
        var effect = Press(store, Ctrl('2'));

        await Assert.That(Active(store)).IsEqualTo("s1");
        await Assert.That(effect).IsTypeOf<TuiEffect.ActivateSession>();
    }

    [Test]
    public async Task CtrlDigit_OutOfRange_IsInert()
    {
        var store = StoreWithTabs(2);
        var before = store.State;

        var effect = Press(store, Ctrl('9'));

        await Assert.That(store.State).IsSameReferenceAs(before);
        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task CtrlLetter_OutsideSlotsAndBindings_StillFallsThroughToChar()
    {
        // The slot entry's null-character binding matches any Ctrl+char, so
        // the digit guard is what keeps unclaimed chords (Ctrl+Q, …) on the
        // legacy Char path.
        await Assert.That(Map.Resolve(Ctrl('q'), StoreWithTabs(3).State)).IsEqualTo(ChatAction.Char);
    }
}
