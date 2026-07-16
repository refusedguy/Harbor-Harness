using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Benchmarks;

/// <summary>
/// Benchmarks <see cref="JsonlSessionStore"/> hot paths:
/// - <see cref="JsonlSessionStore.AppendMessageAsync"/>: append-only write,
///   serializes one message to JSON and appends a line to the .jsonl file.
/// - <see cref="JsonlSessionStore.GetMessagesAsync"/>: reads the entire
///   .jsonl file, parses each line, dedupes by message id, sorts by timestamp.
///
/// Each iteration uses a fresh temp directory to avoid unbounded file growth
/// contaminating later iterations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class JsonlSessionStoreBenchmark
{
    private string _rootDirectory = null!;
    private JsonlSessionStore _store = null!;
    private string _sessionId = null!;
    private Session _session = null!;
    private UserMessage _userMessage = null!;
    private AssistantMessage _assistantMessage = null!;

    [Params(10, 100)]
    public int MessageCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "harbor-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
        _store = new JsonlSessionStore(_rootDirectory, NullLogger<JsonlSessionStore>.Instance);

        var createResult = _store.CreateAsync("/tmp", "code", "stub", "stub-1").GetAwaiter().GetResult();
        _session = createResult.IsSuccess ? createResult.Value : throw new InvalidOperationException("create failed");
        _sessionId = _session.Id;

        var now = DateTimeOffset.UtcNow;
        _userMessage = new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: _sessionId,
            CreatedAt: now,
            Content: "Write a C# function that reverses a string.",
            Agent: "code",
            Model: "stub-1");

        _assistantMessage = new AssistantMessage(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: _sessionId,
            CreatedAt: now.AddSeconds(1),
            Parts: new ContentPart[]
            {
                new TextPart("Here is a function that reverses a string:\n\n```csharp\nstring Reverse(string s) => new string(s.Reverse().ToArray());\n```"),
            },
            StopReason: StopReason.Stop,
            Usage: new Usage(120, 35),
            Model: "stub-1");
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        try
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; temp dir may linger if a file handle is held.
        }
    }

    [Benchmark(Description = "AppendMessageAsync (N writes)")]
    public async Task AppendMessageAsync_N()
    {
        for (var i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Benchmark(Description = "AppendMessageAsync (interleaved user+assistant)")]
    public async Task AppendMessageAsync_Interleaved()
    {
        for (var i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage, CancellationToken.None).ConfigureAwait(false);
            await _store.AppendMessageAsync(_sessionId, _assistantMessage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Benchmark(Description = "GetMessagesAsync (after N writes)")]
    public async Task GetMessagesAsync_AfterN()
    {
        // Seed the session with N messages first.
        for (var i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage, CancellationToken.None).ConfigureAwait(false);
        }

        var result = await _store.GetMessagesAsync(_sessionId, CancellationToken.None).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error);
        }
    }
}
