using Harbor.Tui.Spectre.Fullscreen.Helpers;
using Harbor.Tui.Spectre.Fullscreen;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="FullscreenTuiRenderer" /> — verifies the pure helpers (word wrap,
///     markdown formatting) and the stateful scroll / input-history navigation logic.
/// </summary>
public class FullscreenRendererTests
{
    private static FullscreenTuiRenderer CreateRenderer() =>
        new(NullLogger<FullscreenTuiRenderer>.Instance);

    // ═══════════════════════════════════════════════════════════════
    //  WordWrap
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task WordWrap_EmptyString_ReturnsSingleEmptyLine()
    {
        var lines = MarkdownRenderer.WordWrap(string.Empty, 80);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task WordWrap_ShortText_ReturnsSingleLine()
    {
        var lines = MarkdownRenderer.WordWrap("Hello, World!", 80);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("Hello, World!");
    }

    [Test]
    public async Task WordWrap_LongText_BreaksAtWordBoundaries()
    {
        string text = "The quick brown fox jumps over the lazy dog";
        var lines = MarkdownRenderer.WordWrap(text, 20);

        await Assert.That(lines.Count).IsGreaterThan(1);
        // No line should exceed the maxWidth.
        foreach (var line in lines)
            await Assert.That(line.Length).IsLessThanOrEqualTo(20);
        // Re-joining with spaces should yield the original text (modulo whitespace).
        await Assert.That(string.Join(' ', lines)).IsEqualTo(text);
    }

    [Test]
    public async Task WordWrap_SingleVeryLongWord_FallsBackToCharWrap()
    {
        string text = "abcdefghijklmnopqrstuvwxyz";
        var lines = MarkdownRenderer.WordWrap(text, 10);

        await Assert.That(lines.Count).IsGreaterThan(1);
        foreach (var line in lines)
            await Assert.That(line.Length).IsLessThanOrEqualTo(10);
    }

    [Test]
    public async Task WordWrap_MultiLineText_PreservesBlankLines()
    {
        string text = "line1\n\nline3";
        var lines = MarkdownRenderer.WordWrap(text, 80);

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[0]).IsEqualTo("line1");
        await Assert.That(lines[1]).IsEqualTo(string.Empty);
        await Assert.That(lines[2]).IsEqualTo("line3");
    }

    // ═══════════════════════════════════════════════════════════════
    //  FormatInlineMarkdown
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task FormatInlineMarkdown_BoldBecomesSpectreMarkup()
    {
        string result = MarkdownRenderer.FormatInline("**bold**");

        await Assert.That(result).Contains("[bold white]");
        await Assert.That(result).Contains("bold");
    }

    [Test]
    public async Task FormatInlineMarkdown_ItalicBecomesSpectreMarkup()
    {
        string result = MarkdownRenderer.FormatInline("*italic*");

        await Assert.That(result).Contains("[italic]");
        await Assert.That(result).Contains("italic");
    }

    [Test]
    public async Task FormatInlineMarkdown_CodeBecomesSpectreMarkup()
    {
        string result = MarkdownRenderer.FormatInline("`code`");

        await Assert.That(result).Contains("[yellow]");
        await Assert.That(result).Contains("code");
    }

    [Test]
    public async Task FormatInlineMarkdown_LinkHidesUrl()
    {
        string result = MarkdownRenderer.FormatInline("[Harbor](https://example.com)");

        await Assert.That(result).Contains("Harbor");
        await Assert.That(result.Contains("https://example.com")).IsFalse();
    }

    [Test]
    public async Task FormatInlineMarkdown_EscapesSpectreMarkup()
    {
        // Unbalanced [ and ] in source text must be escaped so Spectre.Console
        // does not interpret them as markup tags.
        string result = MarkdownRenderer.FormatInline("[not a tag]");

        await Assert.That(result).Contains("not a tag");
        // After escaping, the leading [ becomes "[[" in Spectre markup.
        await Assert.That(result).Contains("[[");
    }

    // ═══════════════════════════════════════════════════════════════
    //  FormatMarkdownToList
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task FormatMarkdownToList_CodeBlock_BecomesBorderedBox()
    {
        string markdown = "```csharp\nConsole.WriteLine(\"hi\");\n```\n";
        var lines = MarkdownRenderer.FormatToList(markdown, 80);

        bool hasStartFence = lines.Any(l => l.Contains("csharp") && l.Contains("┌"));
        bool hasEndFence = lines.Any(l => l.Contains("└"));
        await Assert.That(hasStartFence).IsTrue();
        await Assert.That(hasEndFence).IsTrue();
        bool hasCode = lines.Any(l => l.Contains("Console.WriteLine"));
        await Assert.That(hasCode).IsTrue();
    }

    [Test]
    public async Task FormatMarkdownToList_Headers_BecomeBoldWhite()
    {
        string markdown = "# H1\n## H2\n### H3\n";
        var lines = MarkdownRenderer.FormatToList(markdown, 80);

        await Assert.That(lines.Any(l => l.Contains("H1") && l.Contains("[bold white]"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("H2") && l.Contains("[bold white]"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("H3") && l.Contains("[bold white]"))).IsTrue();
    }

    [Test]
    public async Task FormatMarkdownToList_UnorderedListItems_BecomeBulletLines()
    {
        string markdown = "- alpha\n- beta\n- gamma\n";
        var lines = MarkdownRenderer.FormatToList(markdown, 80);

        await Assert.That(lines.Any(l => l.Contains("alpha"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("beta"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("gamma"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("•"))).IsTrue();
    }

    [Test]
    public async Task FormatMarkdownToList_OrderedListItems_KeepNumbers()
    {
        string markdown = "1. first\n2. second\n3. third\n";
        var lines = MarkdownRenderer.FormatToList(markdown, 80);

        await Assert.That(lines.Any(l => l.Contains("1.") && l.Contains("first"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("2.") && l.Contains("second"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("3.") && l.Contains("third"))).IsTrue();
    }

    [Test]
    public async Task FormatMarkdownToList_BlankLine_BecomesEmptyOutputLine()
    {
        string markdown = "para1\n\npara2";
        var lines = MarkdownRenderer.FormatToList(markdown, 80);

        await Assert.That(lines.Any(string.IsNullOrEmpty)).IsTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scroll logic
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Scroll_Initial_OffsetIsZero_NotScrolling()
    {
        var renderer = CreateRenderer();

        await Assert.That(renderer.TestScrollOffset).IsEqualTo(0);
        await Assert.That(renderer.TestIsScrolling).IsFalse();
    }

    [Test]
    public async Task ScrollUp_WithNoLines_StaysAtZero()
    {
        var renderer = CreateRenderer();

        renderer.TestScrollUp(5);

        // No chat lines → maxScroll = 0 → ScrollUp is a no-op.
        await Assert.That(renderer.TestScrollOffset).IsEqualTo(0);
        await Assert.That(renderer.TestIsScrolling).IsFalse();
    }

    [Test]
    public async Task ScrollToTop_WithNoLines_StaysAtZero()
    {
        var renderer = CreateRenderer();

        renderer.TestScrollToTop();

        await Assert.That(renderer.TestScrollOffset).IsEqualTo(0);
        await Assert.That(renderer.TestIsScrolling).IsFalse();
    }

    [Test]
    public async Task ScrollToBottom_AlwaysResetsOffsetAndScrollingFlag()
    {
        var renderer = CreateRenderer();

        // Even if we somehow had a non-zero offset (we don't without lines),
        // ScrollToBottom must reset everything.
        renderer.TestScrollToBottom();

        await Assert.That(renderer.TestScrollOffset).IsEqualTo(0);
        await Assert.That(renderer.TestIsScrolling).IsFalse();
    }

    [Test]
    public async Task ScrollDown_BelowZero_ClampsToZero()
    {
        var renderer = CreateRenderer();

        // Calling ScrollDown when already at offset 0 must not underflow.
        renderer.TestScrollDown(10);

        await Assert.That(renderer.TestScrollOffset).IsEqualTo(0);
        await Assert.That(renderer.TestIsScrolling).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Input history navigation
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task History_Initially_NoHistoryAndNotNavigating()
    {
        var renderer = CreateRenderer();

        await Assert.That(renderer.TestInputHistoryCount).IsEqualTo(0);
        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(-1);
        await Assert.That(renderer.TestInputBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task History_Push_AddsEntryAndResetsIndex()
    {
        var renderer = CreateRenderer();
        renderer.TestPushInputHistory("first");

        await Assert.That(renderer.TestInputHistoryCount).IsEqualTo(1);
        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task History_Up_FromIdle_LoadsLastEntry()
    {
        var renderer = CreateRenderer();
        renderer.TestPushInputHistory("first");
        renderer.TestPushInputHistory("second");

        renderer.TestNavigateHistoryUp();

        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(1);
        await Assert.That(renderer.TestInputBuffer).IsEqualTo("second");
    }

    [Test]
    public async Task History_Up_Twice_WalksBackwards()
    {
        var renderer = CreateRenderer();
        renderer.TestPushInputHistory("first");
        renderer.TestPushInputHistory("second");
        renderer.TestPushInputHistory("third");

        renderer.TestNavigateHistoryUp();
        await Assert.That(renderer.TestInputBuffer).IsEqualTo("third");
        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(2);

        renderer.TestNavigateHistoryUp();
        await Assert.That(renderer.TestInputBuffer).IsEqualTo("second");
        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(1);

        renderer.TestNavigateHistoryUp();
        await Assert.That(renderer.TestInputBuffer).IsEqualTo("first");
        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(0);
    }

    [Test]
    public async Task History_Up_AtOldest_StaysAtOldest()
    {
        var renderer = CreateRenderer();
        renderer.TestPushInputHistory("only");

        renderer.TestNavigateHistoryUp();
        renderer.TestNavigateHistoryUp();  // already at oldest, no-op

        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(0);
        await Assert.That(renderer.TestInputBuffer).IsEqualTo("only");
    }

    [Test]
    public async Task History_Down_FromNavigate_ReturnsToEmpty()
    {
        var renderer = CreateRenderer();
        renderer.TestPushInputHistory("first");
        renderer.TestPushInputHistory("second");

        renderer.TestNavigateHistoryUp();      // index=1, buffer="second"
        renderer.TestNavigateHistoryDown();    // index=-1, buffer=""

        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(-1);
        await Assert.That(renderer.TestInputBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task History_Down_FromMiddle_MovesForward()
    {
        var renderer = CreateRenderer();
        renderer.TestPushInputHistory("first");
        renderer.TestPushInputHistory("second");
        renderer.TestPushInputHistory("third");

        renderer.TestNavigateHistoryUp();  // third
        renderer.TestNavigateHistoryUp();  // second
        renderer.TestNavigateHistoryDown(); // third again

        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(2);
        await Assert.That(renderer.TestInputBuffer).IsEqualTo("third");
    }

    [Test]
    public async Task History_Up_WithNoHistory_IsNoOp()
    {
        var renderer = CreateRenderer();

        renderer.TestNavigateHistoryUp();

        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(-1);
        await Assert.That(renderer.TestInputBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task History_Down_WhenNotNavigating_IsNoOp()
    {
        var renderer = CreateRenderer();
        renderer.TestPushInputHistory("first");

        renderer.TestNavigateHistoryDown();  // _historyIndex == -1 → no-op

        await Assert.That(renderer.TestHistoryIndex).IsEqualTo(-1);
        await Assert.That(renderer.TestInputBuffer).IsEqualTo(string.Empty);
    }
}
