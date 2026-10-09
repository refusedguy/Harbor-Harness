using System.Linq;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.State;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.Logging;

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
        host._broadDamageNextFrame = true;
        switch (evt.Kind)
        {
            case InputEventKind.Key:
                await HandleKeyAsync(evt.Key, ct).ConfigureAwait(false);
                break;

            case InputEventKind.Capability when evt.Capability.Kind == CapabilityEventKind.Osc11BackgroundReport:
                host.Lifecycle.ApplyAutoTheme(evt.Capability);
                break;

            case InputEventKind.Capability when evt.Capability.Kind == CapabilityEventKind.Osc99NotifyReport:
                host.Pipeline.NotifyHint = DesktopNotifyKind.Osc99;
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

    private async Task HandleKeyAsync(KeyEvent key, CancellationToken ct)
    {
        // Key routing can mutate gates, palette, vim state or the composer —
        // the next frame takes the conservative full-scan path.
        host._broadDamageNextFrame = true;

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
        // everything else is swallowed. Ctrl+S bakes the annotated copy; that
        // is a host effect, so it never reaches the overlay router: the
        // reducer stays pure and file I/O lives here.
        if (host.Screen.Markup.Visible)
        {
            if (IsMarkupSave(key))
            {
                await SaveMarkupAsync(ct).ConfigureAwait(false);
            }
            else if (host.Screen.Markup.HandleKey(key) is { } markupMsg)
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
            // Markup open (issue #400): `m` over the zoomed image swaps the
            // viewer for the markup overlay on the same block. The viewer
            // hides, the store opens the session (snapshotting the scroll for
            // restore-on-close), and the snapshot is pushed immediately so no
            // key slips through before the next frame sync.
            if (IsMarkupAnnotateKey(key) && host.Images.Source.HasValue)
            {
                var block = host.Images.Source.Value;
                host.Images.Hide();
                _ = host._replStore.Dispatch(new ChatAppMsg.OpenMarkup(
                    block.FullPath, block.Name, block.PixelWidth, block.PixelHeight,
                    host._replStore.State.Ui.ScrollOffset));
                host.Screen.SyncMarkup(host._replStore.State.Chat.Markup);
                host._broadDamageNextFrame = true;
                host._wake.Writer.TryWrite(null);
                return;
            }

            if (host.Images.HandleKey(key))
            {
                host._wake.Writer.TryWrite(null);
            }

            return;
        }

        // Command palette: ctrl+p toggles; a visible palette claims keys first
        // so Enter/Esc/letters never leak into the approval gate or composer.
        if (key.Key == KeyCode.Char && key.Modifiers == KeyModifiers.Ctrl
            && char.ToLowerInvariant((char)key.Character.Value) == 'p')
        {
            if (host._palette.Visible)
            {
                host._palette.Hide();
            }
            else
            {
                host.Commands.OpenCommandPalette();
            }

            host._wake.Writer.TryWrite(null);
            return;
        }

        // Jump palette: ctrl+j toggles the /jump frame, dispatched through the
        // catalog exactly as the ctrl+p block above dispatches its own palette
        // (#857). This chord is intercepted HERE, ahead of the store
        // dual-write further down, because ChatKeyMap's `ChatAction.JumpPalette`
        // is the panel-plane route: it toggles a `Center`-placed
        // IPanelProvider whose OnKey no host can reach, so it resolved and
        // painted nothing. Claiming the key here gives Ctrl+J a route that
        // exists, without adding a second input path — and the bare-LF alias
        // stays on the keymap for the desktop host, which is not this loop.
        if (key.Key == KeyCode.Char && key.Modifiers == KeyModifiers.Ctrl
            && char.ToLowerInvariant((char)key.Character.Value) == 'j')
        {
            // Only the jump frame itself toggles closed. Any other visible
            // frame (ctrl+p's command list, /tree) is somebody else's palette
            // and must survive a Ctrl+J.
            if (host._palette.Visible && host._palette.CurrentBreadcrumb == JumpCommand.Breadcrumb)
            {
                host._palette.Hide();
            }
            else
            {
                await host.Commands.ExecutePaletteItemAsync(
                    new CommandItem(JumpCommand.CommandId, JumpCommandTitle), ct).ConfigureAwait(false);
            }

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
            host._broadDamageNextFrame = true;
            host._wake.Writer.TryWrite(null);
            return;
        }

        // Slash shortcut: typing '/' on an empty composer opens the command
        // palette directly, skipping manual entry.
        if (key.Key == KeyCode.Char
            && key.Modifiers.AcceptsTypedChar()
            && (char)key.Character.Value == '/'
            && host._composer.Buffer.IsEmpty)
        {
            host.Commands.OpenSlashPalette();
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
        // pollute store input. The returned effect is sunk on purpose: submit
        // executes through Pipeline.SubmitAsync on the composer buffer below,
        // abort through HandleAbortGesture, quit through the Ctrl+C×2 gesture —
        // running them from the store effect too would fire every gesture twice.
        if (KeyEventMapper.TryMap(key, out var dto))
        {
            var uiKey = KeyEventAdapter.ToUiKey(dto);
            // Resolved against the live state: Ctrl+Tab is shared with panel
            // cycling and only means "next tab" while a multi-tab strip is open
            // (#389). A host that passes no snapshot gets the pre-#389 mapping.
            var resolved = host._keyMap.Resolve(uiKey, host._replStore.State);
            if (resolved != ChatAction.None)
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

        var action = host._vim.HandleKey(key, host._composer);
        switch (action)
        {
            case ComposerAction.Submitted:
                await host.Pipeline.SubmitAsync(ct).ConfigureAwait(false);
                break;

            case ComposerAction.Aborted:
                HandleAbortGesture();
                break;

            case ComposerAction.Edited:
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

    /// <summary>Ctrl+S over the markup overlay: bake the annotated copy (a host effect).</summary>
    private static bool IsMarkupSave(KeyEvent key) =>
        key.Key == KeyCode.Char
        && key.EventType is KeyEventType.Press or KeyEventType.Repeat
        && (key.Modifiers & KeyModifiers.Ctrl) != 0
        && (key.Character.ToString() == "s" || key.Character.ToString() == "S");

    /// <summary>`m` over the image viewer: swap it for the markup overlay on the same block.</summary>
    private static bool IsMarkupAnnotateKey(KeyEvent key) =>
        key.Key == KeyCode.Char
        && key.EventType is KeyEventType.Press or KeyEventType.Repeat
        && key.Modifiers.IsUnmodified()
        && key.Character.ToString() == "m";

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

    /// <summary>First idle Ctrl+C hints, second one within the window quits.
    /// While the agent runs, Ctrl+C aborts the current turn instead.</summary>
    private void HandleAbortGesture()
    {
        if (host.Agent.IsRunning())
        {
            // #49 PR1: single cancellation ingress — the coordinator orders this
            // against any in-flight approval decision and unblocks its waiter.
            host.Coordinator.RequestCancel(host.Agent);
            host.Pipeline.ClearQueue(); // abort drops queued prompts — never sent after a kill
            host.Bridge.AppendSystemLine("^C — прерываю текущий ход…");
            host._wake.Writer.TryWrite(null);
            return;
        }

        long now = Environment.TickCount64;
        if (now - host._lastIdleAbortMs <= QuitGestureWindowMs)
        {
            host.Log.LogInformation("Second idle Ctrl+C — quitting CellForge REPL");
            host._quitRequested = true;
            host._wake.Writer.TryWrite(null);
            return;
        }

        host._lastIdleAbortMs = now;
        host.Bridge.AppendSystemLine("^C — ещё раз для выхода");
        host._wake.Writer.TryWrite(null);
    }

    /// <summary>Copy-on-select release (killer features §P6.4): extracts the
    /// selected text from the back buffer and ships it via OSC 52 — terminals
    /// that support the sequence copy it, everything else ignores silently.</summary>
    private async Task FinishSelectionAsync(int releaseX, int releaseY, CancellationToken ct)
    {
        int cols = Math.Max(1, host.ScreenSession.CurrentCols);
        int rows = Math.Max(1, host.ScreenSession.CurrentRows);
        string? text = host._selection.OnRelease(
            releaseX, releaseY, cols, rows,
            (x, y) => x >= 0 && x < cols && y >= 0 && y < rows ? host.ScreenSession.Back.Get(x, y) : Cell.Blank);
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
}
