using System.Collections.Immutable;
using System.Reflection;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.SpectreTui;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
namespace Harbor.Tui.Tests;
/// <summary>
///     Tests for the Harbor panel system: <see cref="PanelRegistry" /> (thread-safe
///     registration-only registry), <see cref="AppReducer" />'s panel transitions
///     (<see cref="AppMsg.TogglePanel" />, <see cref="AppMsg.FocusPanel" />,
///     <see cref="AppMsg.CyclePanelsFocus" />, <see cref="AppMsg.ResizePanel" />), and
///     the read-only <see cref="PanelRegistryView" /> snapshot that
///     <c>PanelLayoutShell</c> consumes.
/// </summary>
public class PanelRegistryTests
{
    /// <summary>
    ///     Test 1 — <see cref="PanelRegistry.Register" /> adds the panel and makes it
    ///     retrievable by id. The registry no longer seeds a default state (TEA: state
    ///     lives in <see cref="UiState" />, mutated only by the reducer).
    /// </summary>
    [Test]
    public async Task Register_AddsPanel_And_IsRetrievable()
    {
        var registry = new PanelRegistry();
        var panel = new StubPanel("alpha", "Alpha", TuiPanelPlacement.Left, 24);

        var result = registry.Register(panel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(registry.All).HasCount(1);
        await Assert.That(registry.Get("alpha")).IsNotNull();
    }

    /// <summary>
    ///     Test 2 — <see cref="AppReducer.TogglePanel" /> flips a Hidden panel to
    ///     Visible and back. Focused → Hidden also clears
    ///     <see cref="UiState.Ui.FocusedPanelId" />.
    /// </summary>
    [Test]
    public async Task TogglePanel_FlipsHiddenAndVisible()
    {
        // Seed UiState with the registered panel + Hidden state (mirror what
        // SpectreTuiRenderer.SeedPanelRegistryIntoState does).
        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = ImmutableArray.Create("alpha"),
                PanelStates = ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, TuiPanelState>("alpha", TuiPanelState.Hidden)
            })
            }
        };

        var afterShow = AppReducer.TogglePanel(state, "alpha");
        await Assert.That(afterShow.Ui.PanelStates["alpha"]).IsEqualTo(TuiPanelState.Visible);

        var afterHide = AppReducer.TogglePanel(afterShow, "alpha");
        await Assert.That(afterHide.Ui.PanelStates["alpha"]).IsEqualTo(TuiPanelState.Hidden);

        // Focused → Hidden must also clear focus.
        var focused = AppReducer.FocusPanel(afterShow, "alpha");
        await Assert.That(focused.Ui.PanelStates["alpha"]).IsEqualTo(TuiPanelState.Focused);
        await Assert.That(focused.Ui.FocusedPanelId).IsEqualTo("alpha");

        var hiddenFromFocused = AppReducer.TogglePanel(focused, "alpha");
        await Assert.That(hiddenFromFocused.Ui.PanelStates["alpha"]).IsEqualTo(TuiPanelState.Hidden);
        await Assert.That(hiddenFromFocused.Ui.FocusedPanelId).IsNull();
    }

    /// <summary>
    ///     Test 3 — Registering multiple panels preserves the registration order in
    ///     <see cref="PanelRegistry.All" />, which is what the Alt+1..Alt+9 hotkey
    ///     mapping depends on. Default placement is preserved per provider.
    /// </summary>
    [Test]
    public async Task All_PreservesRegistrationOrder()
    {
        var registry = new PanelRegistry();
        registry.Register(new StubPanel("left-1", "Left 1", TuiPanelPlacement.Left, 30));
        registry.Register(new StubPanel("bottom-1", "Bottom 1", TuiPanelPlacement.Bottom, 8));
        registry.Register(new StubPanel("right-1", "Right 1", TuiPanelPlacement.Right, 40));

        var all = registry.All;
        await Assert.That(all.Count).IsEqualTo(3);
        await Assert.That(all[0].Id).IsEqualTo("left-1");
        await Assert.That(all[1].Id).IsEqualTo("bottom-1");
        await Assert.That(all[2].Id).IsEqualTo("right-1");

        // Default placement is preserved.
        await Assert.That(all[0].DefaultPlacement).IsEqualTo(TuiPanelPlacement.Left);
        await Assert.That(all[1].DefaultPlacement).IsEqualTo(TuiPanelPlacement.Bottom);
        await Assert.That(all[2].DefaultPlacement).IsEqualTo(TuiPanelPlacement.Right);
    }

    /// <summary>
    ///     Test 4 — <see cref="AppReducer.CycleFocus" /> walks visible panels in
    ///     registration order. When the last visible panel is focused, a further
    ///     cycle returns focus to chat (FocusedPanelId = null).
    /// </summary>
    [Test]
    public async Task CycleFocus_WalksVisiblePanels_AndWrapsToChat()
    {
        var panelStates = ImmutableDictionary.CreateRange(new[]
        {
            new KeyValuePair<string, TuiPanelState>("p1", TuiPanelState.Visible),
            new KeyValuePair<string, TuiPanelState>("p2", TuiPanelState.Visible),
            new KeyValuePair<string, TuiPanelState>("p3", TuiPanelState.Hidden)
        });

        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = ImmutableArray.Create("p1", "p2", "p3"),
                PanelStates = panelStates,
                FocusedPanelId = null
            }
        };

        // Initial cycle: focus chat → first visible panel (p1).
        var s1 = AppReducer.CycleFocus(state);
        await Assert.That(s1.Ui.FocusedPanelId).IsEqualTo("p1");
        await Assert.That(s1.Ui.PanelStates["p1"]).IsEqualTo(TuiPanelState.Focused);

        // p1 → p2 (p3 is Hidden, skipped).
        var s2 = AppReducer.CycleFocus(s1);
        await Assert.That(s2.Ui.FocusedPanelId).IsEqualTo("p2");
        await Assert.That(s2.Ui.PanelStates["p2"]).IsEqualTo(TuiPanelState.Focused);
        await Assert.That(s2.Ui.PanelStates["p1"]).IsEqualTo(TuiPanelState.Visible);

        // p2 → chat (only 2 visible panels, so wrapping returns to chat).
        var s3 = AppReducer.CycleFocus(s2);
        await Assert.That(s3.Ui.FocusedPanelId).IsNull();
        await Assert.That(s3.Ui.PanelStates["p2"]).IsEqualTo(TuiPanelState.Visible);

        // chat → p1 again.
        var s4 = AppReducer.CycleFocus(s3);
        await Assert.That(s4.Ui.FocusedPanelId).IsEqualTo("p1");
    }

    /// <summary>
    ///     Test 5 — <see cref="AppReducer.ResizePanel" /> clamps the new size to
    ///     [<see cref="PanelRegistry.MinSize" />..<see cref="PanelRegistry.MaxSize" />].
    ///     Both grow and shrink paths clamp correctly.
    /// </summary>
    [Test]
    public async Task ResizePanel_ClampsToMinAndMax()
    {
        var panelSizes = ImmutableDictionary.CreateRange(new[]
        {
            new KeyValuePair<string, int>("p1", 24)
        });

        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = ImmutableArray.Create("p1"),
                PanelStates = ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, TuiPanelState>("p1", TuiPanelState.Visible)
            }),
                PanelSizes = panelSizes
            }
        };

        // Grow by 10 → 34.
        var grown = AppReducer.ResizePanel(state, "p1", 10);
        await Assert.That(grown.Ui.PanelSizes["p1"]).IsEqualTo(34);

        // Grow past max → clamped to MaxSize (200).
        var maxed = AppReducer.ResizePanel(grown, "p1", 1000);
        await Assert.That(maxed.Ui.PanelSizes["p1"]).IsEqualTo(PanelRegistry.MaxSize);

        // Shrink below min → clamped to MinSize (2).
        var mined = AppReducer.ResizePanel(maxed, "p1", -1000);
        await Assert.That(mined.Ui.PanelSizes["p1"]).IsEqualTo(PanelRegistry.MinSize);

        // Unknown id is a no-op.
        var unknown = AppReducer.ResizePanel(state, "does-not-exist", 10);
        await Assert.That(ReferenceEquals(unknown, state)).IsTrue();
    }

    /// <summary>
    ///     Test 6 — <see cref="PanelRegistryView.GetVisibleByPlacement" /> filters by
    ///     both visibility (Hidden excluded) and placement. Multiple visible panels
    ///     with the same placement are returned in registration order. State is read
    ///     from <see cref="UiState" />, not from the registry (TEA single source of truth).
    /// </summary>
    [Test]
    public async Task View_GetVisibleByPlacement_FiltersByStateAndPlacement()
    {
        var registry = new PanelRegistry();
        registry.Register(new StubPanel("left-1", "L1", TuiPanelPlacement.Left, 30));
        registry.Register(new StubPanel("left-2", "L2", TuiPanelPlacement.Left, 30));
        registry.Register(new StubPanel("right-1", "R1", TuiPanelPlacement.Right, 40));
        registry.Register(new StubPanel("bottom-1", "B1", TuiPanelPlacement.Bottom, 8));

        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = ImmutableArray.Create("left-1", "left-2", "right-1", "bottom-1"),
                PanelStates = ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, TuiPanelState>("left-1", TuiPanelState.Visible),
                new KeyValuePair<string, TuiPanelState>("left-2", TuiPanelState.Focused),
                new KeyValuePair<string, TuiPanelState>("right-1", TuiPanelState.Hidden),
                new KeyValuePair<string, TuiPanelState>("bottom-1", TuiPanelState.Hidden)
            })
            }
        };

        var view = registry.View(state);
        var leftVisible = view.GetVisibleByPlacement(TuiPanelPlacement.Left);
        await Assert.That(leftVisible.Count).IsEqualTo(2);
        await Assert.That(leftVisible[0].Id).IsEqualTo("left-1");
        await Assert.That(leftVisible[1].Id).IsEqualTo("left-2");

        var rightVisible = view.GetVisibleByPlacement(TuiPanelPlacement.Right);
        await Assert.That(rightVisible.Count).IsEqualTo(0);

        var bottomVisible = view.GetVisibleByPlacement(TuiPanelPlacement.Bottom);
        await Assert.That(bottomVisible.Count).IsEqualTo(0);

        // GetState / GetSize read from UiState, not the registry.
        await Assert.That(view.GetState("left-2")).IsEqualTo(TuiPanelState.Focused);
        await Assert.That(view.GetSize("left-1")).IsEqualTo(0); // not in PanelSizes
    }

    /// <summary>
    ///     Test 7 — Registering a panel with an existing id replaces it (no duplicate
    ///     ids) and preserves order. This is how plugin panels can override builtins.
    /// </summary>
    [Test]
    public async Task Register_DuplicateId_ReplacesInPlace()
    {
        var registry = new PanelRegistry();
        registry.Register(new StubPanel("alpha", "Original", TuiPanelPlacement.Left, 24));
        registry.Register(new StubPanel("beta", "Beta", TuiPanelPlacement.Right, 30));

        // Replace alpha with a new provider.
        registry.Register(new StubPanel("alpha", "Replaced", TuiPanelPlacement.Left, 32));

        var all = registry.All;
        await Assert.That(all.Count).IsEqualTo(2);
        await Assert.That(all[0].Id).IsEqualTo("alpha");
        await Assert.That(all[0].Title).IsEqualTo("Replaced");
        await Assert.That(all[0].DefaultSize).IsEqualTo(32);
        await Assert.That(all[1].Id).IsEqualTo("beta");
    }

    /// <summary>
    ///     Test 8 — Registering with an empty id returns failure (Result.IsSuccess = false).
    /// </summary>
    [Test]
    public async Task Register_EmptyId_ReturnsFailure()
    {
        var registry = new PanelRegistry();
        var panel = new StubPanel(string.Empty, "Empty", TuiPanelPlacement.Left, 24);

        var result = registry.Register(panel);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(registry.All).HasCount(0);
    }

    /// <summary>
    ///     Test 9 — <see cref="PanelRegistryView" /> is a snapshot; subsequent
    ///     registrations / state changes do not affect the captured view (struct
    ///     copy of the providers list + UiState reference).
    /// </summary>
    [Test]
    public async Task View_IsSnapshot_AfterMutation()
    {
        var registry = new PanelRegistry();
        registry.Register(new StubPanel("alpha", "Alpha", TuiPanelPlacement.Left, 24));

        var state1 = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = ImmutableArray.Create("alpha"),
                PanelStates = ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, TuiPanelState>("alpha", TuiPanelState.Hidden)
            })
            }
        };
        var view1 = registry.View(state1);
        await Assert.That(view1.GetState("alpha")).IsEqualTo(TuiPanelState.Hidden);

        // State changes — view1's snapshot is unaffected.
        var state2 = state1 with
        {
            Ui = state1.Ui with { PanelStates = state1.Ui.PanelStates.SetItem("alpha", TuiPanelState.Visible) }
        };
        await Assert.That(view1.GetState("alpha")).IsEqualTo(TuiPanelState.Hidden);

        var view2 = registry.View(state2);
        await Assert.That(view2.GetState("alpha")).IsEqualTo(TuiPanelState.Visible);
    }

    /// <summary>
    ///     Minimal <see cref="IPanelProvider" /> stub for tests. Returns a constant
    ///     widget (the string itself) so tests can assert on registry/reducer behaviour
    ///     without depending on Spectre.Tui.
    /// </summary>
    private sealed class StubPanel : IPanelProvider
    {
        public StubPanel(string id, string title, TuiPanelPlacement placement, int size)
        {
            Id = id;
            Title = title;
            DefaultPlacement = placement;
            DefaultSize = size;
        }

        public string Id { get; }
        public string Title { get; }
        public TuiPanelPlacement DefaultPlacement { get; }
        public int DefaultSize { get; }

        public object? Build(PanelContext ctx) => $"widget:{Id}";

        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }
}

