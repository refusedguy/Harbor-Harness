using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Ansi;
using Harbor.Tui.Plain;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tui.E2E.Tests;
/// <summary>
///     End-to-end renderer tests: feed a realistic event stream to each concrete
///     renderer (ANSI, Plain) and assert that the rendered output contains the
///     streamed text. These tests guard against silent regressions where a
///     renderer's switch statement is updated to skip a case (or returns early)
///     and the user sees nothing on the screen.
/// </summary>
public class RendererE2ETests
{
    /// <summary>
    ///     Build the canonical event stream used by the renderer e2e tests:
    ///     AgentStart → MessageStart → TextDelta("Hello") → MessageEnd → AgentEnd.
    /// </summary>
    private static IEnumerable<AgentEvent> BuildHelloEventStream()
    {
        var partial = AssistantMessage.Empty("s1", "stub-1");
        yield return new AgentStartEvent("s1", Array.Empty<AgentMessage>());
        yield return new MessageStartEvent(partial);
        yield return new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial);
        yield return new MessageEndEvent(partial);
        yield return new AgentEndEvent(Array.Empty<AgentMessage>());
    }

    [Test]
    public async Task AnsiTuiRenderer_RenderAsync_ProducesOutput()
    {
        // AnsiTuiRenderer writes through AnsiRenderContext, which calls
        // Console.Write / Console.WriteLine directly. Redirect Console.Out
        // to a StringWriter so we can assert on the captured bytes.
        //
        // Note: TUnit logs through Console.Out, so we restore the original
        // writer in the finally block to keep test output readable on failure.
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var originalOut = Console.Out;
        Console.SetOut(writer);

        AnsiTuiRenderer? renderer = null;
        try
        {
            renderer = new AnsiTuiRenderer(NullLogger<AnsiTuiRenderer>.Instance);
            await renderer.InitializeAsync();

            foreach (var evt in BuildHelloEventStream())
            {
                await renderer.RenderAsync(evt);
            }

            await writer.FlushAsync();
            string output = sb.ToString();

            // The "[assistant] " prefix is emitted on MessageStartEvent; the
            // delta text "Hello" is emitted on MessageUpdateEvent. Both should
            // appear in the captured output for the stream to be considered
            // visible to the user.
            await Assert.That(output).Contains("Hello");
            await Assert.That(output).Contains("[assistant]");
        }
        finally
        {
            Console.SetOut(originalOut);
            renderer?.Dispose();
        }
    }

    [Test]
    public async Task PlainTuiRenderer_RenderAsync_ProducesOutput()
    {
        // PlainTuiRenderer accepts a TextWriter in its constructor, so we can
        // pass a StringWriter directly — no Console redirection needed.
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);

        var renderer = new PlainTuiRenderer(writer);
        await renderer.InitializeAsync();

        foreach (var evt in BuildHelloEventStream())
        {
            await renderer.RenderAsync(evt);
        }

        await writer.FlushAsync();
        string output = sb.ToString();

        await Assert.That(output).Contains("Hello");
        await Assert.That(output).Contains("[assistant]");
        renderer.Dispose();
    }
}
