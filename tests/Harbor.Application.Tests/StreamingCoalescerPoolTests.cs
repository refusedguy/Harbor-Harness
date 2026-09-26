using Harbor.Application.Agents;

namespace Harbor.Application.Tests;

/// <summary>
///     Regression tests for the pooled-buffer ownership fixes audited in #53:
///     repeated <c>StartToolCall</c> for one id must not leak the previously
///     rented args builder, and flushed/materialized strings must be
///     independent copies (safe to use after the builder is returned).
/// </summary>
public class StreamingCoalescerPoolTests
{
    [Test]
    public async Task StartToolCall_DuplicateId_MaterializesLatestOnly()
    {
        using var coalescer = new StreamingCoalescer();
        coalescer.StartToolCall("a", "first");
        coalescer.AppendToolCallDelta("a", """{"x":1}""");
        // Repeated start for the same id: old builder is returned to the pool
        // (no leak), last-start-wins.
        coalescer.StartToolCall("a", "second");
        coalescer.AppendToolCallDelta("a", """{"y":2}""");

        var calls = coalescer.MaterializeToolCalls();

        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0].ToolName).IsEqualTo("second");
        await Assert.That(calls[0].Args.GetProperty("y").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task FlushText_ReturnsCopy_SubsequentAppendsDoNotMutateIt()
    {
        using var coalescer = new StreamingCoalescer();
        coalescer.AppendTextDelta("hello");
        string flushed = coalescer.FlushText();
        coalescer.AppendTextDelta(" world");
        string second = coalescer.FlushText();

        await Assert.That(flushed).IsEqualTo("hello");
        await Assert.That(second).IsEqualTo(" world");
    }

    [Test]
    public async Task Materialize_ThenDispose_DoesNotThrow_SecondMaterializeEmpty()
    {
        var coalescer = new StreamingCoalescer();
        coalescer.StartToolCall("a", "read");
        coalescer.AppendToolCallDelta("a", """{"path":"x"}""");

        var calls = coalescer.MaterializeToolCalls();
        var again = coalescer.MaterializeToolCalls();
        coalescer.Dispose();

        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(again.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Materialize_EmptyArgs_UsesEmptyObject()
    {
        using var coalescer = new StreamingCoalescer();
        coalescer.StartToolCall("a", "read");

        var calls = coalescer.MaterializeToolCalls();

        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0].Args.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Object);
    }
}
