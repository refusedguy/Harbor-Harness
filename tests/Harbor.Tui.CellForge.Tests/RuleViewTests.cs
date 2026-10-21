using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class RuleViewTests
{
    private static string Art(int width, string? title, RuleJustify justify = RuleJustify.Center)
    {
        var buffer = TestRender.Buffer(width, 1);
        RuleView.Paint(buffer, 0, 0, width, title, justify);
        return TestRender.Art(buffer).TrimEnd('\n');
    }

    [Test]
    public async Task Paint_NoTitle_PlainRule()
    {
        string art = Art(6, null);
        await Assert.That(art).IsEqualTo(new string(PanelChrome.RuleGlyph, 6));
    }

    [Test]
    public async Task Paint_CenterTitle_SurroundedByRules()
    {
        string art = Art(12, "HI");
        await Assert.That(art.Contains("HI")).IsTrue();
        await Assert.That(art.StartsWith(PanelChrome.RuleGlyph.ToString())).IsTrue();
        await Assert.That(art.EndsWith(PanelChrome.RuleGlyph.ToString())).IsTrue();
    }

    [Test]
    public async Task Paint_LeftJustify_TitleNearStart()
    {
        string art = Art(12, "HI", RuleJustify.Left);
        await Assert.That(art.IndexOf("HI", StringComparison.Ordinal)).IsLessThan(4);
    }

    [Test]
    public async Task Paint_RightJustify_TitleNearEnd()
    {
        string art = Art(12, "HI", RuleJustify.Right);
        await Assert.That(art.IndexOf("HI", StringComparison.Ordinal)).IsGreaterThan(4);
    }

    [Test]
    public async Task Paint_LongTitle_EllipsisTruncates()
    {
        string art = Art(8, "0123456789ABCDEF");
        await Assert.That(art.Contains('…')).IsTrue();
        await Assert.That(art.Length).IsEqualTo(8);
    }

    [Test]
    public async Task TruncateTitle_ShortPassthrough()
    {
        await Assert.That(RuleView.TruncateTitle("ab", 5)).IsEqualTo("ab");
        await Assert.That(RuleView.TruncateTitle("abcdef", 3)).IsEqualTo("ab…");
        await Assert.That(RuleView.TruncateTitle("abcdef", 1)).IsEqualTo("…");
        await Assert.That(RuleView.TruncateTitle("abcdef", 0)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Paint_Degenerate_NoThrow()
    {
        var buffer = TestRender.Buffer(4, 1);
        RuleView.Paint(buffer, 0, 0, 0, "HI");
        await Assert.That(TestRender.Art(buffer).Length).IsGreaterThan(0);
    }
}
