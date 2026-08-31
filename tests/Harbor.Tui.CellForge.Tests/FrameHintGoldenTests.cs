using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Partial-scan golden (renderer-moat sprint): the hinted diff path must be
/// byte-identical to the fused full-scan path over the same scripted traffic
/// — streaming appends, scrolls, warn-pulse frames, and quiet spinner ticks.
/// Two identical sessions run side by side: the reference one full-scans
/// every frame, the other follows the conservative damage contract
/// (viewport-wide on content shifts, narrow fx rects on quiet animation
/// frames) exactly like <c>CellForgeReplRunner.ApplyFrameDamageHints</c>.
/// </summary>
public class FrameHintGoldenTests
{
    private sealed class Session
    {
        public readonly ScreenSession Screen;
        public readonly ChatScreen Chat;
        public readonly RecordingBackend Backend;

        private Session(ScreenSession screen, ChatScreen chat, RecordingBackend backend)
        {
            Screen = screen;
            Chat = chat;
            Backend = backend;
        }

        public VirtualizedChatTimeline Timeline => Chat.Timeline.Timeline;

        public static Session Create(int cols, int rows)
        {
            var backend = new RecordingBackend();
            var screen = new ScreenSession(new AnsiWriter(backend, syncUpdates: true), cols, rows);
            var chat = ChatScreen.Build(new ComposerController(), new StatusViewModel { Model = "kilocode/hy3" });
            chat.Status.Vm.Mode = StatusBarMode.Running; // spinner animates every frame
            return new Session(screen, chat, backend);
        }

        /// <summary>One scripted frame: layout, paint, damage policy, flush.
        /// Mirrors CellForgeReplRunner.RenderFrameAsync + ApplyFrameDamageHints;
        /// <paramref name="fx"/> selects the hinted policy (reference runs with false).</summary>
        public void Frame(int cols, int rows, bool broad, Rect[] fxScratch, bool fx)
        {
            var screen = Screen;
            var chat = Chat;
            screen.CheckAutoSize();
            chat.Tree.Solve(cols, rows);
            var tlRect = chat.Timeline.Rect;
            _ = chat.Timeline.Timeline.PrepareFrame(tlRect.Width > 0 ? tlRect.Width : cols, Math.Max(0, tlRect.Height));

            screen.BeginFrame();
            chat.Tree.PaintAll(screen.Back);

            bool fullScan = broad;
            int fxCount = 0;
            if (fx)
            {
                fullScan = chat.Timeline.Timeline.ConsumeFrameDamage(fxScratch, out fxCount);
            }
            else
            {
                chat.Timeline.Timeline.ConsumeFrameDamage(fxScratch, out _);
            }

            if (!fullScan)
            {
                // Status bar row (spinner, mascot, crossfades) — always hinted:
                // one row is negligible next to the hundreds it protects.
                var statusRect = chat.Status.Rect;
                if (statusRect.Height > 0)
                {
                    screen.Damage(new Rect(0, statusRect.Y, cols, statusRect.Height));
                }

                for (int i = 0; i < fxCount; i++)
                {
                    screen.Damage(fxScratch[i]);
                }
            }

            screen.FlushFrame();
        }
    }

    [Test]
    public async Task Hinted_Diff_Output_Is_ByteIdentical_To_FullScan()
    {
        const int cols = 100;
        const int rows = 40;
        var full = Session.Create(cols, rows);
        var hinted = Session.Create(cols, rows);
        var sessions = new[] { full, hinted };
        var fxScratch = new Rect[VirtualizedChatTimeline.MaxFxDamage];
        var gates = new ApprovalGateView[sessions.Length];

        for (int frame = 0; frame < 24; frame++)
        {
            bool inputDriven = false;

            if (frame == 0)
            {
                foreach (var s in sessions)
                {
                    s.Timeline.Append(new UserBlock($"initial prompt with enough words to wrap the row {frame}"));
                    s.Timeline.Append(new AssistantMarkdownBlock($"## Answer {frame}\n- alpha\n- beta\n`code` tail.\n"));
                    s.Timeline.Append(new ToolCallBlock(new ToolCallInfo($"t{frame}", "read", "{{\"path\":\"src/x.cs\"}}")));
                }
            }
            else if (frame == 2)
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    var g = new ApprovalGateView("bash", "rm -rf build/");
                    sessions[i].Timeline.Append(g);
                    g.BeginWarnPulse(sessions[i].Timeline.CurrentTick);
                    gates[i] = g;
                }

                inputDriven = true; // gate arrives through an event — REPL marks broad
            }
            else if (frame == 5)
            {
                foreach (var s in sessions)
                {
                    s.Timeline.Append(new UserBlock("second prompt mid-stream"));
                }

                inputDriven = true;
            }
            else if (frame == 9)
            {
                foreach (var s in sessions)
                {
                    s.Timeline.ScrollUp(3);
                }

                inputDriven = true;
            }
            else if (frame == 12)
            {
                foreach (var s in sessions)
                {
                    s.Timeline.MarkLastDirty(); // streaming tail grew
                }
            }
            else if (frame == 16)
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    _ = gates[i].TryDecide(ApprovalChoice.Approve);
                    sessions[i].Timeline.MarkLastDirty(); // bridge stamps the card
                }
            }

            full.Frame(cols, rows, broad: true, fxScratch, fx: false);
            hinted.Frame(cols, rows, broad: frame == 0 || inputDriven, fxScratch, fx: true);
        }

        await Assert.That(hinted.Backend.Text).IsEqualTo(full.Backend.Text);
        await Assert.That(hinted.Screen.Engine.FrontMatches(hinted.Screen.Back)).IsTrue();
        await Assert.That(full.Screen.Engine.FrontMatches(full.Screen.Back)).IsTrue();
    }
}
