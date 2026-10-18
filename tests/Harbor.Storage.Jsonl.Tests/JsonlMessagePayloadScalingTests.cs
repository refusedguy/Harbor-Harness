using Harbor.Abstractions.Models;
using Harbor.TestKit;
namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Relative scaling gate over the <see cref="JsonlMessageCodec.SerializeMessagePayload" />
///     batch path (issue #1531, one slice of #97). The slice replaced the
///     per-message <c>Select(...).ToArray()</c> chains (two iterators + two
///     growable arrays each) and the <c>StopReason.ToString().ToLowerInvariant()</c>
///     call with pre-sized indexed loops and a constant switch, on the append
///     path of every turn.
///     <para>
///         The gate is RELATIVE, not absolute (#410 / #1373 shape): serializing
///         twice the messages must cost at most 3x the time and allocations of
///         the single-size batch, measured in the same run
///         (<c>t(2N) &lt;= 3*t(N)</c>, <c>alloc(2N) &lt;= 3*alloc(N)</c>). A
///         per-message quadratic/additive regression lands above the bound and
///         cannot hide under it; runner speed cancels in the ratio. Measured
///         through the shared <see cref="TestScalingGate" /> (#1652): GC-fenced
///         legs scaled toward ~100 ms of real work, medians over five rounds,
///         and a x1.05 noise band on the time branch only. Absolute columns
///         print for information; only the ratios are gated.
///         Linux-only (GC accounting varies across OS).
///     </para>
/// </summary>
[NotInParallel("jsonl-payload-scale")]
public class JsonlMessagePayloadScalingTests
{
    /// <summary>Messages in the small batch; the large one doubles it.</summary>
    private const int SmallMessages = 16;

    /// <summary>Batches serialized per measured leg's unit (one batch is sub-millisecond).</summary>
    private const int Iterations = 30;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{"path":"a.txt"}""");
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     An alternating user/assistant/tool_result stream: the assistant
    ///     legs exercise the parts loop + <c>StopReasonLower</c>, the
    ///     tool_result legs the entry loop.
    /// </summary>
    private static AgentMessage Message(int i)
    {
        int index = i % 3;
        return index switch
        {
            0 => new UserMessage($"u{i}", "sess", CreatedAt, "content", "code", "p/m"),
            1 => new AssistantMessage(
                $"a{i}", "sess", CreatedAt,
                [new TextPart("hi"), new ThinkingPart("reason"), new ToolCallPart.Create($"tc_{i}", "read", ArgsDoc.RootElement)],
                StopReason.ToolUse, new Usage(10, 5), "p/m"),
            _ => new ToolResultMessage(
                $"r{i}", "sess", CreatedAt,
                [
                    new ToolResultEntry($"tc_{i - 1}", "read", "output", IsError: false),
                    new ToolResultEntry("tc_early", "bash", "error", IsError: true),
                ]),
        };
    }

    private static AgentMessage[] Batch(int messages)
    {
        var batch = new AgentMessage[messages];
        for (int i = 0; i < messages; i++)
        {
            batch[i] = Message(i);
        }

        return batch;
    }

    /// <summary>
    ///     One measured leg: serialize <paramref name="units" /> batches of
    ///     <paramref name="messages" /> payloads, timed and allocation-counter'd
    ///     as one window.
    /// </summary>
    private static (double Ms, long Allocated) Measure(AgentMessage[] batch, int units)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < units; i++)
        {
            for (int m = 0; m < batch.Length; m++)
            {
                _ = JsonlMessageCodec.SerializeMessagePayload(batch[m]);
            }
        }

        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static TestScalingGateLeg AsLeg((double Ms, long Allocated) leg) => new(leg.Ms, leg.Allocated);

    [Test]
    [Timeout(120_000)]
    public async Task SerializeMessagePayload_DoubledMessages_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var small = Batch(SmallMessages);
        var large = Batch(SmallMessages * 2);

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(AsLeg(Measure(small, units))),
            measureLargeLeg: units => Task.FromResult(AsLeg(Measure(large, units))),
            initialUnits: Iterations,
            options: new TestScalingGateOptions
            {
                Label = "jsonl-payload-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "serialize(2N)/serialize(N)",
            linearWhy: "Linear is 2.00, so this bound separates linear from quadratic without depending on how fast this runner is.");
    }
}
