using System.Linq;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Attachments;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.State;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging;
using EngineInput = Harbor.Tui.CellForge.Input;
// #436: same shared-name pins as Harbor.Tui.CellForge/GlobalUsings.cs — the
// Rendering vocabulary keeps its historical meaning in this file; engine-typed
// sites qualify through EngineCells.
using Cell = Harbor.Ui.Framework.Rendering.Cell;
using Rect = Harbor.Ui.Framework.Rendering.Rect;
using ScreenBuffer = Harbor.Ui.Framework.Rendering.ScreenBuffer;
using CellStyle = Harbor.Ui.Framework.Rendering.CellStyle;
using PackedColor = Harbor.Ui.Framework.Rendering.PackedColor;
using StyleAttr = Harbor.Ui.Framework.Rendering.StyleAttr;
using UnicodeWidth = Harbor.Ui.Framework.Rendering.UnicodeWidth;
using KeyEvent = Harbor.Ui.Framework.Rendering.Input.KeyEvent;
using KeyCode = Harbor.Ui.Framework.Rendering.Input.KeyCode;
using KeyModifiers = Harbor.Ui.Framework.Rendering.Input.KeyModifiers;
using KeyEventType = Harbor.Ui.Framework.Rendering.Input.KeyEventType;
using IFocusTarget = Harbor.Ui.Framework.Rendering.Input.IFocusTarget;
using UiKeyDto = Harbor.Ui.Framework.Rendering.Input.UiKeyDto;
using UiKeyKind = Harbor.Ui.Framework.Rendering.Input.UiKeyKind;
using UiKeyMods = Harbor.Ui.Framework.Rendering.Input.UiKeyMods;
using EngineCells = Harbor.Tui.CellForge.Rendering;
using EngineInput = Harbor.Tui.CellForge.Input;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Input routing behind the CellForge REPL (G2 split of
///     <see cref="CellForgeReplRunner"/>, issue #174): mouse/key/paste/resize
///     dispatch, composer key resolution, the idle double-Ctrl+C quit gesture
///     and copy-on-select release. Stateless service — all mutable state stays
///     on the runner and is reached through its internal accessors, so the
///     split moves code without moving behavior.
/// </summary>
internal sealed class ReplInputLoop(CellForgeReplRunner host)
{
    /// <summary>
    ///     Title of the <c>/jump</c> palette frame (#857). A literal rather
    ///     than <c>new JumpCommand().Title</c>: this loop is a singleton per
    ///     runner, so building a command to read a display string would allocate
    ///     per keystroke for no gain, and the catalog still owns the value.
    /// </summary>
    private const string JumpCommandTitle = "Jump";

    /// <summary>Idle-Ctrl+C window for the «press again to quit» gesture.</summary>
    private const long QuitGestureWindowMs = 2000;

    /// <summary>Wheel tick ≈ three rows (xterm convention).</summary>
    private const int WheelScrollLines = 3;

    /// <summary>
    ///     Send-to-agent latch (issue #402): Press-only plus the busy refuse
    ///     already dedupes most double keypresses, but the validation await
    ///     below still yields — without this flag two Ctrl+Enter presses in
    ///     the same gap would each submit a turn. Two DELIBERATE sends still
    ///     produce two blocks, as specified.
    /// </summary>
    private bool _markupSendInFlight;

    /// <summary>
    ///     #3617: the modal barriers as named key contexts. One instance per
    ///     loop; bindings are set once in the constructor and the contexts are
    ///     read-only from here on (rebind is a test/diagnostic concern).
    /// </summary>
    private readonly ReplModalKeyContexts _keyContexts = new();

    /// <summary>
    ///     #3111: durable, session-scoped prompt store. The composer owns the
    ///     readline walk (draft save/restore), but its rail is in-memory and
    ///     survives a session switch unchanged. This store is the session
    ///     boundary: submits are recorded against the active <see cref="SessionId"/>
    ///     and the rail is refilled from it whenever that id changes, so Up/Down
    ///     can only ever recall prompts from the conversation being viewed.
    /// </summary>
    private readonly SessionPromptHistory _promptHistory = new();

    /// <summary>Last session id the composer rail was populated for.</summary>
    private string? _promptHistorySessionId;

    // ── Input routing ──────────────────────────────────────────────────────

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="col" /> /
    ///     <paramref name="row" /> falls inside the sidebar's model section.
    ///     Used by the mouse handler to open the model picker on click.
    /// </summary>
    private bool TryHandleSidebarModelClick(int col, int row)
    {
        if (host.Screen.Sidebar is null || host.ScreenSession.CurrentCols < SideBarLayout.AutoShowMinWidth)
        {
            return false;
        }

        int sidebarX = host.ScreenSession.CurrentCols - SideBarLayout.DefaultWidth;
        if (col < sidebarX || col >= host.ScreenSession.CurrentCols)
        {
            return false;
        }

        // MODEL section is the second section in the sidebar paint order,
        // typically at visual rows 3-5 inside the sidebar rect (0-indexed).
        // This is an approximate hit-box — good enough for a click target.
        return row is >= 3 and <= 6;
    }

    internal async Task HandleInputAsync(InputEvent evt, CancellationToken ct)
    {
        // Any user input can mutate composer, palette, scroll or selection —
        // the next frame takes the conservative full-scan path.
        host._replStore.DamageAll();
        // #3111: a session switch is observed on the next input event. Refill
        // the composer rail first, so the Up/Down in this very event walks the
        // prompts of the session it now belongs to (never the previous one).
        SyncPromptHistoryForSession();
        switch (evt.Kind)
        {
            case InputEventKind.Key:
                await HandleKeyAsync(ToRenderingKey(evt.Key), ct).ConfigureAwait(false);
                break;

            case InputEventKind.Capability when evt.Capability.Kind == CapabilityEventKind.Osc11BackgroundReport:
                host.Lifecycle.ApplyAutoTheme(evt.Capability);
                break;

            case InputEventKind.Capability when evt.Capability.Kind == CapabilityEventKind.Osc99NotifyReport:
                host.Pipeline.NotifyHint = DesktopNotifyKind.Osc99;
                break;

            case InputEventKind.Focus:
                // #2218: CSI I / CSI O. The next ArmSpinner in the frame loop
                // reads this and parks the heartbeat while the window is blurred.
                // A focus report wakes the loop, so re-arming needs no extra
                // signal. This adds no plugin or extension axis.
                host._windowFocused = evt.Focus.IsFocused;
                break;

            case InputEventKind.Paste:
            {
                // Paste payload is verbatim by parser contract — sanitize it
                // BEFORE it reaches the composer and, through submit, the agent
                // (osc-sprint): escape sequences and control bytes stripped, a
                // sanitized preview lands in the timeline. No new permissions —
                // after sanitization the paste is trusted input.
                PasteSanitizeResult sanitized = PasteSanitizer.Sanitize(evt.Paste.Text);
                if (sanitized.Modified)
                {
                    host.Bridge.AppendSystemLine(
                        $"⎘ paste: снято {sanitized.EscapeSequences} escape-последоват., {sanitized.ControlChars} control-символов");
                }
                else if (evt.Paste.Text.Contains('\n'))
                {
                    int lines = evt.Paste.Text.Count('\n') + 1;
                    host.Bridge.AppendSystemLine($"⎘ paste: {lines} строк — вставлено как текст, Enter не исполняется");
                }

                // The sanitized buffer IS the preview — the composer shows the
                // cleaned text; submit routes exactly what the user sees.
                // Store mirror (epic C): the TEA input box tracks the same
                // draft the composer paints (cf. CellForgeTuiRenderer sync).
                _ = host._composer.Buffer.InsertText(sanitized.Text);
                _ = host._replStore.Dispatch(new AppMsg.InputText(host._composer.Buffer.SnapshotText()));
                break;
            }

            case InputEventKind.Resize:
                // Policy lives in ScreenSession: shrink ⇒ erase-in-display next frame.
                // Geometry (Viewport/HistoryMeasured/ScrollClamp) flows into the
                // TEA store on the next RenderFrameAsync (changed-only) — the
                // layout the clamp depends on settles only inside PrepareFrame.
                int resizeW = Math.Max(1, evt.Resize.Width);
                host.ScreenSession.Resize(resizeW, Math.Max(1, evt.Resize.Height));
                host.Lifecycle.ApplySidebarResizePolicy(resizeW);
                break;

            case InputEventKind.Mouse when evt.Mouse.Type is MouseEventType.Press or MouseEventType.Click:
                // Markup barrier (issue #400): clicks place/select through the
                // store; nothing reaches gates, cards, selection or the sidebar.
                if (host.Screen.Markup.Visible)
                {
                    if (host.Screen.Markup.HandleMouse(evt.Mouse, FullViewport()) is { } pressMsg)
                    {
                        _ = host._replStore.Dispatch(pressMsg);
                        host._wake.Writer.TryWrite(null);
                    }

                    break;
                }

                // Click-to-decide: pending approval gates get first claim on a
                // left press; otherwise a press anchors a copy-on-select
                // selection (P6.4) — a plain click selects nothing on release.
                if (host.Bridge.TryRouteApprovalClick(evt.Mouse))
                {
                    host._selection.Clear();
                }
                else if (host.Bridge.TryRouteToolCardClick(evt.Mouse))
                {
                    host._selection.Clear();
                    host._wake.Writer.TryWrite(null);
                }
                else if (host._selection.OnPress(evt.Mouse.Column, evt.Mouse.Row, evt.Mouse.Button))
                {
                    host._wake.Writer.TryWrite(null);
                }
                else if (evt.Mouse.Button == MouseButton.Left
                         && TryHandleSidebarModelClick(evt.Mouse.Column, evt.Mouse.Row))
                {
                    host._selection.Clear();
                    await new Repl.Commands.ModelCommand().ExecuteAsync(new ReplCommandContext(host, "model"), ct).ConfigureAwait(false);
                }
                else
                {
                    host._selection.Clear();
                }

                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.Drag
                                           && evt.Mouse.Button == MouseButton.Left:
                // Markup drag (issue #400): stretches the draft or moves the
                // pressed annotation; the copy-on-select path starves.
                if (host.Screen.Markup.Visible)
                {
                    if (host.Screen.Markup.HandleMouse(evt.Mouse, FullViewport()) is { } dragMsg)
                    {
                        _ = host._replStore.Dispatch(dragMsg);
                        host._wake.Writer.TryWrite(null);
                    }

                    break;
                }

                host._selection.OnDrag(evt.Mouse.Column, evt.Mouse.Row);
                host._wake.Writer.TryWrite(null); // repaint the growing highlight
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.Release
                                           && evt.Mouse.Button == MouseButton.Left:
                // Markup release (issue #400): commits the draft as a new
                // primitive. No selection, no clipboard.
                if (host.Screen.Markup.Visible)
                {
                    if (host.Screen.Markup.HandleMouse(evt.Mouse, FullViewport()) is { } releaseMsg)
                    {
                        _ = host._replStore.Dispatch(releaseMsg);
                        host._wake.Writer.TryWrite(null);
                    }

                    break;
                }

                await FinishSelectionAsync(evt.Mouse.Column, evt.Mouse.Row, ct).ConfigureAwait(false);
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.WheelUp:
                // Swallowed while the markup overlay is up: the feed beneath
                // must not scroll out from under the wireframe.
                if (host.Screen.Markup.Visible)
                {
                    break;
                }

                // Store-first scroll (epic C): one line-msg per row so the TEA
                // store tracks the same offset the timeline paints locally.
                for (int i = 0; i < WheelScrollLines; i++)
                {
                    _ = host._replStore.Dispatch(VirtualizedChatTimeline.LineUpMsg());
                }

                host._timeline.ScrollBy(-WheelScrollLines);
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.WheelDown:
                if (host.Screen.Markup.Visible)
                {
                    break;
                }

                for (int i = 0; i < WheelScrollLines; i++)
                {
                    _ = host._replStore.Dispatch(VirtualizedChatTimeline.LineDownMsg());
                }

                host._timeline.ScrollBy(WheelScrollLines);
                break;
        }
    }

    /// <summary>
    /// Engine-to-renderer key adapter (#436: the byte parser lives in the
    /// standalone leaf and produces engine keys; every consumer behind this
    /// loop — palette, vim, composer, mapper — speaks the Rendering
    /// vocabulary). Exact field-for-field copy; both structs are the same
    /// shape by construction (verbatim port), so no meaning is lost.
    /// Per-keystroke cost, never per-frame.
    /// </summary>
    private static KeyEvent ToRenderingKey(in Harbor.Tui.CellForge.Input.KeyEvent key) => new(
        (KeyCode)(int)key.Key,
        key.Character,
        (KeyModifiers)(int)key.Modifiers,
        (KeyEventType)(int)key.EventType,
        key.IsKittyEncoded,
        key.Codepoint);

    /// <summary>
    /// Renderer-to-engine key adapter for #1620's adapted floating-terminal
    /// slice: that session now lives in the standalone engine and speaks the
    /// engine key vocabulary. Exact field-for-field copy, inverse of
    /// <see cref="ToRenderingKey"/>. Per-keystroke cost, never per-frame.
    /// </summary>
    private static Harbor.Tui.CellForge.Input.KeyEvent ToEngineKey(in KeyEvent key) => new(
        (Harbor.Tui.CellForge.Input.KeyCode)(int)key.Key,
        key.Character,
        (Harbor.Tui.CellForge.Input.KeyModifiers)(int)key.Modifiers,
        (Harbor.Tui.CellForge.Input.KeyEventType)(int)key.EventType,
        key.IsKittyEncoded,
        key.Codepoint);

    private async Task HandleKeyAsync(KeyEvent key, CancellationToken ct)
    {
        // Key routing can mutate gates, palette, vim state or the composer —
        // the next frame takes the conservative full-scan path.
        host._replStore.DamageAll();

        // #3617: modal barriers are declarative key contexts. The active
        // context is selected in the SAME order as the barrier chain below
        // (terminal → sheet → markup → image → onboarding → palette → setup),
        // so the priority is unchanged. A gesture bound by the host dispatches
        // its OPAQUE action id here; a miss falls through to the modal's own
        // handler, which still swallows the key. KeyContext owns no callbacks
        // and no action vocabulary — ChatKeyMap keeps the composer names and
        // LeaderKeyRouter keeps the chords.
        EngineInput.KeyContext? modalCtx = _keyContexts.Active(new ReplModalState(
            host.FloatingTerminal.IsAttached,
            host.Sheet.IsOpen,
            host.Screen.Markup.Visible,
            host.Images.Visible,
            host.Onboarding.Active,
            host._palette.Visible,
            host.Screen.SetupChecklist.Visible));
        if (modalCtx is not null && modalCtx.TryMatch(ToEngineKey(key), out var modalHit))
        {
            switch (modalHit.ActionId)
            {
                // Press-only send, exactly as IsMarkupSend was: a held chord
                // must not submit a second billable turn. Repeat is swallowed
                // by the barrier's return below.
                case ReplModalKeyContexts.MarkupSend when key.EventType == KeyEventType.Press:
                    await SendMarkupAsync(ct).ConfigureAwait(false);
                    break;

                // Save tolerates legacy Repeat, matching IsMarkupSave.
                case ReplModalKeyContexts.MarkupSave
                    when key.EventType is KeyEventType.Press or KeyEventType.Repeat:
                    await SaveMarkupAsync(ct).ConfigureAwait(false);
                    break;

                // #400 store-continuation unchanged: hide the viewer, open the
                // store session snapshotting the scroll, sync it immediately so
                // no key slips through before the next frame.
                case ReplModalKeyContexts.ImageAnnotate
                    when (key.EventType is KeyEventType.Press or KeyEventType.Repeat) && host.Images.Source.HasValue:
                {
                    var block = host.Images.Source.Value;
                    host.Images.Hide();
                    _ = host._replStore.Dispatch(new ChatAppMsg.OpenMarkup(
                        block.FullPath, block.Name, block.PixelWidth, block.PixelHeight,
                        host._replStore.State.Ui.ScrollOffset));
                    host.Screen.SyncMarkup(host._replStore.State.Chat.Markup);
                    host._replStore.DamageAll();
                    host._wake.Writer.TryWrite(null);
                    break;
                }
            }

            // Every matched gesture is swallowed: panels (and the agent) beneath
            // the modal starve, exactly as before. Save/send already wake from
            // their own effect methods; annotate wakes above.
            return;
        }

        // Floating terminal pane (issue #1491): while attached it owns input —
        // a full barrier like the image/markup modals. The detach chord is
        // offered through the dedicated LeaderKeyRouter first; everything else
        // (including other leader chords) is fed to the live session or
        // swallowed, so no key can leak into the composer beneath. Detaching
        // simply ends this branch: the next key flows to the composer again,
        // which is the focus-return contract. The session object is untouched
        // by detach, so re-attaching returns the same live history/process.
        if (host.FloatingTerminal.IsAttached)
        {
            if (host._terminalLeader.HandleKey(key, Environment.TickCount64))
            {
                host._wake.Writer.TryWrite(null);
                return;
            }

            host.FloatingTerminal.FeedKey(ToEngineKey(key));
            host._wake.Writer.TryWrite(null);
            return;
        }

        // Sheet (issue #2376): an open sheet owns the keystroke like the other
        // modal overlays. SheetOverlay.HandleKey consumes Escape (close) and
        // Tab / Shift+Tab (the FocusRouter trap); every other key is swallowed
        // here, so nothing beneath the modal barrier receives it. The host
        // calls the sheet directly — no new overlay key route is introduced.
        if (host.Sheet.IsOpen)
        {
            _ = host.Sheet.HandleKey(key);
            host._wake.Writer.TryWrite(null);
            return;
        }

        // Image zoom viewer (issue #387) is MODAL, so it forms the outermost
        // input barrier: it claims keys BEFORE the palette, the setup guide,
        // the approval gate, the tool cards and the composer — a zoomed
        // screenshot must never be typed into, and no key may reach the agent
        // while it is up. Esc/q/Enter close; +/-/arrows zoom. An unconsumed
        // key is still swallowed (the barrier contract: panels beneath starve).
        //
        // The markup overlay (issue #400) sits one step further out: it opens
        // FROM the viewer (which hides underneath) and carries the same
        // barrier contract — consumed gestures dispatch store messages
        // (the session lives in UiState, transitions in the reducer),
        // everything else is swallowed. Its Ctrl+S / Ctrl+Enter host effects
        // are now markup-context bindings dispatched above (#3617), so the
        // reducer stays pure and file I/O still lives here.
        if (host.Screen.Markup.Visible)
        {
            if (host.Screen.Markup.HandleKey(key) is { } markupMsg)
            {
                // Close restores the scroll the open snapshotted: the overlay
                // owns no scroll of its own, so the feed is already where it
                // was — this only corrects drift from rows the agent streamed
                // while the wireframe was up.
                int savedScroll = host._replStore.State.Chat.Markup.SavedScrollOffset;
                bool closing = markupMsg is ChatAppMsg.CloseMarkup;
                _ = host._replStore.Dispatch(markupMsg);
                if (closing)
                {
                    host._timeline.ScrollBy(savedScroll - (int)host._timeline.ScrollY);
                    host.Screen.SyncMarkup(host._replStore.State.Chat.Markup);
                }

                host._wake.Writer.TryWrite(null);
            }

            return;
        }

        if (host.Images.Visible)
        {
            // The `m` annotate gesture is an image-context binding dispatched
            // above (#400 store-continuation unchanged). Every other key still
            // goes to the viewer, which swallows what it does not consume.
            if (host.Images.HandleKey(key))
            {
                host._wake.Writer.TryWrite(null);
            }

            return;
        }

        // Onboarding flow (issue #1248, slice 1): modal dialog — while it is
        // up every key belongs to it, so typing never reaches the composer.
        if (await host.Onboarding.HandleKeyAsync(key, ct).ConfigureAwait(false))
        {
            host._wake.Writer.TryWrite(null);
            return;
        }

        if (host._palette.Visible && host._palette.HandleKey(key))
        {
            // Frame-carried continuations: no host-side stacks. Esc/Hide drops
            // a frame together with its continuation — nothing leaks.
            if (host._palette.TakePendingCommit() is { } commit)
            {
                await commit.Handler(commit.Item, ct).ConfigureAwait(false);
            }
            else if (host._palette.TakePendingInput() is { } input)
            {
                await input.Handler(input.Value, ct).ConfigureAwait(false);
            }

            host._wake.Writer.TryWrite(null);
            return;
        }

        // Setup checklist (issue #383): the guide is read-only, so it only claims the
        // dismiss keys (Esc/q/? — consumed); anything else keeps flowing to the
        // composer and the guide stays up until the user closes it.
        if (host.Setup.HandleKey(key))
        {
            host._wake.Writer.TryWrite(null);
            return;
        }

        // Image zoom viewer OPEN gesture (issue #387): a plain Enter on an empty
        // composer opens the viewer on the newest image row. Routed before the
        // tool-card expand gesture so an image that is the newer of the two
        // wins — a screenshot is the thing you asked to look at. The viewer
        // owns no scroll/selection/UiState, so closing it hands the feed back
        // exactly as it was: nothing to restore, nothing to leak.
        if (host._composer.Buffer.AsSpan().IsEmpty
            && host.Bridge.TryOpenImageViewer(key) is { } imageBlock)
        {
            host.Images.Show(imageBlock, host.ScreenSession.Images);
            host._replStore.DamageAll();
            host._wake.Writer.TryWrite(null);
            return;
        }

        // Leader chords (ctrl+x …): armed router consumes the leader press and
        // the chord; resolved sync actions run here, slash chords and quick-
        // switch digits hand off to the frame loop for async execution.
        // Msg-bound chords (scroll anchors) additionally dual-write their AppMsg
        // into the TEA store — same dual-write as agent events in LoopAsync.
        if (host._leader.HandleKey(key, Environment.TickCount64))
        {
            if (host._leader.TakePendingMsg() is { } chordMsg)
            {
                _ = host._replStore.Dispatch(chordMsg);
            }

            if (host._leaderSlash is { } leaderSlash)
            {
                host._leaderSlash = null;
                await host.Commands.ExecutePaletteItemAsync(new CommandItem(leaderSlash, leaderSlash), ct).ConfigureAwait(false);
            }

            if (host._quickSwitchChord is { } chord)
            {
                host._quickSwitchChord = null;
                await host.Sessions.SwitchToSlotAsync(chord, ct).ConfigureAwait(false);
            }

            host._wake.Writer.TryWrite(null);
            return;
        }

        // Permission gate outranks the composer while one is pending: y/n/a/
        // Enter/Esc resolve the card and never leak into prompt editing.
        if (host.Bridge.TryRouteApprovalKey(key))
        {
            host._wake.Writer.TryWrite(null);
            return;
        }

        // Tool cards: plain Enter on an empty composer toggles the newest
        // feed card instead of submitting empty input. Non-empty composer
        // keeps the submit path below.
        if (host._composer.Buffer.AsSpan().IsEmpty && host.Bridge.TryRouteToolCardKey(key))
        {
            host._wake.Writer.TryWrite(null);
            return;
        }

        // Epic C dual-write: every key that reaches the composer is resolved
        // through the central ChatKeyMap (char-aware Binding + None-exact
        // Matches live there — this shell adds no key→action branches) and
        // dual-written to the TEA store as KeyInput. Intercepts above
        // (palette/leader/gates) consumed their keys first so they never
        // pollute store input. Most returned effects are sunk on purpose:
        // submit executes through Pipeline.SubmitAsync on the composer buffer
        // below. Abort / quit are the exception — they run as host effects just
        // below, where the resolved ChatAction is dispatched ONCE and the
        // reducer's busy/idle decision is honoured. Falling through to the
        // dispatch here as well would fire those effects twice.
        if (KeyEventMapper.TryMap(key, out var dto))
        {
            var uiKey = KeyEventAdapter.ToUiKey(dto);
            // Resolved against the live state: Ctrl+Tab is shared with panel
            // cycling and only means "next tab" while a multi-tab strip is open
            // (#389). A host that passes no snapshot gets the pre-#389 mapping.
            var resolved = host._keyMap.Resolve(uiKey, host._replStore.State);
            if (resolved != ChatAction.None)
            {
                // Host-effect chords (#3615): the keymap is the single source of
                // "which key does what", but these actions are impure — open a
                // palette, hand the terminal to $EDITOR — so the HOST runs them
                // and the reducer stays a pure fold (no KeyActionRegistry entry).
                switch (resolved)
                {
                    case ChatAction.TogglePalette:
                        if (host._palette.Visible)
                            host._palette.Hide();
                        else
                            host.Commands.OpenCommandPalette();
                        host._wake.Writer.TryWrite(null);
                        return;

                    case ChatAction.JumpPalette:
                        // #857 jump-frame behavior unchanged: only the jump frame
                        // toggles closed; any other visible frame (ctrl+p's list,
                        // /tree) survives a Ctrl+J. The bare-LF alias stays on the
                        // keymap for hosts that do not use this loop.
                        if (host._palette.Visible && host._palette.CurrentBreadcrumb == JumpCommand.Breadcrumb)
                            host._palette.Hide();
                        else
                            await host.Commands.ExecutePaletteItemAsync(
                                new CommandItem(JumpCommand.CommandId, JumpCommandTitle), ct).ConfigureAwait(false);
                        host._wake.Writer.TryWrite(null);
                        return;

                    case ChatAction.OpenExternalEditor when key.EventType == KeyEventType.Press:
                        // Press-only so a held chord cannot spawn several children.
                        await host.OpenExternalEditorAsync(ct).ConfigureAwait(false);
                        host._wake.Writer.TryWrite(null);
                        return;

                    case ChatAction.OpenSlashPalette:
                        // The chord opens the palette only while the composer is
                        // empty. With text already typed '/' stays an ordinary
                        // character: fall through to the composer below instead.
                        if (host._composer.Buffer.IsEmpty)
                        {
                            host.Commands.OpenSlashPalette();
                            host._wake.Writer.TryWrite(null);
                            return;
                        }
                        break;

                    case ChatAction.Abort when host._composer.Buffer.IsEmpty:
                    case ChatAction.Quit when !host._composer.Dictation.IsActive:
                        // Ctrl+C on a non-empty composer is readline clear
                        // (ComposerController owns that branch), and Escape
                        // while dictating belongs to the cancel path below.
                        // Otherwise both gestures mean "abort the running turn,
                        // else quit" — the reducer's Quit fold. The host effect
                        // runs in HandleAbortGesture, which returns before the
                        // dispatch at the bottom of this block.
                        HandleAbortGesture(resolved, uiKey);
                        host._wake.Writer.TryWrite(null);
                        return;
                }

                // Ctrl+C with a non-empty composer is readline clear: the composer
                // owns that edit below and dispatching Abort would fold the
                // transcript into a spurious abort state. Every other resolved
                // action still dual-writes to the store as before.
                if (resolved != ChatAction.Abort || host._composer.Buffer.IsEmpty)
                {
                    var effect = host._replStore.Dispatch(new AppMsg.KeyInput(resolved, uiKey));
                    // The one exception to the sink-the-effect rule above: the strip
                    // actions are effect-driven, because the reducer resolves the
                    // target session and the host performs the switch. Dropping the
                    // effect here would make Ctrl+Tab and Ctrl+W look like no-ops.
                    if (effect is TuiEffect.ActivateSession or TuiEffect.RequestOpenSession)
                        host.Effects.Run(effect);
                }
            }
        }

        // Esc cancels a live dictation session and restores the exact draft that
        // was in the composer before Ctrl+D. After the modal barriers, before the
        // composer: the cancel key must not edit text or open a panel.
        if (host._composer.Dictation.IsActive
            && key.Key == KeyCode.Escape
            && key.EventType == KeyEventType.Press)
        {
            await host._composer.CancelDictationAsync().ConfigureAwait(false);
            host._wake.Writer.TryWrite(null);
            return;
        }

        var action = host._vim.HandleKey(key, host._composer);
        switch (action)
        {
            case ComposerAction.DictationToggle:
            {
                bool started = await host._composer
                    .ToggleDictationAsync(host.Agent.IsRunning(), ct).ConfigureAwait(false);
                if (host._composer.DictationRejection is { } rejection)
                {
                    host.Bridge.AppendSystemLine($"⚠ {rejection}");
                }
                else if (started && !host._composer.SpeechToText.IsAvailable)
                {
                    host.Bridge.AppendSystemLine(
                        "◉ dictation started — offline backend captures no audio; Ctrl+D again to stop, Esc to cancel.");
                }

                host._wake.Writer.TryWrite(null);
                break;
            }

            case ComposerAction.Submitted:
                // #3111: record BEFORE SubmitAsync — it calls TakeText(), which
                // clears the buffer. The text still in the buffer here is the
                // exact line the user submitted, keyed to the live session.
                RecordSubmittedPrompt(_promptHistory, host.SessionModel.Id, host._composer.Buffer.SnapshotText());
                await host.Pipeline.SubmitAsync(ct).ConfigureAwait(false);
                break;

            case ComposerAction.Edited:
                // #2354: a plain composer edit touches only the composer row.
                // Narrow the conservative input-time broad mark to that one
                // subgraph so the frame diffs a single row, not the screen.
                host._replStore.NarrowDamageTo(ChatScreen.ComposerId);
                break;

            case ComposerAction.Ignored when key.Key == KeyCode.PageUp:
                host._timeline.PageUp(Math.Max(1, host._timelineViewportH));
                break;

            case ComposerAction.Ignored when key.Key == KeyCode.PageDown:
                host._timeline.PageDown(Math.Max(1, host._timelineViewportH));
                break;
        }
    }

    /// <summary>Fullscreen viewport the markup overlay maps mouse cells through.</summary>
    private Rect FullViewport() => new(0, 0, host.ScreenSession.CurrentCols, host.ScreenSession.CurrentRows);

    /// <summary>
    ///     Bakes the store session onto the source pixels and writes the
    ///     annotated copy into the workspace (issue #400 slice 1/2): read
    ///     source → <see cref="MarkupPngBaker.TryBake" /> → write sibling file
    ///     → timeline <c>ImageBlock</c> with real dimensions + store note.
    ///     Every failure lands in <c>MarkupFailed</c> as inline text — this
    ///     method never throws out of the input loop.
    /// </summary>
    private async Task SaveMarkupAsync(CancellationToken ct)
    {
        var markup = host._replStore.State.Chat.Markup;
        if (!markup.IsOpen)
        {
            return;
        }

        if (markup.Model.Items.Length == 0)
        {
            Fail("Nothing to save yet — place an arrow, box or text first.");
            return;
        }

        byte[] source;
        try
        {
            source = await File.ReadAllBytesAsync(markup.SourcePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail($"Cannot read {markup.SourceName}: {ex.Message}");
            return;
        }

        if (!MarkupPngBaker.TryBake(source, markup.Model, out byte[]? baked, out string bakeError) || baked is null)
        {
            Fail(bakeError);
            return;
        }

        string target = UniqueAnnotatedPath(markup.SourcePath, markup.SourceName);
        try
        {
            await File.WriteAllBytesAsync(target, baked, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail($"Cannot write {target}: {ex.Message}");
            return;
        }

        host.Bridge.AppendImageCard(target, "image/png", baked.Length, baked);
        _ = host._replStore.Dispatch(new ChatAppMsg.MarkupSaved(target));
        _ = host._replStore.Dispatch(new ChatAppMsg.AppendLine(ChatRole.System, $"Saved annotated image: {target}"));
        host._wake.Writer.TryWrite(null);
        return;

        void Fail(string error)
        {
            _ = host._replStore.Dispatch(new ChatAppMsg.MarkupFailed(error));
            host._wake.Writer.TryWrite(null);
        }
    }

    /// <summary>
    ///     Sends the SAVED annotated copy back to the agent as a real image
    ///     turn (issue #402 slice 2/2): validate the file through the same
    ///     <c>ImageAttachmentReader</c> every <c>/attach</c> goes through
    ///     (magic bytes, size cap, vision capability) and submit a user turn
    ///     carrying the baked PNG plus the <see cref="MarkupSendPreamble" />
    ///     text naming source, count and path. Every failure lands in
    ///     <c>MarkupFailed</c> as inline overlay text — a non-vision model is
    ///     a one-line note, never a raw provider 400. Baking stays silent;
    ///     only this explicit chord spends tokens. This method never throws
    ///     out of the input loop.
    /// </summary>
    private async Task SendMarkupAsync(CancellationToken ct)
    {
        var markup = host._replStore.State.Chat.Markup;
        if (!markup.IsOpen || _markupSendInFlight)
        {
            return;
        }

        if (markup.Model.Items.Length == 0)
        {
            Fail("Nothing to send yet — place an arrow, box or text first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(markup.SavedPath))
        {
            Fail("Save first (Ctrl+S), then send (Ctrl+Enter).");
            return;
        }

        if (host.Pipeline.IsBusy)
        {
            host.Bridge.AppendSystemLine("⚠ Agent is busy — wait for completion or press Esc / Ctrl+C to abort.");
            host._wake.Writer.TryWrite(null);
            return;
        }

        _markupSendInFlight = true;
        try
        {
            var reader = new ImageAttachmentReader(host.ProviderRegistry, host.Log);
            var read = await reader.ReadAsync(
                markup.SavedPath, host.SessionModel.ProviderId, host.SessionModel.Model, ct).ConfigureAwait(false);
            if (read.IsFailure)
            {
                Fail(read.Error);
                return;
            }

            string preamble = MarkupSendPreamble.Build(markup.SourceName, markup.Model.Items.Length, read.Value.Path);
            await host.Pipeline.SubmitImageTurnAsync(preamble, [read.Value], ct).ConfigureAwait(false);
        }
        finally
        {
            _markupSendInFlight = false;
        }

        return;

        void Fail(string error)
        {
            _ = host._replStore.Dispatch(new ChatAppMsg.MarkupFailed(error));
            host._wake.Writer.TryWrite(null);
        }
    }

    /// <summary>
    ///     Sibling path for the annotated copy: <c>&lt;stem&gt;.annotated.png</c>
    ///     next to the source (the workspace), numeric suffix while taken.
    ///     Falls back to the process working directory when the source dir is
    ///     missing or unusable.
    /// </summary>
    private static string UniqueAnnotatedPath(string sourcePath, string sourceName)
    {
        string dir = string.Empty;
        try
        {
            dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            dir = string.Empty;
        }

        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            dir = Environment.CurrentDirectory;
        }

        string stem = Path.GetFileNameWithoutExtension(
            string.IsNullOrWhiteSpace(sourceName) ? "annotated" : sourceName);
        if (stem.Length == 0 || stem == "?")
        {
            stem = "annotated";
        }

        string candidate = Path.Combine(dir, stem + ".annotated.png");
        for (int i = 2; i < 100 && File.Exists(candidate); i++)
        {
            candidate = Path.Combine(dir, stem + ".annotated-" + i + ".png");
        }

        return candidate;
    }

    /// <summary>
    ///     Ctrl+C / Escape host effect. The reducer owns the busy split (Quit
    ///     folds to abort while a turn runs, to quit when idle), so this host
    ///     reads the resolved <see cref="ChatAction"/> only for the Ctrl+C
    ///     confirmation window and no longer duplicates an <c>IsRunning()</c>
    ///     branch. The first idle Ctrl+C hints, the second within the window
    ///     quits (#49 PR1).
    /// </summary>
    private void HandleAbortGesture(ChatAction resolved, UiKey uiKey)
    {
        // Both gestures express "leave the turn, or the app when idle" — exactly
        // the Quit fold in KeyActionRegistry. Dispatching Quit (not the resolved
        // Abort, which always aborts even when idle) is what keeps the busy/idle
        // decision in the reducer instead of a host IsRunning check.
        var effect = host._replStore.Dispatch(new AppMsg.KeyInput(ChatAction.Quit, uiKey));

        if (effect is TuiEffect.AbortAgent)
        {
            // #49 PR1: single cancellation ingress — the coordinator orders this
            // against any in-flight approval decision and unblocks its waiter.
            host.Coordinator.RequestCancel(host.Agent);
            host.Pipeline.AbortAndRestoreQueuedToComposer(); // abort drops the queue but returns the newest payload to the composer (O7 #1176) — never sent after a kill
            host.Bridge.AppendSystemLine(resolved == ChatAction.Abort
                ? "^C — прерываю текущий ход…"
                : "Esc — прерываю текущий ход…");
            return;
        }

        if (resolved == ChatAction.Abort)
        {
            // The double-press window is the only timing this host still owns;
            // the reducer has already decided this is an idle quit.
            long now = Environment.TickCount64;
            if (!IsSecondPressWithinWindow(now, host._lastIdleAbortMs))
            {
                host._lastIdleAbortMs = now;
                host.Bridge.AppendSystemLine("^C — ещё раз для выхода");
                return;
            }

            ReplInputLoopLog.SecondIdleAbortQuitting(host.Log);
        }

        host._quitRequested = true;
    }

    /// <summary>
    ///     The #49 PR1 double-press window as a pure predicate, so the timer
    ///     decision is testable without a terminal. A negative
    ///     <paramref name="lastPressMs"/> is the "no press yet" sentinel and
    ///     never satisfies the window (CE-5: uptime arithmetic must not let the
    ///     first Ctrl+C quit on its own).
    /// </summary>
    internal static bool IsSecondPressWithinWindow(long now, long lastPressMs) =>
        lastPressMs >= 0 && now - lastPressMs <= QuitGestureWindowMs;

    /// <summary>Copy-on-select release (killer features §P6.4): extracts the
    /// selected text from the back buffer and ships it via OSC 52 — terminals
    /// that support the sequence copy it, everything else ignores silently.</summary>
    private async Task FinishSelectionAsync(int releaseX, int releaseY, CancellationToken ct)
    {
        int cols = Math.Max(1, host.ScreenSession.CurrentCols);
        int rows = Math.Max(1, host.ScreenSession.CurrentRows);
        string? text = host._selection.OnRelease(
            releaseX, releaseY, cols, rows,
            (x, y) => x >= 0 && x < cols && y >= 0 && y < rows ? host.ScreenSession.PaintBuffer.Get(x, y) : Cell.Blank);
        if (string.IsNullOrEmpty(text))
        {
            host._wake.Writer.TryWrite(null);
            return;
        }

        await host.Backend.WriteAsync(ReplLifecycle.Utf8(Osc52Clipboard.Encode(text)), ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine($"⧉ скопировано {text.Length} симв.");
        host._selection.Clear();
        host._wake.Writer.TryWrite(null);
    }

    // ── #3111: session-scoped prompt history ────────────────────────────────

    /// <summary>
    ///     Rebinds the composer rail when the active session changed. Called
    ///     on every input event so every switch path (palette, quick-switch
    ///     chord, new session) is covered in one place — no edit to the switch
    ///     manager itself. The rail is cleared first, so an unrecoverable
    ///     prompt from the previous conversation cannot leak into the new one.
    /// </summary>
    private void SyncPromptHistoryForSession()
    {
        string sessionId = host.SessionModel.Id;
        if (string.Equals(_promptHistorySessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        _promptHistorySessionId = sessionId;
        PopulatePromptHistory(host._composer, _promptHistory, sessionId);
    }

    /// <summary>
    ///     Records a submitted line against its session. Trims like the
    ///     composer rail does, so the two stay in step; blank lines are ignored
    ///     by <see cref="SessionPromptHistory.Record" /> itself.
    /// </summary>
    internal static void RecordSubmittedPrompt(SessionPromptHistory store, string sessionId, string text) =>
        store.Record(SessionId.Create(sessionId), text?.Trim());

    /// <summary>
    ///     Refills the composer's readline rail from the session store. Reads
    ///     through <see cref="SessionPromptHistory.At" /> (index from newest)
    ///     and pushes oldest-first, so the rail's newest entry is the last
    ///     prompt submitted in this session — exactly what Up recalls first.
    ///     Internal for the session-isolation behavior test.
    /// </summary>
    internal static void PopulatePromptHistory(ComposerController composer, SessionPromptHistory store, string sessionId)
    {
        composer.History.Clear();
        var id = SessionId.Create(sessionId);
        int count = store.Count(id);
        for (int indexFromNewest = count - 1; indexFromNewest >= 0; indexFromNewest--)
        {
            string? entry = store.At(id, indexFromNewest);
            if (entry is not null)
            {
                composer.History.Push(entry);
            }
        }
    }
}

/// <summary>
///     SG9: BCL <c>[LoggerMessage]</c> delegates for <see cref="ReplInputLoop" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ReplInputLoopLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Second idle Ctrl+C — quitting CellForge REPL")]
    public static partial void SecondIdleAbortQuitting(ILogger logger);
}
