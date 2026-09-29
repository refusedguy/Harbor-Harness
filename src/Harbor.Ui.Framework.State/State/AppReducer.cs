using Harbor.Ui.Framework.Panels;
namespace Harbor.Ui.Framework.State;

/// <summary>
///     Result of a reducer transition: the next immutable <see cref="UiState" />
///     plus the <see cref="TuiEffect" /> the host should run. A readonly record
///     struct (not a tuple) so the plugin hook can answer "not my message" with
///     <see langword="null" /> instead of a sentinel state.
/// </summary>
/// <param name="State">The next immutable snapshot.</param>
/// <param name="Effect">The effect for the host to run (<see cref="TuiEffect.None" /> when there is none).</param>
public readonly record struct ReduceResult(UiState State, TuiEffect Effect)
{
    /// <summary>A transition that only changes state (no host effect).</summary>
    public static ReduceResult NoOp(UiState state) => new(state, new TuiEffect.None());

    /// <summary>Keep the deconstruction shape of the pre-split <c>(UiState, TuiEffect)</c> tuple.</summary>
    public void Deconstruct(out UiState state, out TuiEffect effect)
    {
        state = State;
        effect = Effect;
    }
}

/// <summary>
///     Extension point that lets a domain add its own arms to the generic
///     <see cref="AppReducer" /> without forking it (OCP, #33/T4).
/// </summary>
/// <remarks>
///     <para>
///         The generic reducer calls the members in this order for every message:
///     </para>
///     <list type="number">
///         <item><description><see cref="Reduce" /> — the domain gets first claim.</description></item>
///         <item><description>
///             the generic arms (only reached when <see cref="Reduce" /> returned
///             <see langword="null" />).
///         </description></item>
///         <item><description><see cref="After" /> — the domain folds its own state on top.</description></item>
///     </list>
///     <para>
///         <see cref="IsBusy" /> is a policy <em>query</em>, not a reduction: the
///         generic input gate asks it whether a key press may edit/commit the
///         input box, so the generic reducer never has to know what "running"
///         means.
///     </para>
/// </remarks>
public interface IAppReducerPlugin
{
    /// <summary>
    ///     Try to consume <paramref name="msg" />. Return <see langword="null" />
    ///     for any message this domain does not own so the generic pass runs.
    /// </summary>
    ReduceResult? Reduce(UiState state, AppMsg msg);

    /// <summary>
    ///     Whether a domain operation is in flight for <paramref name="state" />.
    ///     While busy the generic reducer suppresses input editing and commits but
    ///     still allows scroll, focus and panel keys (abort/quit routing is the
    ///     domain's own claim). The state is passed in rather than captured so the
    ///     reducer stays pure and the plugin stays stateless.
    /// </summary>
    bool IsBusy(UiState state);

    /// <summary>
    ///     Fold domain state on top of a transition the generic reducer already
    ///     applied (e.g. the rising-edge snapshot on
    ///     <see cref="AppMsg.ScrollResetToTail" />). Return <paramref name="state" />
    ///     unchanged when there is nothing to add.
    /// </summary>
    UiState After(UiState state, AppMsg msg) => state;
}

