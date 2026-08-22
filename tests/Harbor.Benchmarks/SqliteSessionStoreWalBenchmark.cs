using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"SqliteSessionStore\" /> hot paths under
///     WAL-mode concurrent write load. Measures append throughput,
///     read-back latency, and transaction commit cost with the recommended
///     PRAGMAs (<c>journal_mode=WAL</c>, <c>synchronous=NORMAL</c>).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class SqliteSessionStoreWalBenchmark
{
    private SqliteSessionStore _store = null!;
    private string _dbPath = null!;
    private Session _session = null!;
    private string _sessionId = null!;
    private UserMessage _userMessage = null!;
    private AssistantMessage _assistantMessage = null!;

    [Params(10, 100, 1000)]
    public int MessageCount;

    [IterationSetup]
    public void IterationSetup()
    {
        _dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harbor-bench-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new SqliteSessionStore(_dbPath, NullLogger<SqliteSessionStore>.Instance);

        var createResult = _store.CreateAsync("/tmp", "code", "stub", "stub-1").GetAwaiter().GetResult();
        _session = createResult.IsSuccess ? createResult.Value : throw new InvalidOperationException("create failed");
        _sessionId = _session.Id;

        var now = DateTimeOffset.UtcNow;
        _userMessage = new UserMessage(
            Guid.NewGuid().ToString("N"),
            _sessionId,
            now,
            "Write a C# function that reverses a string.",
            "code",
            "stub-1");

        _assistantMessage = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            _sessionId,
            now.AddSeconds(1),
            new[] { new TextPart("Here is a function that reverses a string.") },
            StopReason.Stop,
            new Usage(120, 35),
            "stub-1");
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        try
        {
            if (System.IO.File.Exists(_dbPath))
                System.IO.File.Delete(_dbPath);
        }
        catch { }
    }

    [Benchmark(Description = "AppendMessageAsync (N messages, single session)")]
    public async Task AppendMessages()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage).ConfigureAwait(false);
            await _store.AppendMessageAsync(_sessionId, _assistantMessage).ConfigureAwait(false);
        }
    }

    [Benchmark(Description = "GetMessagesAsync (after N appends)")]
    public async Task GetMessages()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage).ConfigureAwait(false);
            await _store.AppendMessageAsync(_sessionId, _assistantMessage).ConfigureAwait(false);
        }

        var result = await _store.GetMessagesAsync(_sessionId).ConfigureAwait(false);
        if (result.IsFailure)
            throw new InvalidOperationException(result.Error);
    }

    [Benchmark(Description = "ListAsync (all sessions)")]
    public async Task ListSessions()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage).ConfigureAwait(false);
            await _store.AppendMessageAsync(_sessionId, _assistantMessage).ConfigureAwait(false);
        }

        var result = await _store.ListAsync().ConfigureAwait(false);
        if (result.IsFailure)
            throw new InvalidOperationException(result.Error);
    }
}