/// <summary>
///     TEA compliance tests — assert that The Elm Architecture invariants hold:
///     <list type="bullet">
///         <item>All scroll actions go through <see cref="ChatAppReducer.Update" /> (no direct mutation).</item>
///         <item>
///             <see cref="SpectreTuiRenderer" />'s <c>ChatScreen</c> has no local mutable
///             scroll / viewport / was-running fields (they live in <see cref="UiState" />).
///         </item>
///         <item>
///             <see cref="IPanelRegistry" /> exposes only registration methods — no
///             state mutation surface (<c>SetState</c> / <c>SetSize</c> / <c>FocusedPanelId</c>
///             setter are all gone).
///         </item>
///         <item>
///             <see cref="ChatAppReducer.Update" /> handles <see cref="AppMsg.ScrollResetToTail" />
///             and <see cref="AppMsg.ScrollClamp" /> — the messages a pure render dispatches.
///         </item>
///     </list>
/// </summary>
public class TeaComplianceTests
{
    /// <summary>
    ///     <see cref="IPanelRegistry" /> exposes only registration methods. State mutation
    ///     methods (<c>SetState</c>, <c>SetSize</c>, <c>FocusedPanelId</c> setter) have
    ///     been removed — TEA: state lives in <see cref="UiState" />, mutated only by
    ///     <see cref="AppReducer" />.
    /// </summary>
    [Test]
    public async Task IPanelRegistry_HasOnlyRegistrationMethods()
    {
        var iface = typeof(IPanelRegistry);
        var methods = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var props = iface.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Allowed method names: Register, Unregister, Get (plus compiler-synthesized
        // property accessors like get_All if the binding flags surface them).
        var banned = new HashSet<string>(StringComparer.Ordinal)
        {
            "SetState", "SetSize", "GetState", "GetSize",
            "ApplySnapshot", "SnapshotStates", "SnapshotSizes", "CycleFocus"
        };
        foreach (var m in methods)
        {
            await Assert.That(banned.Contains(m.Name)).IsFalse();
        }

        // Allowed property names: All.
        foreach (var p in props)
        {
            await Assert.That(p.Name is "All").IsTrue();
        }

        // Explicitly: no SetState, no SetSize, no FocusedPanelId.
        await Assert.That(iface.GetMethod("SetState")).IsNull();
        await Assert.That(iface.GetMethod("SetSize")).IsNull();
        await Assert.That(iface.GetProperty("FocusedPanelId")).IsNull();
        await Assert.That(iface.GetMethod("GetState")).IsNull();
        await Assert.That(iface.GetMethod("GetSize")).IsNull();
        await Assert.That(iface.GetMethod("ApplySnapshot")).IsNull();
        await Assert.That(iface.GetMethod("SnapshotStates")).IsNull();
        await Assert.That(iface.GetMethod("SnapshotSizes")).IsNull();
        await Assert.That(iface.GetMethod("CycleFocus")).IsNull();
    }

    /// <summary>
    ///     <see cref="PanelRegistry" /> (the concrete impl) also exposes no state
    ///     mutation surface — only <see cref="PanelRegistry.Register" />,
    ///     <see cref="PanelRegistry.Unregister" />, <see cref="PanelRegistry.All" />,
    ///     <see cref="PanelRegistry.Get" />, and <see cref="PanelRegistry.View" />.
    /// </summary>
    [Test]
    public async Task PanelRegistry_HasNoStateMutationMethods()
    {
        var t = typeof(PanelRegistry);
        await Assert.That(t.GetMethod("SetState", BindingFlags.Public | BindingFlags.Instance)).IsNull();
        await Assert.That(t.GetMethod("SetSize", BindingFlags.Public | BindingFlags.Instance)).IsNull();
        await Assert.That(t.GetMethod("ApplySnapshot", BindingFlags.Public | BindingFlags.Instance)).IsNull();
        await Assert.That(t.GetMethod("SnapshotStates", BindingFlags.Public | BindingFlags.Instance)).IsNull();
        await Assert.That(t.GetMethod("SnapshotSizes", BindingFlags.Public | BindingFlags.Instance)).IsNull();
        await Assert.That(t.GetMethod("CycleFocus", BindingFlags.Public | BindingFlags.Instance)).IsNull();
        await Assert.That(t.GetProperty("FocusedPanelId", BindingFlags.Public | BindingFlags.Instance)).IsNull();
    }

    /// <summary>
    ///     <see cref="UiState.Ui.ScrollOffset" /> / <see cref="UiState.Ui.ViewportLines" /> /
    ///     <see cref="UiState.Ui.TotalLines" /> / <see cref="UiState.Chat.WasRunning" /> exist
    ///     (TEA: state in UiState, not in renderer fields).
    /// </summary>
    [Test]
    public async Task UiState_HasScrollViewportTotalLinesWasRunning()
    {
        var t = typeof(UiState);
        await Assert.That(t.GetProperty("ScrollOffset")).IsNotNull();
        await Assert.That(t.GetProperty("ViewportLines")).IsNotNull();
        await Assert.That(t.GetProperty("TotalLines")).IsNotNull();
        await Assert.That(t.GetProperty("WasRunning")).IsNotNull();
        await Assert.That(t.GetProperty("IsAgentRunning")).IsNotNull();
    }

    /// <summary>
    ///     <see cref="AppMsg.ScrollResetToTail" /> and <see cref="AppMsg.ScrollClamp" />
    ///     exist as message cases (TEA: render dispatches these — never mutates state).
    /// </summary>
    [Test]
    public async Task UiMsg_HasScrollResetToTailAndScrollClamp()
    {
        var msgBase = typeof(AppMsg);
        var nested = msgBase.GetNestedTypes(BindingFlags.Public);
        var names = nested.Select(t => t.Name).ToHashSet();
        await Assert.That(names.Contains("ScrollResetToTail")).IsTrue();
        await Assert.That(names.Contains("ScrollClamp")).IsTrue();
        await Assert.That(names.Contains("Viewport")).IsTrue();
        await Assert.That(names.Contains("HistoryMeasured")).IsTrue();
    }

    /// <summary>
    ///     <see cref="ChatAppReducer.Update" /> handles <see cref="AppMsg.ScrollResetToTail" />
    ///     by resetting <see cref="UiState.Ui.ScrollOffset" /> to 0 and setting
    ///     <see cref="UiState.Chat.WasRunning" /> to true (so the rising-edge detection in
    ///     render does not keep firing every frame).
    /// </summary>
    [Test]
    public async Task Reducer_ScrollResetToTail_SetsScrollZeroAndWasRunningTrue()
    {
        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                ScrollOffset = 42,
                TotalLines = 100,
                ViewportLines = 10
            },
            Chat = ChatDomainState.Empty with
            {
                WasRunning = false
            }
        };