/// <summary>
///     Generic, domain-free reducer: <c>(UiState, AppMsg) → ReduceResult</c>.
///     Panels, scroll, input editing, focus and quit all live here. The Harbor
///     chat half (<see cref="ChatAppReducer" />) plugs in through
///     <see cref="IAppReducerPlugin" />, so a non-chat host can use this class
///     alone.
/// </summary>
/// <remarks>
///     <para>
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///         responsibility of <see cref="ITuiEffectRunner" />.
///     </para>
///     <para>
///         <b>Ordering.</b> <see cref="Update" /> runs the extension first, the
///         generic arms second, and the extension's <c>After</c> hook last. Reasons:
///         (1) the extension owns the <see cref="IAppReducerPlugin.IsBusy" /> input
///         gate, so it must see a key press before the generic pass decides to
///         accept it; (2) the extension returns <see langword="null" /> for anything
///         it does not own, so the generic arms stay deterministic and unshadowable
///         for a plain framework host; (3) running the domain first lets a domain arm
///         normalize state (e.g. pin scroll on run start) before the generic pass
///         observes it, and running <c>After</c> last lets it fold state that only
///         exists after the generic pass.
///     </para>
/// </remarks>
public static class AppReducer
{
    /// <summary>
    ///     The single update function for the interactive UI. Maps any
    ///     <see cref="AppMsg" /> to the next immutable <see cref="UiState" /> plus an
    ///     optional <see cref="TuiEffect" /> for the host to run.
    /// </summary>
    /// <param name="state">Current snapshot.</param>
    /// <param name="msg">The message to fold.</param>
    /// <param name="extension">Optional domain extension (see <see cref="IAppReducerPlugin" />).</param>
    public static ReduceResult Update(UiState state, AppMsg msg, IAppReducerPlugin? extension = null)
    {
        // Phase 1 — the domain claims the message.
        if (extension is not null && extension.Reduce(state, msg) is { } claimed)
            return claimed;

        // Phase 2 — the framework's own arms.
        ReduceResult result = ReduceGeneric(state, msg, extension?.IsBusy(state) ?? false);

        // Phase 3 — the domain folds its own state on top of the generic pass.
        if (extension is null || ReferenceEquals(result.State, state))
            return result;
        return new ReduceResult(extension.After(result.State, msg), result.Effect);
    }

    /// <summary>
    ///     The framework-owned half: every generic <see cref="AppMsg" /> arm.
    /// </summary>
    private static ReduceResult ReduceGeneric(UiState state, AppMsg msg, bool busy) => msg switch
    {
        AppMsg.InputText it => ReduceResult.NoOp(state.SetInput(state.Ui.Input.SetText(it.Text))),
        AppMsg.Quit => new ReduceResult(state with { Ui = state.Ui with { ShouldQuit = true } }, new TuiEffect.None()),
        AppMsg.Reset => new ReduceResult(new UiState(), new TuiEffect.None()),
        AppMsg.KeyInput k => UpdateKey(state, k, busy),
        AppMsg.Viewport v => ReduceResult.NoOp(state with { Ui = state.Ui with { ViewportLines = v.HistoryHeight } }),
        AppMsg.HistoryMeasured t => ReduceResult.NoOp(state with { Ui = state.Ui with { TotalLines = t.TotalLines } }),
        AppMsg.TogglePanel tp => ReduceResult.NoOp(TogglePanel(state, tp.Id)),
        AppMsg.FocusPanel fp => ReduceResult.NoOp(FocusPanel(state, fp.Id)),
        AppMsg.CyclePanelFocus => ReduceResult.NoOp(CycleFocus(state)),
        AppMsg.ResizePanel rp => ReduceResult.NoOp(ResizePanel(state, rp.Id, rp.Delta)),
        AppMsg.ScrollResetToTail => ReduceResult.NoOp(state with { Ui = state.Ui with { ScrollOffset = 0 } }),
        AppMsg.ScrollClamp sc => ReduceResult.NoOp(state with
        {
            Ui = state.Ui with
            {
                ScrollOffset = Math.Clamp(state.Ui.ScrollOffset, 0, Math.Max(0, sc.MaxScroll))
            }
        }),
        AppMsg.SeedPanels sp => ReduceResult.NoOp(state with
        {
            Ui = state.Ui with
            {
                RegisteredPanelIds = sp.Ids,
                PanelStates = sp.States,
                PanelSizes = sp.Sizes
            }
        }),
        AppMsg.SetPanelCursor pc => ReduceResult.NoOp(SetPanelCursor(state, pc.Id, pc.Cursor)),
        AppMsg.SetPanelDirectory pd => ReduceResult.NoOp(SetPanelDirectory(state, pd.Id, pd.Directory)),
        AppMsg.SetFileTreePending ft => ReduceResult.NoOp(SetFileTree(state, ft.Id, FileTreeSnapshot.Pending(ft.Directory))),
        AppMsg.SetFileTreeLoaded fl => ReduceResult.NoOp(SetFileTree(state, fl.Id, FileTreeSnapshot.Completed(
            fl.Directory,
            fl.Entries,
            fl.Truncated,
            fl.TotalCount))),
        AppMsg.SetFileTreeFailed ff => ReduceResult.NoOp(SetFileTree(state, ff.Id, FileTreeSnapshot.Failed(ff.Directory, ff.Error))),
        AppMsg.InvalidateFileTree inv => ReduceResult.NoOp(InvalidateFileTree(state, inv.Id)),
        _ => ReduceResult.NoOp(state)
    };

