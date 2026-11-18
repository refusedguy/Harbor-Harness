using Harbor.Tui.CellForge.Streaming;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #2678 item 99: the buffer must never expose a Markdown construct whose
// closing marker has not streamed in yet. These tests assert the observable
// prefix, not the scanning internals.
public class MarkdownStreamBufferTests
{
    private static MarkdownStreamBuffer Buffer(params string[] deltas)
    {
        var buffer = new MarkdownStreamBuffer();
        foreach (string delta in deltas)
        {
            buffer.Append(delta);
        }

        return buffer;
    }

    [Test]
    public async Task PlainText_IsVisibleImmediately()
    {
        var buffer = Buffer("Hello ", "world");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("Hello world");
        await Assert.That(buffer.Length).IsEqualTo(11);
    }

    [Test]
    public async Task OpenFence_HidesFromTheFenceLine()
    {
        var buffer = Buffer("before\n", "```csharp\n", "code");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("before\n");
    }

    [Test]
    public async Task ClosedFence_ExposesEverything()
    {
        var buffer = Buffer("before\n", "```csharp\n", "code\n", "```\n", "after");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("before\n```csharp\ncode\n```\nafter");
    }

    [Test]
    public async Task PartialFenceAtTail_IsHeldBack()
    {
        var buffer = Buffer("text\n", "``");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("text\n");

        buffer.Append("`\ncode");
        await Assert.That(buffer.SafePrefix()).IsEqualTo("text\n");
    }

    [Test]
    public async Task TildeFence_IsRecognized()
    {
        var buffer = Buffer("a\n", "~~~\n", "x");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("a\n");
    }

    [Test]
    public async Task OpenBoldRun_HidesFromTheMarker()
    {
        var buffer = Buffer("this is **", "bold");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("this is ");

        buffer.Append("**.");
        await Assert.That(buffer.SafePrefix()).IsEqualTo("this is **bold**.");
    }

    [Test]
    public async Task OpenUnderscoreRun_HidesFromTheMarker()
    {
        var buffer = Buffer("a __b");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("a ");
    }

    [Test]
    public async Task EscapedBoldMarker_IsNotCounted()
    {
        var buffer = Buffer(@"literal \*\* and text");

        await Assert.That(buffer.SafePrefix()).IsEqualTo(@"literal \*\* and text");
    }

    [Test]
    public async Task OpenLink_HidesFromTheOpeningBracket()
    {
        var buffer = Buffer("see [label", "](https://exa");

        await Assert.That(buffer.SafePrefix()).IsEqualTo("see ");
        await Assert.That(buffer.Length).IsEqualTo("see [label](https://exa".Length);

        buffer.Append("mple.com) now");
        await Assert.That(buffer.SafePrefix()).IsEqualTo("see [label](https://example.com) now");
    }

    [Test]
    public async Task OpenFenceWinsOverLaterOpenBold()
    {
        var buffer = Buffer("```\n", "**bold");

        await Assert.That(buffer.SafePrefix()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Flush_ReturnsEverythingAndClears()
    {
        var buffer = Buffer("text\n", "```\n", "code");

        await Assert.That(buffer.Flush()).IsEqualTo("text\n```\ncode");
        await Assert.That(buffer.SafePrefix()).IsEqualTo(string.Empty);
        await Assert.That(buffer.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_DiscardsEverything()
    {
        var buffer = Buffer("text", "**bold");

        buffer.Clear();

        await Assert.That(buffer.SafePrefix()).IsEqualTo(string.Empty);
        await Assert.That(buffer.Length).IsEqualTo(0);
    }
}
