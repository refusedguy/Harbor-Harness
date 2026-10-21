namespace Harbor.Ui.Framework.Tests;

using Harbor.Ui.Framework.Rendering;

/// <summary>
/// The grid factory for this project, the local twin of the shared
/// <c>Harbor.TestKit.TestRender.Buffer</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shared render harness lives in <c>tests/Harbor.TestKit.Tui</c>, the
/// UI-scoped sibling of the test kit, because the vocabulary it speaks
/// (<c>ScreenBuffer</c>, <c>BlockPaintContext</c>, <c>IChatBlock</c>) belongs
/// to <c>Harbor.Ui.Framework.Rendering</c>. This project does not reference
/// that sibling, and adding the reference would widen its closure by
/// <c>Harbor.Tui.CellForge.Engine</c>, a CellForge assembly in a project
/// whose perf gates measure the Ui.Framework slice. The repository's rule is
/// that a wrong edge between test projects is paid for twice, once in closure
/// and once in test discovery, so the helper stays local.
/// </para>
/// <para>
/// What the shared harness is not for: the grid here is the subject under
/// test, not the surface a widget paints into. These suites drive the
/// cell-diff encoder, the differential pipeline and the streaming markdown
/// renderer with grids they build themselves, so the construction belongs to
/// the project.
/// </para>
/// </remarks>
internal static class TestGrid
{
    /// <summary>A grid of the given size.</summary>
    internal static ScreenBuffer Buffer(int cols, int rows) => new(cols, rows);

    /// <summary>A grid of the given size with every cell holding <paramref name="fill" />.</summary>
    internal static ScreenBuffer Filled(int cols, int rows, char fill)
    {
        var buffer = Buffer(cols, rows);
        for (int y = 0; y < buffer.Rows; y++)
        {
            buffer.SetText(0, y, new string(fill, buffer.Cols), CellStyle.Plain);
        }

        return buffer;
    }
}