    // ── panel transitions (pure; no IPanelRegistry dependency) ────────────

    /// <summary>Toggle a panel between Hidden ↔ Visible (Focused → Hidden also clears focus).</summary>
    public static UiState TogglePanel(UiState state, string id)
    {
        var panels = state.Ui.PanelStates;
        if (string.IsNullOrEmpty(id) || !panels.ContainsKey(id))
            return state;

        var current = panels[id];
        var next = current == TuiPanelState.Hidden ? TuiPanelState.Visible : TuiPanelState.Hidden;
        var states = panels.SetItem(id, next);

        string? focused = state.Ui.FocusedPanelId;
        if (next == TuiPanelState.Hidden && focused == id)
            focused = null;

        return state with { Ui = state.Ui with { PanelStates = states, FocusedPanelId = focused } };
    }

    /// <summary>Focus a specific panel (or chat when <paramref name="id" /> is null).</summary>
    public static UiState FocusPanel(UiState state, string? id)
    {
        var panels = state.Ui.PanelStates;
        if (id is null)
        {
            // Demote any Focused panel back to Visible.
            var states = panels;
            if (state.Ui.FocusedPanelId is { } prev && states.ContainsKey(prev))
                states = states.SetItem(prev, TuiPanelState.Visible);
            return state with { Ui = state.Ui with { PanelStates = states, FocusedPanelId = null } };
        }

        if (!panels.ContainsKey(id))
            return state;

        var next = panels;
        if (state.Ui.FocusedPanelId is { } prevFocused && next.ContainsKey(prevFocused) && prevFocused != id)
            next = next.SetItem(prevFocused, TuiPanelState.Visible);
        // Make sure the target is at least Visible before focusing.
        if (next[id] == TuiPanelState.Hidden)
            next = next.SetItem(id, TuiPanelState.Visible);
        next = next.SetItem(id, TuiPanelState.Focused);

        return state with { Ui = state.Ui with { PanelStates = next, FocusedPanelId = id } };
    }

    /// <summary>
    ///     Cycle focus to the next visible panel in registration order. If the last
    ///     visible panel is already focused, returns focus to chat (null).
    /// </summary>
    public static UiState CycleFocus(UiState state)
    {
        var visible = state.Ui.RegisteredPanelIds
            .Where(id => state.Ui.PanelStates.TryGetValue(id, out var s) && s != TuiPanelState.Hidden)
            .ToList();
        if (visible.Count == 0)
            return state.Ui.FocusedPanelId is null ? state : FocusPanel(state, null);

        int idx = visible.IndexOf(state.Ui.FocusedPanelId ?? string.Empty);
        int nextIdx = idx < 0 ? 0 : (idx + 1) % visible.Count;
        if (idx >= 0 && nextIdx == 0)
            return FocusPanel(state, null);

        return FocusPanel(state, visible[nextIdx]);
    }

