using Harbor.Tui.Spectre.Fullscreen;

namespace Harbor.Tui.Tests;

/// <summary>ENG5 (issue #276): Spectre markup parse memoization.</summary>
public class MarkupCacheTests
{
    [Test]
    public async Task Same_Source_Returns_Shared_Instance()
    {
        var first = MarkupCache.GetOrParse("[bold]hello[/]");
        var second = MarkupCache.GetOrParse("[bold]hello[/]");

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task Different_Sources_Parse_Independently()
    {
        var plain = MarkupCache.GetOrParse("just text");
        var styled = MarkupCache.GetOrParse("[red]just text[/]");

        await Assert.That(ReferenceEquals(plain, styled)).IsFalse();
    }

    [Test]
    public async Task Escaped_Text_Round_Trips()
    {
        string source = Spectre.Console.Markup.Escape("a [bracket] c");
        var parsed = MarkupCache.GetOrParse(source);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(ReferenceEquals(parsed, MarkupCache.GetOrParse(source))).IsTrue();
    }
}
