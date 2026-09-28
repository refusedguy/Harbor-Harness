using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Acceptance tests for the GENERIC half of the TEA split (#33/T4, #364):
///     <see cref="AppReducer" /> with no domain extension at all.
/// </summary>
/// <remarks>
///     <para>
///         This file deliberately references no Harbor domain type — no
///         <c>AgentEvent</c>, no <c>ChatLine</c>, no <c>ChatRole</c>. If any of
///         these tests ever needs one, the generic reducer has leaked domain
///         knowledge and the split has regressed.
///     </para>
///     <para>
///         Coverage: panels (toggle / focus / cycle / resize / seed / cursor /
///         directory), scroll (line / page / top / bottom / clamp / reset),
///         input editing (char / backspace / history / autocomplete /
///         newline), focus toggle, quit and reset — plus the busy gate injected
///         through a stub <see cref="IAppReducerPlugin" /> and the ordering
///         contract (extension claims → generic → extension folds).
///     </para>
/// </remarks>
public class AppReducerGenericTests
{
    private static UiState WithPanels(params string[] ids) =>
        new()
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = [.. ids],
                PanelStates = Enumerable.Range(0, ids.Length)
                    .ToImmutableDictionary(i => ids[i], _ => TuiPanelState.Hidden)
            }
        };

    private static UiState Measured(int viewport, int total) =>
        new() { Ui = TerminalUiState.Empty with { ViewportLines = viewport, TotalLines = total } };

    // ── panels ─────────────────────────────────────────────────────────────

    [Test]
    public async Task TogglePanel_FlipsHiddenToVisible()
    {
        var result = AppReducer.Update(WithPanels("alpha"), new AppMsg.TogglePanel("alpha"));
        await Assert.That(result.State.Ui.PanelStates["alpha"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task TogglePanel_UnknownId_IsNoOp()
    {
        var state = WithPanels("alpha");
        var result = AppReducer.Update(state, new AppMsg.TogglePanel("does-not-exist"));
        await Assert.That(result.State).IsSameReferenceAs(state);
    }

    [Test]
    public async Task TogglePanel_FocusedPanel_ClearsFocus()
    {
        var state = AppReducer.FocusPanel(WithPanels("alpha"), "alpha");
        var result = AppReducer.Update(state, new AppMsg.TogglePanel("alpha"));
        await Assert.That(result.State.Ui.FocusedPanelId).IsNull();
    }

    [Test]
    public async Task FocusPanel_Null_DemotesPreviousFocus()
    {
        var focused = AppReducer.FocusPanel(WithPanels("alpha"), "alpha");
        var result = AppReducer.Update(focused, new AppMsg.FocusPanel(null));
        await Assert.That(result.State.Ui.FocusedPanelId).IsNull();
        await Assert.That(result.State.Ui.PanelStates["alpha"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task CyclePanelFocus_WalksVisiblePanelsThenReturnsToChat()
    {
        var state = WithPanels("a", "b");
        state = AppReducer.Update(state, new AppMsg.TogglePanel("a")).State;
        state = AppReducer.Update(state, new AppMsg.TogglePanel("b")).State;

        var first = AppReducer.Update(state, new AppMsg.CyclePanelFocus());
        await Assert.That(first.State.Ui.FocusedPanelId).IsEqualTo("a");

        var second = AppReducer.Update(first.State, new AppMsg.CyclePanelFocus());
        await Assert.That(second.State.Ui.FocusedPanelId).IsEqualTo("b");

        var third = AppReducer.Update(second.State, new AppMsg.CyclePanelFocus());
        await Assert.That(third.State.Ui.FocusedPanelId).IsNull();
    }

    [Test]
    public async Task ResizePanel_ClampsToRegistryBounds()
    {
        var state = WithPanels("a");
        // Unseeded panel: current size is 0, so +5 lands on 5 (inside the clamp).
        var grown = AppReducer.Update(state, new AppMsg.ResizePanel("a", 5));
        await Assert.That(grown.State.Ui.PanelSizes["a"]).IsEqualTo(5);

        var maxed = AppReducer.Update(state, new AppMsg.ResizePanel("a", 10_000));
        await Assert.That(maxed.State.Ui.PanelSizes["a"]).IsEqualTo(PanelRegistry.MaxSize);

        var mined = AppReducer.Update(state, new AppMsg.ResizePanel("a", -10_000));
        await Assert.That(mined.State.Ui.PanelSizes["a"]).IsEqualTo(PanelRegistry.MinSize);

        // Seeding a size first makes the delta relative to it.
        var seeded = AppReducer.Update(state, new AppMsg.ResizePanel("a", 20));
        var bigger = AppReducer.Update(seeded.State, new AppMsg.ResizePanel("a", 5));
        await Assert.That(bigger.State.Ui.PanelSizes["a"]).IsEqualTo(25);
    }

    [Test]
    public async Task SeedPanels_ReplacesRegistrySnapshot()
    {
        var result = AppReducer.Update(new UiState(), new AppMsg.SeedPanels(
            ["x", "y"],
            ImmutableDictionary<string, TuiPanelState>.Empty.SetItem("x", TuiPanelState.Visible),
            ImmutableDictionary<string, int>.Empty.SetItem("x", 12)));
        await Assert.That(result.State.Ui.RegisteredPanelIds).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(result.State.Ui.PanelStates["x"]).IsEqualTo(TuiPanelState.Visible);
        await Assert.That(result.State.Ui.PanelSizes["x"]).IsEqualTo(12);
    }

    [Test]
    public async Task SetPanelCursor_ClampsNegativeToZero()
    {
        var result = AppReducer.Update(new UiState(), new AppMsg.SetPanelCursor("diag", -3));
        await Assert.That(result.State.Ui.PanelCursors["diag"]).IsEqualTo(0);
    }

    [Test]
    public async Task SetPanelDirectory_ResetsCursorAtomically()
    {
        var state = AppReducer.Update(new UiState(), new AppMsg.SetPanelCursor("tree", 4)).State;
        var result = AppReducer.Update(state, new AppMsg.SetPanelDirectory("tree", "/tmp"));
        await Assert.That(result.State.Ui.PanelDirs["tree"]).IsEqualTo("/tmp");
        await Assert.That(result.State.Ui.PanelCursors["tree"]).IsEqualTo(0);
    }

    // ── scroll ─────────────────────────────────────────────────────────────

    [Test]
    public async Task KeyInput_ScrollUpLine_IncrementsOffsetWithinBounds()
    {
        var state = Measured(10, 100);
        var result = AppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up)));
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(1);
    }

    [Test]
    public async Task KeyInput_ScrollTop_ClampsToMaxScroll()
    {
        var state = Measured(10, 100);
        var result = AppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.ScrollTop, new UiKey(UiKeyCode.Home)));
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(90);
    }

    [Test]
    public async Task KeyInput_ScrollBottom_PinsToTail()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ViewportLines = 10, TotalLines = 100, ScrollOffset = 40 } };
        var result = AppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.ScrollBottom, new UiKey(UiKeyCode.End)));
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task ScrollClamp_ClampsToReportedMaximum()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ViewportLines = 10, TotalLines = 100, ScrollOffset = 80 } };
        var result = AppReducer.Update(state, new AppMsg.ScrollClamp(20));
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(20);
    }

    [Test]
    public async Task ScrollClamp_NegativeMaximum_ClampsToZero()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ViewportLines = 10, TotalLines = 100, ScrollOffset = 5 } };
        var result = AppReducer.Update(state, new AppMsg.ScrollClamp(-1));
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task ScrollResetToTail_PinsOffsetToZero()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 30 } };
        var result = AppReducer.Update(state, new AppMsg.ScrollResetToTail());
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task ScrollPercent_IsDerivedFromTheGenericPart()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ViewportLines = 10, TotalLines = 110, ScrollOffset = 50 } };
        await Assert.That(state.Ui.ScrollPercent).IsEqualTo(50);
    }

    // ── focus / input ──────────────────────────────────────────────────────

    [Test]
    public async Task KeyInput_ToggleFocus_FlipsFocusMode()
    {
        var result = AppReducer.Update(new UiState(),
            new AppMsg.KeyInput(ChatAction.ToggleFocus, new UiKey(UiKeyCode.Tab)));
        await Assert.That(result.State.Ui.Focus).IsEqualTo(FocusMode.Chat);
    }

    [Test]
    public async Task KeyInput_Char_AppendsToInputBox()
    {
        var result = AppReducer.Update(new UiState(),
            new AppMsg.KeyInput(ChatAction.Char, UiKey.ForChar('h')));
        await Assert.That(result.State.Ui.Input.Text).IsEqualTo("h");
    }

    [Test]
    public async Task KeyInput_Char_IgnoredWhenPanelOwnsFocus()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { Focus = FocusMode.Chat } };
        var result = AppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Char, UiKey.ForChar('h')));
        await Assert.That(result.State.Ui.Input.Text).IsEmpty();
    }

    [Test]
    public async Task KeyInput_Backspace_RemovesLastChar()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { Input = new InputModel("abc", [], -1) } };
        var result = AppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Backspace, new UiKey(UiKeyCode.Backspace)));
        await Assert.That(result.State.Ui.Input.Text).IsEqualTo("ab");
    }

    [Test]
    public async Task InputText_ReplacesInputBoxContent()
    {
        var result = AppReducer.Update(new UiState(), new AppMsg.InputText("/models"));
        await Assert.That(result.State.Ui.Input.Text).IsEqualTo("/models");
    }

    [Test]
    public async Task KeyInput_InsertNewline_AppendsNewline()
    {
        var result = AppReducer.Update(new UiState(),
            new AppMsg.KeyInput(ChatAction.InsertNewline, new UiKey(UiKeyCode.Enter)));
        await Assert.That(result.State.Ui.Input.Text).IsEqualTo("\n");
    }

    [Test]
    public async Task KeyInput_InsertNewline_CtrlEnterIsDropped()
    {
        var result = AppReducer.Update(new UiState(),
            new AppMsg.KeyInput(ChatAction.InsertNewline,
                new UiKey(UiKeyCode.Enter, KeyModifierSet.Ctrl)));
        await Assert.That(result.State.Ui.Input.Text).IsEmpty();
    }

    [Test]
    public async Task KeyInput_Autocomplete_OnlyForSlashPrefix()
    {
        var typed = AppReducer.Update(new UiState(),
            new AppMsg.KeyInput(ChatAction.Char, UiKey.ForChar('/'))).State;
        var result = AppReducer.Update(typed,
            new AppMsg.KeyInput(ChatAction.Autocomplete, new UiKey(UiKeyCode.Tab)));
        await Assert.That(result.State.Ui.Input.Text).StartsWith("/");
    }

    // ── lifecycle ──────────────────────────────────────────────────────────

    [Test]
    public async Task Quit_SetsShouldQuitFlag()
    {
        var result = AppReducer.Update(new UiState(), new AppMsg.Quit());
        await Assert.That(result.State.Ui.ShouldQuit).IsTrue();
    }

    [Test]
    public async Task Reset_ReturnsFreshState()
    {
        var dirty = AppReducer.Update(WithPanels("a"), new AppMsg.Quit()).State;
        var result = AppReducer.Update(dirty, new AppMsg.Reset());
        await Assert.That(result.State).IsEqualTo(new UiState());
    }

    [Test]
    public async Task ViewportAndHistoryMeasured_UpdateGeometry()
    {
        var state = AppReducer.Update(new UiState(), new AppMsg.Viewport(24)).State;
        state = AppReducer.Update(state, new AppMsg.HistoryMeasured(240)).State;
        await Assert.That(state.Ui.ViewportLines).IsEqualTo(24);
        await Assert.That(state.Ui.TotalLines).IsEqualTo(240);
    }

    // ── the extension contract ─────────────────────────────────────────────

    [Test]
    public async Task BusyExtension_SuppressesEditingButKeepsScroll()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ViewportLines = 10, TotalLines = 100, ScrollOffset = 3 } };

        var typed = AppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Char, UiKey.ForChar('x')), BusyPlugin.Instance);
        await Assert.That(typed.State.Ui.Input.Text).IsEmpty();

        var scrolled = AppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up)), BusyPlugin.Instance);
        await Assert.That(scrolled.State.Ui.ScrollOffset).IsEqualTo(4);
    }

    [Test]
    public async Task Extension_ClaimsMessageBeforeGenericArms()
    {
        var result = AppReducer.Update(new UiState(), new AppMsg.Quit(), BusyPlugin.Instance);
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.QuitApp>();
    }

    [Test]
    public async Task Extension_AfterHook_FoldsOnTopOfGenericTransition()
    {
        var result = AppReducer.Update(
            new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 12 } },
            new AppMsg.ScrollResetToTail(),
            BusyPlugin.Instance);
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(0);
        await Assert.That(result.State.Chat.Status).IsEqualTo("folded-by-extension");
    }

    /// <summary>
    ///     Minimal stateless plugin: claims <see cref="AppMsg.Quit" /> with its own
    ///     effect, reports busy, and folds a marker status on every transition.
    ///     Proves the generic reducer is reachable/extendable without a fork.
    /// </summary>
    private sealed class BusyPlugin : IAppReducerPlugin
    {
        public static readonly BusyPlugin Instance = new();

        public ReduceResult? Reduce(UiState state, AppMsg msg) => msg switch
        {
            AppMsg.Quit => new ReduceResult(state, new TuiEffect.QuitApp()),
            _ => null
        };

        public bool IsBusy(UiState state) => true;

        public UiState After(UiState state, AppMsg msg) =>
            state with { Chat = state.Chat with { Status = "folded-by-extension" } };
    }
}
