using System.Linq;
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
                _ = host._replStore.Dispatch(new UiMsg.InputText(host._composer.Buffer.SnapshotText()));
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
                host._selection.OnDrag(evt.Mouse.Column, evt.Mouse.Row);
                host._wake.Writer.TryWrite(null); // repaint the growing highlight
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.Release
                                           && evt.Mouse.Button == MouseButton.Left:
                await FinishSelectionAsync(evt.Mouse.Column, evt.Mouse.Row, ct).ConfigureAwait(false);
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.WheelUp:
                // Store-first scroll (epic C): one line-msg per row so the TEA
                // store tracks the same offset the timeline paints locally.
                for (int i = 0; i < WheelScrollLines; i++)
                {
                    _ = host._replStore.Dispatch(VirtualizedChatTimeline.LineUpMsg());
                }

                host._timeline.ScrollBy(-WheelScrollLines);
                break;

            case InputEventKind.Mouse when evt.Mouse.Type == MouseEventType.WheelDown:
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

        // Slash shortcut: typing '/' on an empty composer opens the command
        // palette directly, skipping manual entry.
        if (key.Key == KeyCode.Char
            && key.Modifiers is KeyModifiers.None or KeyModifiers.Shift
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
        // Msg-bound chords (scroll anchors) additionally dual-write their UiMsg
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
            var resolved = host._keyMap.Resolve(uiKey);
            if (resolved != ChatAction.None)
            {
                _ = host._replStore.Dispatch(new UiMsg.KeyInput(resolved, uiKey));
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

    /// <summary>First idle Ctrl+C hints, second one within the window quits.
    /// While the agent runs, Ctrl+C aborts the current turn instead.</summary>
    private void HandleAbortGesture()
    {
        if (host.Agent.State.IsRunning)
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
