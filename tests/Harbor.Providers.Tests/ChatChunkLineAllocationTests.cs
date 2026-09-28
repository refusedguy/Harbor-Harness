extern alias CompatWire;

using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using CompatOpenAiWire = CompatWire::Harbor.Providers.Internal.OpenAiWire;
using CompatChunkState = CompatWire::Harbor.Providers.Internal.ChunkStreamState;

namespace Harbor.Providers.Tests;

/// <summary>
/// Allocation-budget coverage for the SSE text-delta line path (#186).
/// <c>TryParseChatChunkLine</c> transcodes the already-owned SSE line into a
/// pooled buffer (single pass, no <c>JsonDocument</c>); the bound below is a
/// generous, CI-safe tripwire in the <c>SpanParserTests</c> tradition — a
/// DOM-per-chunk revert blows it, the span path stays comfortably inside.
/// The same <c>ChunkStreamState</c> is reused across warmup + measurement so
/// the index→id map settles once (text chunks never touch it).
/// </summary>
public class ChatChunkLineAllocationTests
{
    [Test]
    public async Task TryParseChatChunkLine_TextDelta_StaysBounded()
    {
        const string data = """{"choices":[{"delta":{"content":"hello"}}]}""";
        var state = new CompatChunkState();

        var first = CompatOpenAiWire.TryParseChatChunkLine(data, state, NullLogger.Instance);
        await Assert.That(first.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("hello");
        await Assert.That(state.MalformedChunks).IsEqualTo(0);

        for (int i = 0; i < 200; i++)
        {
            _ = CompatOpenAiWire.TryParseChatChunkLine(data, state, NullLogger.Instance);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int parses = 500;
        for (int i = 0; i < parses; i++)
        {
            _ = CompatOpenAiWire.TryParseChatChunkLine(data, state, NullLogger.Instance);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"sse-alloc: TryParseChatChunkLine avg = {(double)allocated / parses:F0} B over {parses} text chunks");
        await Assert.That(allocated).IsLessThanOrEqualTo(parses * 4L * 1_024L);
        await Assert.That(state.MalformedChunks).IsEqualTo(0);
    }
}
