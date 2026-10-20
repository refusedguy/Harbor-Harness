using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Cost gate over the <see cref="JsonlMessageCodec.SerializeMessagePayload" />
///     batch path (issue #1531, one slice of #97). The slice replaced the
///     per-message <c>Select(...).ToArray()</c> chains (two iterators + two
///     growable arrays each) and the <c>StopReason.ToString().ToLowerInvariant()</c>
///     call with pre-sized indexed loops and a constant switch, on the append
///     path of every turn.
///     <para>
///         THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
///         to be <c>t(2N) &lt;= 3*t(N)</c> and <c>alloc(2N) &lt;= 3*alloc(N)</c>
///         over 16 against 32 messages, decided by the shared TestScalingGate
///         (#1652). It is now:
///         <code>
///         bytes per serialized message &lt;= 220   (machine-independent, HARD)
///         probe units per message        &lt;= 6.0  (machine-scaled time, SOFT)
///         </code>
///         measured over a FIXED amount of work (1 000 000 messages, in
///         batches of 16) on the SAME runner in the SAME run. The allocation
///         branch is machine-independent and is asserted hard; the time branch
///         is in PROBE UNITS and stands down when the rounds do not reproduce
///         each other. The report prints on every run, pass or fail.
///         Linux-only (GC accounting varies across OS).
///     </para>
/// </summary>
[NotInParallel("jsonl-payload-scale")]
public class JsonlMessagePayloadScalingTests
{
    /// <summary>Messages in one serialized batch - the FIXED shape the gate drives.</summary>
    private const int MessagesPerBatch = 16;

    /// <summary>
    ///     Messages serialized per measured leg - the FIXED amount of work.
    ///     1 000 000 messages is 62 500 batches, about 44 ms of real work on
    ///     the CI runner.
    /// </summary>
    private const int MessagesPerLeg = 1_000_000;

    /// <summary>
    ///     Allocation budget for ONE serialized message, in bytes.
    ///     Machine-independent, so it is compared as measured, with no band.
    ///     Pinned from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 49 788 976 B over 21 387 batches of 16 messages =
    ///     145 B per message. The budget leaves ~1.51x headroom.
    /// </summary>
    private const long MaxBytesPerMessage = 220;

    /// <summary>
    ///     Time budget for ONE serialized message, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run: 14.973 ms over 342 192 messages = 0.044 us
    ///     per message against a probe that costs 0.000380 ms/unit, i.e. ~0.115
    ///     probe units per message.
    ///     The budget is wide on purpose: the probe's OWN measured cost moved
    ///     5.3x between two CI runs of the SAME tree (0.000380 ms/unit in run
    ///     39423151864, 0.000072 ms/unit in run 39436405496), so the
    ///     probe-unit currency is only as stable as the yardstick behind it.
    ///     The BYTE budget is the branch that can fail on any runner - it is
    ///     exact - and the time budget is the gross-regression detector.
    /// </summary>
    private const double MaxProbeUnitsPerMessage = 6.0;

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
                [new TextPart("hi"), new ThinkingPart("reason"), ToolCallPart.Create($"tc_{i}", "read", ArgsDoc.RootElement)],
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
    ///     One measured leg: serialize <paramref name="messages" /> payloads,
    ///     timed and allocation-counter'd as one window. The batch is a
    ///     pre-built array of <see cref="MessagesPerBatch" /> messages, so the
    ///     window is the serialization and nothing else.
    /// </summary>
    private static PerfGateLeg Measure(AgentMessage[] batch, int messages)
    {
        int batches = messages / MessagesPerBatch;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < batches; i++)
        {
            for (int m = 0; m < batch.Length; m++)
            {
                _ = JsonlMessageCodec.SerializeMessagePayload(batch[m]);
            }
        }

        sw.Stop();
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Test]
    [Timeout(300_000)]
    public async Task SerializeMessagePayload_CostPerMessage_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var batch = Batch(MessagesPerBatch);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: messages => Task.FromResult(Measure(batch, messages)),
            units: MessagesPerLeg,
            options: new PerfGateOptions
            {
                Label = "jsonl-payload-cost",
                MaxBytesPerUnit = MaxBytesPerMessage,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerMessage,
            });

        await result.AssertWithinBudgetsAsync("jsonl message payload serialization per message");
    }
}
