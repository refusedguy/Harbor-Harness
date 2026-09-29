using System.Collections.Immutable;
using System.Text;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// A layout change is damage (#508 regression: black bands between painted rows).
///
/// <para>
/// Owner observation, verbatim in substance: a trivial session — one user
/// message, one short reply — renders with <i>strips of the theme background
/// missing</i>. Between correctly-painted rows sit bands of raw terminal black
/// with the previous frame's glyphs showing through them, in the header (right
/// after the tab title), right of the <c>INPUT</c> label, and at the end of the
/// status line. No scrolling: scroll away and back, all the bands disappear.
/// Scrolling forces <b>broad</b> damage, a full scan. So only the
/// <b>narrow</b> damage path is at fault.
/// </para>
///
/// <para>
/// Root cause: <c>ChatScreen.SyncTabStrip</c> drives the tab-strip split's ratio
/// to claim (or release) its two rows. That re-homes every panel beneath the
/// strip at once — the transcript's tail, the composer, its INPUT title row and
/// the status row all change row in the same frame. But <c>SetRatio</c> is an
/// INSTANT retarget: it arms no spring, so <c>LayoutTree.IsAnimating</c> stays
/// false, and the host's damage policy
/// (<c>ReplLifecycle.ApplyFrameDamageHints</c>) only widens the scan for
/// <c>IsAnimating</c>, for input, or for the transcript's own ledger. A
/// streaming frame takes the narrow path, the diff rescans only the status row
/// and the transcript's dirty rect, and the moved rows are never written. The
/// narrow dirty-rect added in #511 cannot explain this on its own — it explains
/// no band in the header or the INPUT row, because those rects are not its to
/// cover. The seam that lets a narrow frame outlive a moved layout is.
/// </para>
///
/// <para>
/// The oracle is the broad path itself, so the assertion needs no theme, no
/// palette and no golden: after a narrow frame the terminal mirror
/// (<c>DiffEngine.Front</c>) must equal what the frame painted
/// (<c>ScreenSession.Back</c>) over the whole grid, and must equal a twin session
/// that ran the very same frame wide. Both are checked; the failure message
/// prints the offending rows so a band is legible instead of a boolean.
/// </para>
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class FrameLayoutDamageTests
{
    private const int Cols = 100;
    private const int Rows = 30;

    [Test]
    public async Task TabStrip_Claiming_Rows_Leaves_No_Unpainted_Band()
    {
        var narrow = Session.Create(Cols, Rows);
        var wide = Session.Create(Cols, Rows);

        // Cold start + the two appends: the strip is hidden, so these frames
        // move nothing and the baseline paints in full.
        foreach (var s in new[] { narrow, wide })
        {
            _ = s.SeedTranscript();
            _ = s.Frame(Cols, Rows, HiddenStrip, narrowDamage: false);
        }

        // The strip appears mid-session (#389) and claims its two rows. The
        // fix makes exactly this frame go wide; the assertion below is about
        // WHAT reaches the terminal, not about which policy ran — the
        // "stay narrow when nothing moved" half is the second test.
        _ = narrow.Frame(Cols, Rows, ShownStrip, narrowDamage: true);
        _ = wide.Frame(Cols, Rows, ShownStrip, narrowDamage: false);

        // The frame is allowed to be narrow only if every cell it painted
        // reached the terminal.
        await Assert.That(narrow.Screen.Engine.FrontMatches(narrow.Screen.Back))
            .IsTrue()
            .Because(
                "a narrow frame must repaint every cell the layout move dirtied " +
                "(FRONT mirror vs BACK paint). Unpainted rows:\n" +
                Describe(narrow.Screen.Engine.Front, narrow.Screen.Back));

        // …and it must agree, cell for cell, with the same frame run wide.
        string drift = Diff(wide.Screen.Back, narrow.Screen.Engine.Front);
        await Assert.That(drift).IsEqualTo(string.Empty).Because(
            "the narrow frame's terminal state must equal the wide frame's grid — " +
            "the difference IS the black band the user sees:\n" + drift);
    }

    /// <summary>
    /// The companion guard: a frame that moves NOTHING must keep the narrow
    /// path. Without it, "mark every frame viewport-wide" would satisfy the
    /// band test above and quietly give back the whole point of #465/#511
    /// (a full scan on every paced streaming tick).
    /// </summary>
    [Test]
    public async Task Steady_Streaming_Frames_Stay_On_The_Narrow_Path()
    {
        var probe = Session.Create(Cols, Rows);
        var stream = probe.SeedTranscript();
        _ = probe.Frame(Cols, Rows, HiddenStrip, narrowDamage: false);

        for (int tick = 0; tick < 6; tick++)
        {
            stream.Push($"streamed line {tick} of the answer\n");
            probe.Chat.Timeline.Timeline.MarkDirty(stream);

            bool wide = probe.Frame(Cols, Rows, HiddenStrip, narrowDamage: true);
            await Assert.That(wide).IsFalse().Because(
                $"the strip re-derives the same (hidden) ratio on frame {tick}, so " +
                "nothing re-homed and the streaming frame must stay narrow");
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────

    private static SessionId Sid(string id) => SessionId.Create(id);

    /// <summary>One tab — <see cref="TabStripState.ShouldRender"/> is false, so
    /// the strip claims no rows. This is a fresh single-session REPL.</summary>
    private static readonly TabStripState HiddenStrip = TabStripState.Empty;

    /// <summary>Two tabs — the strip becomes visible and claims
    /// <see cref="SessionTabStripPanel.PreferredRows"/> rows out of the band,
    /// which re-homes everything under it.</summary>
    private static readonly TabStripState ShownStrip = new()
    {
        Tabs = ImmutableArray.Create(
            new SessionTab(Sid("s1"), "consoleex"),
            new SessionTab(Sid("s2"), "docs")),
        ActiveTabId = Sid("s1")
    };

    /// <summary>
    /// The host frame, verbatim in shape: <c>ReplLifecycle.RenderFrameAsync</c> +
    /// <c>ApplyFrameDamageHints</c>. <c>SyncTabStrip</c> runs before the solve,
    /// the damage policy after the paint, and the flush closes the frame.
    /// <paramref name="narrowDamage"/> selects the conservative policy (status
    /// row + the transcript's own narrow rects); false forces the wide path.
    /// Returns whether the frame actually had to full-scan.
    /// </summary>
    private sealed class Session
    {
        public readonly ScreenSession Screen;
        public readonly ChatScreen Chat;
        public readonly Rect[] Fx = new Rect[VirtualizedChatTimeline.MaxFxDamage];

        private Session(ScreenSession screen, ChatScreen chat)
        {
            Screen = screen;
            Chat = chat;
        }

        public static Session Create(int cols, int rows) =>
            new(
                new ScreenSession(new AnsiWriter(new RecordingBackend(), syncUpdates: true), cols, rows),
                ChatScreen.Build(new ComposerController(), new StatusViewModel { Model = "kilocode/hy3" }));

        /// <summary>The trivial session the defect was reported on: one user
        /// message, one streaming assistant reply.</summary>
        public StreamingMarkdownBlock SeedTranscript()
        {
            var tl = Chat.Timeline.Timeline;
            tl.Append(new UserBlock("как дела?"));
            var stream = new StreamingMarkdownBlock();
            tl.Append(stream);
            return stream;
        }

        public bool Frame(int cols, int rows, TabStripState strip, bool narrowDamage)
        {
            Screen.CheckAutoSize();
            Chat.SyncTabStrip(strip, rows);
            Chat.Tree.Solve(cols, rows);

            var tlRect = Chat.Timeline.Rect;
            _ = Chat.Timeline.Timeline.PrepareFrame(tlRect.Width > 0 ? tlRect.Width : cols, Math.Max(0, tlRect.Height));

            Screen.BeginFrame();
            Chat.Tree.PaintAll(Screen.Back);

            bool wide = Chat.Timeline.Timeline.ConsumeFrameDamage(Fx, out int fxCount);
            if (narrowDamage && !wide)
            {
                var statusRect = Chat.Status.Rect;
                if (statusRect.Height > 0)
                {
                    Screen.Damage(new Rect(0, statusRect.Y, cols, statusRect.Height));
                }

                for (int i = 0; i < fxCount; i++)
                {
                    Screen.Damage(Fx[i]);
                }
            }

            Screen.FlushFrame();
            return wide;
        }
    }

    // ── Diagnostics ────────────────────────────────────────────────────────

    /// <summary>Every row where <paramref name="expected"/> and
    /// <paramref name="actual"/> disagree, as <c>row | expected | actual</c> —
    /// the band, spelled out.</summary>
    private static string Diff(ScreenBuffer expected, ScreenBuffer actual)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < expected.Rows; y++)
        {
            string e = Row(expected, y);
            string a = Row(actual, y);
            if (!string.Equals(e, a, StringComparison.Ordinal))
            {
                sb.Append("row ").Append(y).Append("\n  painted: |").Append(e).Append("|\n  on wire: |").Append(a).Append("|\n");
            }
        }

        return sb.ToString();
    }

    private static string Describe(ScreenBuffer expected, ScreenBuffer actual)
    {
        string drift = Diff(expected, actual);
        return drift.Length > 0 ? drift : "(grid agrees — check FRONT/BACK geometry)";
    }

    private static string Row(ScreenBuffer buffer, int y)
    {
        var sb = new StringBuilder(buffer.Cols);
        for (int x = 0; x < buffer.Cols; x++)
        {
            var cell = buffer.Get(x, y);
            if (cell.Width == Cell.WSkip)
            {
                sb.Append('·');
                continue;
            }

            int r = cell.Rune;
            sb.Append(r is > 0x20 and < 0x7F ? (char)r : '·');
        }

        return sb.ToString().TrimEnd();
    }
}
