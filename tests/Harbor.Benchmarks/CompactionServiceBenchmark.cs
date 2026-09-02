using System.Runtime.CompilerServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="CompactionService" /> — invoked at the start of every agent turn to
///     decide whether the session history must be summarized/pruned before the next LLM call.
///     Because it runs potentially every turn, the cost of its NON-LLM work (the "should we
///     compact?" decision and the summarization prompt assembly) matters.
///     <para>
///         The LLM summarization round-trip is replaced by <see cref="StubLlmClient" />, which
///         yields a canned <see cref="TextDeltaEvent" /> instantly, so the benchmarks isolate the
///         orchestration + message-assembly cost rather than network latency. Message history sizes
///         are scaled via <c>MessageCount</c> (10 / 100 / 1000) against a realistic token window.
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class CompactionServiceBenchmark
{
    private IReadOnlyList<AgentMessage> _messages = null!;
    private ModelInfo _model = null!;
    private IProviderRegistry _registry = null!;
    private CompactionService _service = null!;

    /// <summary>
    ///     Number of messages in the synthetic session history. Scales the
    ///     decision scan and prompt-assembly cost linearly.
    /// </summary>
    [Params(10, 100, 1000)]
    public int MessageCount { get; set; }

    /// <summary>
    ///     The model context window (tokens). Combined with <see cref="MessageCount" /> this
    ///     determines whether <see cref="CompactionService.ShouldCompact" /> returns true.
    /// </summary>
    [Params(200_000)]
    public int ContextWindow { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _model = new ModelInfo(
            "stub-1",
            "stub",
            "Stub Model",
            ContextWindow,
            4096,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai");

        var providerId = ProviderId.Create("stub");
        _registry = new StubProviderRegistry(providerId, new StubSummarizingLlmClient(providerId));

        _service = new CompactionService(
            new TokenTracker(),
            _registry,
            NullLogger<CompactionService>.Instance);

        _messages = BuildMessages(MessageCount);
    }

    /// <summary>
    ///     Benchmarks the "should we compact?" decision — a full token scan of the history.
    ///     This runs every turn, so it must stay cheap even at 1000 messages.
    /// </summary>
    [Benchmark(Description = "ShouldCompact (full token scan)")]
    public bool ShouldCompact()
    {
        bool result = false;
        for (int i = 0; i < 100; i++)
            result = _service.ShouldCompact(_messages, _model);
        return result;
    }

    /// <summary>
    ///     Benchmarks a full compaction orchestration with the stub client, isolating the
    ///     in-process cost: cut-point selection, summarization-prompt assembly over the head,
    ///     summary token accounting, and summary-message construction.
    /// </summary>
    [Benchmark(Description = "CompactAsync (stub LLM)")]
    public async Task<Result<CompactionResult>> CompactAsync_Stub()
    {
        var result = await _service.CompactAsync("session-1", _messages, _model, CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    private static IReadOnlyList<AgentMessage> BuildMessages(int count)
    {
        var list = new List<AgentMessage>(count);
        var now = DateTimeOffset.UtcNow;
        string sessionId = "session-1";
        var emptyArgs = JsonDocument.Parse("{}").RootElement.Clone();

        for (int i = 0; i < count; i++)
        {
            // Interleave user / assistant(tool call) / tool result / assistant to mirror a
            // realistic coding session with tool-heavy turns.
            list.Add(new UserMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                now.AddSeconds(i),
                $"Read the file at path/to/module_{i % 50}.cs and refactor the ProcessAsync method to be async.",
                "code",
                "stub-1"));

            list.Add(new AssistantMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                now.AddSeconds(i).AddMilliseconds(1),
                new ContentPart[]
                {
                    new TextPart($"I'll read the file and inspect its contents before refactoring. (turn {i})"),
                    new ToolCallPart($"tc_{i}", "read", emptyArgs)
                },
                StopReason.ToolUse,
                new Usage(120, 35),
                "stub-1"));

            list.Add(new ToolResultMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                now.AddSeconds(i).AddMilliseconds(2),
                new[]
                {
                    new ToolResultEntry(
                        $"tc_{i}",
                        "read",
                        $"namespace Sample;\npublic class Module{i} {{\n  public void ProcessAsync() {{ /* body of module {i} */ }}\n}}",
                        false)
                }));

            list.Add(new AssistantMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                now.AddSeconds(i).AddMilliseconds(3),
                new ContentPart[] { new TextPart($"Refactored Module{i}.ProcessAsync to return Task and await internally. (turn {i})") },
                StopReason.Stop,
                new Usage(200, 60),
                "stub-1"));
        }

        return list;
    }
}

/// <summary>
///     Minimal stub provider registry that returns a single canned <see cref="ILlmClient" />.
///     Avoids the real provider wiring so the benchmark isolates compaction cost only.
/// </summary>
internal sealed class StubProviderRegistry : IProviderRegistry
{
    private readonly ILlmClient _client;
    private readonly ProviderId _providerId;

    public StubProviderRegistry(ProviderId providerId, ILlmClient client)
    {
        _providerId = providerId;
        _client = client;
    }

    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => new[] { _providerId };

    public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Success(_client);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));

    public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

    public Result Unregister(ProviderId providerId) => Result.Success();
}

/// <summary>
///     Minimal stub LLM client for benchmarking compaction without network I/O. Returns a
///     canned summary synchronously as a single <see cref="TextDeltaEvent" />.
/// </summary>
internal sealed class StubSummarizingLlmClient : ILlmClient
{
    public StubSummarizingLlmClient(ProviderId providerId)
    {
        ProviderId = providerId;
    }

    public ProviderId ProviderId { get; }

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new TextDeltaEvent("0", "Compacted summary of the conversation so far.");
        await Task.Yield();
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));
}