        var (next, effect) = ChatAppReducer.Update(state, new AppMsg.ScrollResetToTail());

        await Assert.That(next.Ui.ScrollOffset).IsEqualTo(0);
        await Assert.That(next.Chat.WasRunning).IsTrue();
        await Assert.That(effect).IsTypeOf<TuiEffect.None>();
    }

    /// <summary>
    ///     <see cref="ChatAppReducer.Update" /> handles <see cref="AppMsg.ScrollClamp" /> by
    ///     clamping <see cref="UiState.Ui.ScrollOffset" /> to [0..MaxScroll].
    /// </summary>
    [Test]
    public async Task Reducer_ScrollClamp_ClampsScrollOffset()
    {
        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                ScrollOffset = 50
            }
        };

        var (next, _) = ChatAppReducer.Update(state, new AppMsg.ScrollClamp(MaxScroll: 20));
        await Assert.That(next.Ui.ScrollOffset).IsEqualTo(20);

        var (next2, _) = ChatAppReducer.Update(state, new AppMsg.ScrollClamp(MaxScroll: 100));
        await Assert.That(next2.Ui.ScrollOffset).IsEqualTo(50);

        var (next3, _) = ChatAppReducer.Update(state, new AppMsg.ScrollClamp(MaxScroll: -1));
        await Assert.That(next3.Ui.ScrollOffset).IsEqualTo(0);
    }

    /// <summary>
    ///     All <c>ChatAction.Scroll*</c> actions flow through
    ///     <see cref="ChatAppReducer.Update" /> — there is no <c>HandleLocalScroll</c>
    ///     shortcut in the renderer. Verified by checking that the reducer handles each
    ///     scroll action and updates <see cref="UiState.Ui.ScrollOffset" />.
    /// </summary>
    [Test]
    public async Task Reducer_HandlesAllScrollActions_ThroughUpdate()
    {
        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                ScrollOffset = 5,
                TotalLines = 100,
                ViewportLines = 10
            }
        };

        var (s1, _) = ChatAppReducer.Update(state, new AppMsg.KeyInput(ChatAction.ScrollUpLine, default));
        await Assert.That(s1.Ui.ScrollOffset).IsEqualTo(6);

        var (s2, _) = ChatAppReducer.Update(s1, new AppMsg.KeyInput(ChatAction.ScrollDownLine, default));
        await Assert.That(s2.Ui.ScrollOffset).IsEqualTo(5);

        var (s3, _) = ChatAppReducer.Update(s2, new AppMsg.KeyInput(ChatAction.ScrollUpPage, default));
        // Page = max(1, viewport-2) = 8.
        await Assert.That(s3.Ui.ScrollOffset).IsEqualTo(13);

        var (s4, _) = ChatAppReducer.Update(s3, new AppMsg.KeyInput(ChatAction.ScrollDownPage, default));
        await Assert.That(s4.Ui.ScrollOffset).IsEqualTo(5);

        var (s5, _) = ChatAppReducer.Update(s4, new AppMsg.KeyInput(ChatAction.ScrollTop, default));
        await Assert.That(s5.Ui.ScrollOffset).IsEqualTo(90); // max = 100 - 10

        var (s6, _) = ChatAppReducer.Update(s5, new AppMsg.KeyInput(ChatAction.ScrollBottom, default));
        await Assert.That(s6.Ui.ScrollOffset).IsEqualTo(0);
    }

    /// <summary>
    ///     <c>AgentStartEvent</c> snapshots <see cref="UiState.Chat.IsAgentRunning" /> into
    ///     <see cref="UiState.Chat.WasRunning" /> and resets <see cref="UiState.Ui.ScrollOffset" />
    ///     to 0 (so streaming output is always visible). This is the reducer-side
    ///     replacement for the old ChatScreen <c>_wasRunning</c> / <c>_scroll = 0</c>
    ///     mutation (§FP-005).
    /// </summary>
    [Test]
    public async Task Reducer_AgentStart_SnapshotsWasRunningAndResetsScroll()
    {
        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                ScrollOffset = 50,
                TotalLines = 100,
                ViewportLines = 10
            },
            Chat = ChatDomainState.Empty with
            {
                IsAgentRunning = false,
                WasRunning = false
            }
        };

        var next = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentStartEvent("s1", Array.Empty<AgentMessage>()))).State;

        await Assert.That(next.Chat.IsAgentRunning).IsTrue();
        await Assert.That(next.Chat.WasRunning).IsFalse(); // prior IsAgentRunning
        await Assert.That(next.Ui.ScrollOffset).IsEqualTo(0); // pinned to live tail
    }

    /// <summary>
    ///     <c>AgentEndEvent</c> snapshots <see cref="UiState.Chat.IsAgentRunning" /> into
    ///     <see cref="UiState.Chat.WasRunning" /> (so the next AgentStart can detect the
    ///     rising edge correctly).
    /// </summary>
    [Test]
    public async Task Reducer_AgentEnd_SnapshotsWasRunning()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                IsAgentRunning = true,
                WasRunning = false
            }
        };

        var next = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent(Array.Empty<AgentMessage>()))).State;

        await Assert.That(next.Chat.IsAgentRunning).IsFalse();
        await Assert.That(next.Chat.WasRunning).IsTrue(); // prior IsAgentRunning
    }

    /// <summary>
    ///     <see cref="SpectreTuiRenderer" />'s private <c>ChatScreen</c> class has no
    ///     mutable instance fields named <c>_scroll</c> or
    ///     <c>_wasRunning</c>. These were the §FP-005 violation — they used to bypass
    ///     the reducer and store state locally. After the fix, all state lives in
    ///     <see cref="UiState" />.
    /// </summary>
    [Test]
    public async Task ChatScreen_HasNoLocalScrollViewportWasRunningFields()
    {
        var rendererType = typeof(SpectreTuiRenderer);
        // ChatScreen is a private nested class.
        var chatScreenType = rendererType.GetNestedType("ChatScreen", BindingFlags.NonPublic);
        await Assert.That(chatScreenType).IsNotNull();

        var fields = chatScreenType!
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.Name)
            .ToHashSet();

        await Assert.That(fields.Contains("_scroll")).IsFalse();
        await Assert.That(fields.Contains("_wasRunning")).IsFalse();
    }

    /// <summary>
    ///     <see cref="SpectreTuiRenderer" />'s private <c>ChatScreen</c> class has no
    ///     method named <c>HandleLocalScroll</c>. The TEA fix routes every scroll
    ///     action through <see cref="ChatAppReducer.Update" /> via
    ///     <see cref="UiStore.Dispatch(AppMsg)" />.
    /// </summary>
    [Test]
    public async Task ChatScreen_HasNoHandleLocalScrollMethod()
    {
        var rendererType = typeof(SpectreTuiRenderer);
        var chatScreenType = rendererType.GetNestedType("ChatScreen", BindingFlags.NonPublic);
        await Assert.That(chatScreenType).IsNotNull();

        var method = chatScreenType!.GetMethod(
            "HandleLocalScroll",
            BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(method).IsNull();
    }

    /// <summary>
    ///     <see cref="SpectreTuiRenderer" />'s private <c>ChatScreen</c> class has no
    ///     method named <c>SyncRegistryFromState</c>. The TEA fix removes the
    ///     "double source of truth" bridge — state lives only in <see cref="UiState" />.
    /// </summary>
    [Test]
    public async Task ChatScreen_HasNoSyncRegistryFromStateMethod()
    {
        var rendererType = typeof(SpectreTuiRenderer);
        var chatScreenType = rendererType.GetNestedType("ChatScreen", BindingFlags.NonPublic);
        await Assert.That(chatScreenType).IsNotNull();

        var method = chatScreenType!.GetMethod(
            "SyncRegistryFromState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(method).IsNull();
    }

    /// <summary>
    ///     <see cref="SpectreTuiRenderer" />'s <c>SyncPanelRegistryToState</c> method
    ///     has been removed (it was the registry→state bridge; the registry is now
    ///     registration-only). Replaced by <c>SeedPanelRegistryIntoState</c>.
    /// </summary>
    [Test]
    public async Task SpectreTuiRenderer_HasNoSyncPanelRegistryToState()
    {
        var t = typeof(SpectreTuiRenderer);
        var method = t.GetMethod(
            "SyncPanelRegistryToState",
            BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(method).IsNull();

        var seed = t.GetMethod(
            "SeedPanelRegistryIntoState",
            BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(seed).IsNotNull();
    }
}
