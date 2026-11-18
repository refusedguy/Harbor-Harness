using Harbor.TestKit;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #2648: the span walk on the frame path must return exactly what
///     <c>string.Split(["\r\n", "\r", "\n"], StringSplitOptions.None)</c>
///     returned, including empty and trailing entries. The <c>IEnumerable</c>
///     branch must keep its null-to-empty coercion.
/// </summary>
public class PanelAdapterLineSplitTests
{
    private static IReadOnlyList<string> Rows(object? widget) =>
        CellForgePanelAdapter.RenderToRows(
            new TestPanelProvider("lines", "Lines", widget),
            new PanelContext(new UiState(), 80, 24));

    // Count plus a delimited join pins row order, empty rows and trailing rows.
    private static string Shape(object? widget)
    {
        IReadOnlyList<string> rows = Rows(widget);
        return $"{rows.Count}:{string.Join("|", rows)}";
    }

    [Test]
    public async Task Split_Keeps_StringSplit_Shape()
    {
        await Assert.That(Shape("")).IsEqualTo("1:");
        await Assert.That(Shape("a")).IsEqualTo("1:a");
        await Assert.That(Shape("a\nb")).IsEqualTo("2:a|b");
        await Assert.That(Shape("a\r\nb")).IsEqualTo("2:a|b");
        await Assert.That(Shape("a\rb")).IsEqualTo("2:a|b");
        await Assert.That(Shape("a\n")).IsEqualTo("2:a|");
        await Assert.That(Shape("\n")).IsEqualTo("2:|");
        await Assert.That(Shape("a\n\nb")).IsEqualTo("3:a||b");
        await Assert.That(Shape("a\r\n\r\nb")).IsEqualTo("3:a||b");
        await Assert.That(Shape("\r\n")).IsEqualTo("2:|");
    }

    [Test]
    public async Task ToString_Fallback_Splits_Too()
    {
        await Assert.That(Shape(new CarriageReturnText())).IsEqualTo("3:x||y");
    }

    private sealed record CarriageReturnText
    {
        public override string ToString() => "x\r\n\r\ny";
    }

    [Test]
    public async Task Sized_IEnumerable_Coalesces_Nulls()
    {
        var lines = new LinkedList<string>();
        lines.AddLast("a");
        lines.AddLast(null!);
        lines.AddLast("b");

        await Assert.That(Shape(lines)).IsEqualTo("3:a||b");
    }

    [Test]
    public async Task Lazy_IEnumerable_Is_Materialized()
    {
        await Assert.That(Shape(Lines())).IsEqualTo("3:a||b");

        static IEnumerable<string> Lines()
        {
            yield return "a";
            yield return null!;
            yield return "b";
        }
    }
}
