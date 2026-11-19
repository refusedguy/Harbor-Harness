// #3371 — ChatScreen.Build must give PanelNodeAdapter a real product caller.
// The golden below drives the composition root that Build assembles through
// NodeFrame and pins that a panel owned by the real screen lands in the engine
// frame. The adapter is not a test-only leaf: Build is the only constructor of
// this root, and the frame is rendered from the panels it adapted.

using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

[NotInParallel("pty")]
public class ChatScreenCompositionTests
{
    private const int Cols = 80;
    private const int Rows = 12;

    [Test]
    public async Task Build_CompositionRoot_PaintsAdaptedPanelIntoGoldenFrame()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "kilocode/hy3" };

        ChatScreen screen = ChatScreen.Build(composer, status, includeSidebar: false);
        screen.Composer.Placeholder = "COMPOSED-PANEL";

        FlexSplitNode? compositionRoot = screen.CompositionRoot;
        await Assert.That(compositionRoot).IsNotNull();
        FlexSplitNode root = compositionRoot!;
        await Assert.That(root.Count).IsEqualTo(3);

        var context = new PrepaintContext();
        var buffer = new ScreenBuffer(Cols, Rows);
        NodeFrame.Render(
            root,
            context,
            buffer,
            new Constraints(Cols, Rows),
            new Rect(0, 0, Cols, Rows));

        string first = Art(buffer);
        await Assert.That(first).Contains("COMPOSED-PANEL").Because(
            "the composer panel from ChatScreen.Build must appear in the engine frame");

        // Same panels, same keys, same frame: the id-keyed adapters keep their
        // prepaint records instead of rebinding by position.
        var replay = new ScreenBuffer(Cols, Rows);
        NodeFrame.Render(
            root,
            context,
            replay,
            new Constraints(Cols, Rows),
            new Rect(0, 0, Cols, Rows));

        await Assert.That(Art(replay)).IsEqualTo(first);
    }

    private static string Art(ScreenBuffer buffer)
    {
        var text = new System.Text.StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                text.Append((char)buffer.Get(x, y).Rune);
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