    /// <summary>Grow or shrink a panel by <paramref name="delta" />, clamped to [2..200].</summary>
    public static UiState ResizePanel(UiState state, string id, int delta)
    {
        var ui = state.Ui;
        if (string.IsNullOrEmpty(id) || !ui.PanelStates.ContainsKey(id) || delta == 0)
            return state;
        int current = ui.PanelSizes.TryGetValue(id, out int s) ? s : 0;
        int next = Math.Clamp(current + delta, PanelRegistry.MinSize, PanelRegistry.MaxSize);
        return state with { Ui = ui with { PanelSizes = ui.PanelSizes.SetItem(id, next) } };
    }

    /// <summary>
    ///     Store a panel-local cursor keyed by panel id (#360). Pure: no I/O,
    ///     no clamping against content (the provider clamps for display and on
    ///     write using the live item count). Unknown/empty ids still record so
    ///     null-store-degraded providers converge on the next seeded frame.
    /// </summary>
    public static UiState SetPanelCursor(UiState state, string id, int cursor)
    {
        if (string.IsNullOrEmpty(id))
            return state;
        int next = Math.Max(0, cursor);
        var ui = state.Ui;
        if (ui.PanelCursors.TryGetValue(id, out int current) && current == next)
            return state;
        return state with { Ui = ui with { PanelCursors = ui.PanelCursors.SetItem(id, next) } };
    }

    /// <summary>
    ///     Store a panel-local directory keyed by panel id and reset its cursor
    ///     to 0 atomically (#360). The filesystem listing itself lives in
    ///     <see cref="TerminalUiState.FileTrees" />, so this handler is still
    ///     pure — but note what it does NOT do: it does not clear the old
    ///     listing. A listing for a different directory is already inert, because
    ///     <see cref="TerminalUiState.FileTreeFor" /> only answers for the
    ///     directory the view is pointed at. Clearing here instead would make
    ///     the panel flash "(loading)" on every navigation step, including
    ///     steps where a perfectly good listing is one keypress away in the
    ///     reducer's own history.
    /// </summary>
    public static UiState SetPanelDirectory(UiState state, string id, string directory)
    {
        if (string.IsNullOrEmpty(id))
            return state;
        string dir = directory ?? string.Empty;
        var ui = state.Ui;
        bool sameDir = ui.PanelDirs.TryGetValue(id, out string? current) ? current == dir : dir == string.Empty;
        bool cursorZero = !ui.PanelCursors.TryGetValue(id, out int cur) || cur == 0;
        if (sameDir && cursorZero)
            return state;
        return state with
        {
            Ui = ui with
            {
                PanelDirs = ui.PanelDirs.SetItem(id, dir),
                PanelCursors = ui.PanelCursors.SetItem(id, 0)
            }
        };
    }

    // ── file-tree transitions (#667) ───────────────────────────────────────
    //
    // Pure on purpose. The walk happens in `FileTreeLoader` behind the Domain
    // `IDirectoryLister` port; what lands here is a RESULT, addressed to the
    // directory it was produced for. The staleness guard below is the reducer's
    // contribution to the design: a load that finishes after the user has
    // already navigated must not repaint the tree, and "has the user navigated"
    // is exactly the kind of question only the state can answer.

    /// <summary>
    ///     Store a file-tree snapshot (#667), dropping any result whose directory
    ///     the panel is no longer pointed at.
    /// </summary>
    /// <param name="state">Current snapshot.</param>
    /// <param name="id">The panel id owning the listing.</param>
    /// <param name="snapshot">The snapshot to store.</param>
    /// <remarks>
    ///     The guard is not a micro-optimisation, it is the last line of defence
    ///     against a torn frame. The loader also checks, on its side, that it is
    ///     still the owner of the in-flight walk — but the loader is a
    ///     Presentation service outside the state machine and could be replaced,
    ///     re-implemented or bypassed. This check lives where the truth does.
    /// </remarks>
    public static UiState SetFileTree(UiState state, string id, FileTreeSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(id) || snapshot is null)
            return state;

