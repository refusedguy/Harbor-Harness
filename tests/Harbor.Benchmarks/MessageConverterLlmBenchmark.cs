using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Sessions;

namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref="MessageConverter.ToLlmMessages"/> — the Adapter (GOF) that
///     converts domain <see cref="AgentMessage"/>s to provider-agnostic <see cref="LlmMessage"/>s.
///     Called every turn in <c>AgentLoop.cs:235</c> to build the <see cref="LlmRequest.Messages"/>
///     payload before each <c>ILlmClient.StreamAsync</c> call.
/// </summary>
/// <remarks>
///     Distinct from <see cref="MessageConverterBenchmark"/> which benchmarks the JSONL
///     persistence codec (<c>JsonlMessageCodec</c> via <c>JsonlSessionStore</c>).
///     This benchmark isolates the in-memory domain-to-LLM adapter:
///     <list type="bullet">
///         <item><see cref="UserMessage"/> → <see cref="LlmUserMessage"/> (single <see cref="LlmTextBlock"/>)</item>
///         <item><see cref="AssistantMessage"/> → <see cref="LlmAssistantMessage"/> via <c>ConvertParts</c> (Text/Thinking/ToolCall)</item>
///         <item><see cref="ToolResultMessage"/> → N × <see cref="LlmToolResultMessage"/> (one per <see cref="ToolResultEntry"/>)</item>
///     </list>
///     The <c>ConvertParts</c> fan-out (per-part type switch + <see cref="LlmContentBlock"/> allocation)
///     is included in the assistant-message cost; the <c>StopReason</c> → wire-string lowering
///     is also on the hot path.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class MessageConverterLlmBenchmark
{
    private static readonly JsonElement SampleArgs =
        JsonDocument.Parse("""{"path":"src/Harbor.Core/AgentLoop.cs","limit":40}""").RootElement.Clone();

    private MessageConverter _converter = null!;
    private IReadOnlyList<AgentMessage> _messages = null!;

    /// <summary>
    ///     Number of domain <see cref="AgentMessage"/>s fed to <see cref="MessageConverter.ToLlmMessages"/>.
    ///     1  ≈ single-turn prompt, 10 ≈ short session, 100 ≈ long session approaching compaction.
    /// </summary>
    [Params(1, 10, 100)]
    public int MessageCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _converter = new MessageConverter();
        _messages = BuildMessages(MessageCount);
    }

    /// <summary>
    ///     Adapter cost for <see cref="MessageConverter.ToLlmMessages"/> at varying history lengths.
    ///     Measures capacity pre-pass + per-message switch + <c>ConvertParts</c> fan-out.
    /// </summary>
    [Benchmark(Description = "ToLlmMessages (N AgentMessages → LlmMessages)")]
    public IReadOnlyList<LlmMessage> ToLlmMessages() => _converter.ToLlmMessages(_messages);

    private static IReadOnlyList<AgentMessage> BuildMessages(int count)
    {
        const string sessionId = "bench-session";
        var now = DateTimeOffset.UtcNow;
        var list = new List<AgentMessage>(count);

        // Mix mirrors a realistic session: user → assistant (text + thinking + tool_call) → tool_result
        // cycled to fill exactly 'count' messages. Keeps allocations stable across iterations.
        for (int i = 0; i < count; i++)
        {
            int mod = i % 3;
            string id = $"m{i:D4}";
            var ts = now.AddSeconds(i);

            if (mod == 0)
            {
                list.Add(new UserMessage(
                    id,
                    sessionId,
                    ts,
                    $"Implement feature {i}: add unit tests for MessageConverter adapter.",
                    "code",
                    "test-model"));
            }
            else if (mod == 1)
            {
                // 3 parts exercises ConvertParts: TextPart + ThinkingPart + ToolCallPart
                list.Add(new AssistantMessage(
                    id,
                    sessionId,
                    ts,
                    new ContentPart[]
                    {
                        new TextPart($"I'll implement feature {i} and add tests."),
                        new ThinkingPart($"Reasoning for turn {i}: check edge cases around empty ToolResultMessage."),
                        new ToolCallPart($"tc_{i}", "read", SampleArgs)
                    },
                    StopReason.ToolUse,
                    new Usage(120, 40),
                    "test-model"));
            }
            else
            {
                list.Add(new ToolResultMessage(
                    id,
                    sessionId,
                    ts,
                    new[]
                    {
                        new ToolResultEntry($"tc_{i - 1}", "read", $"Contents of file {i - 1} (512 bytes)", false)
                    }));
            }
        }

        return list;
    }
}
