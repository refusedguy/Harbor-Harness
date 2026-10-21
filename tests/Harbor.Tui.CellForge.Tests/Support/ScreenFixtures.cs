using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Paints a screen and reads the buffer back.
/// </summary>
/// <remarks>
///     <para>
///         The CellForge suites paint panels into a <see cref="ScreenBuffer" /> and
///         then turn that buffer into text. Each of those steps was written out
///         again in every file that needed it — eight copies of the buffer dump,
///         two of the paint-all loop, two of the last-frame dump.
///     </para>
/// </remarks>
internal static class ScreenFixtures
{
    internal static string Art(ScreenBuffer buffer) => TestRender.Art(buffer).TrimEnd('\n');

    internal static void PaintAll(ChatScreen screen, ScreenBuffer buffer)
    {
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }
    }

    /// <summary>Paints all panels <paramref name="frames" /> times, returning only the LAST frame's art.</summary>
    internal static string PaintLastFrame(ChatScreen screen, ScreenBuffer buffer, int frames)
    {
        for (int i = 0; i < frames - 1; i++)
        {
            foreach (var panel in screen.Tree.Panels)
            {
                panel.Paint(buffer);
            }
        }

        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        return TestRender.Art(buffer);
    }

    internal static string Row(ScreenBuffer buffer, int y, int width)
    {
        var sb = new StringBuilder(width);
        for (int x = 0; x < width; x++)
        {
            var cell = buffer.Get(x, y);
            sb.Append(cell.Rune == 0 ? ' ' : (char)cell.Rune);
        }

        return sb.ToString();
    }

    internal static ScreenBuffer PaintToBuffer(TableBlock block, int width = 40)
    {
        var buffer = TestRender.Buffer(width, block.Measure(width).MinLines);
        block.Paint(TestRender.Ctx(buffer));
        return buffer;
    }

    internal static ChatScreen BuildScreen() =>
        ChatScreen.Build(new ComposerController(), new StatusViewModel(), includeSidebar: false);
}