        // A message carrying the empty "means CWD" marker has not been resolved
        // by its sender, and resolving it here would make the reducer's answer
        // depend on the process CWD — a fact the message itself does not carry.
        if (string.IsNullOrEmpty(snapshot.Directory))
            return state;

        var ui = state.Ui;
        if (!string.Equals(ui.ResolvePanelDirectory(id), snapshot.Directory, StringComparison.Ordinal))
            return state;

        if (ui.FileTrees.TryGetValue(id, out FileTreeSnapshot current) && current == snapshot)
            return state;

        return state with { Ui = ui with { FileTrees = ui.FileTrees.SetItem(id, snapshot) } };
    }

    /// <summary>
    ///     Drop the file-tree listing for <paramref name="id" /> so the next
    ///     demand re-loads it (#667) - the <c>r</c> key.
    /// </summary>
    /// <param name="state">Current snapshot.</param>
    /// <param name="id">The panel id owning the listing.</param>
    public static UiState InvalidateFileTree(UiState state, string id)
    {
        if (string.IsNullOrEmpty(id))
            return state;
        var ui = state.Ui;
        if (!ui.FileTrees.ContainsKey(id))
            return state;

        // SetItem with FileTreeSnapshot.None rather than Remove: the panel's
        // demand check asks "is there a snapshot for my directory", and a MISSING
        // key and a present-but-empty snapshot are the same answer there. Keeping
        // the key means the invalidation is observable as a state change
        // (a revision bump, a repaint) instead of a no-op the store would drop.
        return state with
        {
            Ui = ui with { FileTrees = ui.FileTrees.SetItem(id, FileTreeSnapshot.None) }
        };
    }

    // ── key input (generic actions only; chat actions are the extension's) ──

    /// <summary>
    ///     Generic key handling. While <paramref name="busy" /> is true only the
    ///     "safe while an operation is in flight" subset is accepted (scroll, focus,
    ///     panels); everything else is dropped so a running run is never disturbed.
    ///     Submit / abort / quit / clear are NOT handled here — they are chat
    ///     concerns and are claimed by <see cref="IAppReducerPlugin.Reduce" />.
    /// </summary>
    private static ReduceResult UpdateKey(UiState state, AppMsg.KeyInput k, bool busy)
    {
        if (busy && k.Action is not (ChatAction.ScrollUpLine or ChatAction.ScrollDownLine
            or ChatAction.ScrollUpPage or ChatAction.ScrollDownPage
            or ChatAction.ScrollTop or ChatAction.ScrollBottom
            or ChatAction.ToggleFocus))
            return ReduceResult.NoOp(state);

        return UpdateKeyGeneric(state, k);
    }

    private static ReduceResult UpdateKeyGeneric(UiState state, AppMsg.KeyInput k)
    {
        var ui = state.Ui;
        switch (k.Action)
        {
            case ChatAction.ToggleFocus:
                return ReduceResult.NoOp(state.SetFocus(ui.Focus == FocusMode.Input ? FocusMode.Chat : FocusMode.Input));

            // Scrolling works in both focus modes; the wheel arrives as PageUp/PageDown.
            case ChatAction.ScrollUpLine:
                return ReduceResult.NoOp(state.SetScroll(ui.ScrollOffset + 1));
            case ChatAction.ScrollDownLine:
                return ReduceResult.NoOp(state.SetScroll(ui.ScrollOffset - 1));
            case ChatAction.ScrollUpPage:
                return ReduceResult.NoOp(state.SetScroll(ui.ScrollOffset + Math.Max(1, ui.ViewportLines - 2)));
            case ChatAction.ScrollDownPage:
                return ReduceResult.NoOp(state.SetScroll(ui.ScrollOffset - Math.Max(1, ui.ViewportLines - 2)));
            case ChatAction.ScrollTop:
                return ReduceResult.NoOp(state.SetScroll(int.MaxValue));
            case ChatAction.ScrollBottom:
                return ReduceResult.NoOp(state.SetScroll(0));

            // Input editing only when the input box owns focus.
            case ChatAction.Backspace:
                return Edit(state, ui, new InputMsg.Backspace());
            case ChatAction.InputHistoryPrev:
                return Edit(state, ui, new InputMsg.HistoryUp());
            case ChatAction.InputHistoryNext:
                return Edit(state, ui, new InputMsg.HistoryDown());
            case ChatAction.Autocomplete:
                return ui.Focus == FocusMode.Input && ui.Input.Text.StartsWith('/')
                    ? Edit(state, ui, new InputMsg.Autocomplete(TuiEffectHost.KnownSlashCommands))
                    : ReduceResult.NoOp(state);
            case ChatAction.Char:
                return ui.Focus == FocusMode.Input && k.Pressed.Character is { } c
                    ? Edit(state, ui, new InputMsg.Char(c))
                    : ReduceResult.NoOp(state);
            case ChatAction.InsertNewline:
                // Enter-family decision lives here (#359): Shift/Alt+Enter
                // appends '\n' (same as typing it). Ctrl combos resolve here
                // only via subset matching — the composer ignores Ctrl+Enter,
                // so the store drops it too instead of inserting a newline.
                return ui.Focus == FocusMode.Input && !k.Pressed.Has(KeyModifierSet.Ctrl)
                    ? Edit(state, ui, new InputMsg.Char('\n'))
                    : ReduceResult.NoOp(state);

            // Panel actions (epic C step 2): resolved actions land on the
            // existing panel transitions — previously fell into default noop.
            case ChatAction.TogglePanelSlot:
                return ReduceResult.NoOp(TogglePanelSlot(state, k));
            case ChatAction.CyclePanelFocus:
                return ReduceResult.NoOp(CycleFocus(state));
            case ChatAction.ClosePanel:
                return ReduceResult.NoOp(FocusPanel(state, null));
            case ChatAction.ResizePanelGrow:
                return ReduceResult.NoOp(ResizeFocusedPanel(state, +1));
            case ChatAction.ResizePanelShrink:
                return ReduceResult.NoOp(ResizeFocusedPanel(state, -1));
            case ChatAction.HelpPanel:
                return ReduceResult.NoOp(TogglePanel(state, "help"));
            case ChatAction.ToggleLogsPanel:
                return ReduceResult.NoOp(TogglePanel(state, "logs"));
            case ChatAction.JumpPalette:
                // Hosts register a "jump" panel (or overlay) rendering the
                // worktree jump palette; noop until one exists (TogglePanel
                // ignores unknown ids) so the key is safe on every renderer.
                return ReduceResult.NoOp(TogglePanel(state, "jump"));

            case ChatAction.None:
            default:
                return ReduceResult.NoOp(state);
        }
    }

    /// <summary>Apply an input-model command, but only while the input box owns focus.</summary>
    private static ReduceResult Edit(UiState state, TerminalUiState ui, InputMsg command) =>
        ui.Focus == FocusMode.Input
            ? ReduceResult.NoOp(state.SetInput(InputMsg.Update(ui.Input, command)))
            : ReduceResult.NoOp(state);

    /// <summary>Alt+1..9: slot index from the pressed character.</summary>
    private static UiState TogglePanelSlot(UiState state, AppMsg.KeyInput k)
    {
        if (k.Pressed.Character is not { } c || c is < '1' or > '9')
            return state;
        int idx = c - '1';
        if (idx < 0 || idx >= state.Ui.RegisteredPanelIds.Length)
            return state;
        return TogglePanel(state, state.Ui.RegisteredPanelIds[idx]);
    }

    /// <summary>Grow/shrink the focused panel; noop when chat owns focus.</summary>
    private static UiState ResizeFocusedPanel(UiState state, int delta) =>
        state.Ui.FocusedPanelId is { } id ? ResizePanel(state, id, delta) : state;
}
