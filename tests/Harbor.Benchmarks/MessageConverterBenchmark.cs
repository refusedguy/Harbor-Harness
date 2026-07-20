using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks the <see cref="MessageConverter" /> JSON hot path used by
///     <see cref="JsonlSessionStore" />: every turn serializes each
///     <see cref="AgentMessage" /> to a JSONL line (via the store's append
///     path) and deserializes the whole session back on reload (via
///     <see cref="JsonlSessionStore.GetMessagesAsync" />). The underlying
///     serialization/deserialization is owned by <c>JsonlMessageCodec</c>,
///     which is the polymorphic payload codec for the JSONL wire format.
///     <para>
///         Three hot paths are exercised:
///         - Serialize a single message of varying content size (small / medium / large).
///         - Deserialize a single message from its serialized JSON string.
///         - Round-trip a list of N messages (1 / 10 / 100) via append + reload.
///     </para>
///     Each iteration uses a fresh temp directory so file growth does not
///     contaminate later iterations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class MessageConverterBenchmark
{
    private AssistantMessage _assistantLarge = null!;
    private AssistantMessage _assistantMedium = null!;
    private AssistantMessage _assistantSmall = null!;
    private UserMessage _userMessage = null!;
    private string _rootDirectory = null!;
    private Session _session = null!;
    private string _sessionId = null!;
    private string _sessionFile = null!;
    private JsonlSessionStore _store = null!;
    private ToolResultMessage _toolResultMessage = null!;
    private IReadOnlyList<AgentMessage> _messages = null!;

    [Params(1, 10, 100)]
    public int MessageCount { get; set; }

    [Params("small", "medium", "large")]
    public string ContentSize { get; set; } = "medium";

    [IterationSetup]
    public void IterationSetup()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "harbor-bench-msg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
        _store = new JsonlSessionStore(_rootDirectory, NullLogger<JsonlSessionStore>.Instance);

#pragma warning disable RS0030 // Do not use APIs banned for analyzers — BenchmarkDotNet [IterationSetup] requires sync
        var createResult = _store.CreateAsync("/tmp", "code", "stub", "stub-1").GetAwaiter().GetResult();
#pragma warning restore RS0030
        _session = createResult.IsSuccess ? createResult.Value : throw new InvalidOperationException("create failed");
        _sessionId = _session.Id;

        var now = DateTimeOffset.UtcNow;

        string CodeSample(int scale) =>
            string.Create(scale * 64, scale, (span, s) =>
            {
                const string template =
                    "public sealed class Calculator {\n    public int Add(int a, int b) => a + b;\n}\n";
                for (int i = 0; i < span.Length; i++)
                {
                    span[i] = template[i % template.Length];
                }
            });

        int scale = ContentSize switch
        {
            "small" => 1,
            "medium" => 64,
            "large" => 1024,
            _ => 64
        };
        string code = CodeSample(scale);

        _userMessage = new UserMessage(
            Guid.NewGuid().ToString("N"),
            _sessionId,
            now,
            "Implement a C# calculator with add, subtract, multiply and divide.",
            "code",
            "stub-1");

        _assistantSmall = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            _sessionId,
            now.AddSeconds(1),
            new ContentPart[]
            {
                new TextPart("Done. See the implementation below."),
                new ToolCallPart("tc_1", "write", JsonElement.Parse("""{"path":"Calculator.cs","content":"x"}"""))
            },
            StopReason.Stop,
            new Usage(120, 35),
            "stub-1");

        _assistantMedium = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            _sessionId,
            now.AddSeconds(1),
            new ContentPart[]
            {
                new TextPart("Here is a complete calculator implementation:\n\n```csharp\n" + code + "\n```"),
                new ThinkingPart("I should ensure division guards against zero."),
                new ToolCallPart("tc_1", "write", JsonElement.Parse("""{"path":"Calculator.cs"}"""))
            },
            StopReason.Stop,
            new Usage(1_280, 320),
            "stub-1");

        _assistantLarge = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            _sessionId,
            now.AddSeconds(1),
            new ContentPart[]
            {
                new TextPart(code),
                new TextPart(code),
                new ThinkingPart(code),
                new ToolCallPart("tc_1", "write", JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["path"] = "Calculator.cs", ["content"] = code }))
            },
            StopReason.Stop,
            new Usage(20_480, 5_120),
            "stub-1");

        _toolResultMessage = new ToolResultMessage(
            Guid.NewGuid().ToString("N"),
            _sessionId,
            now.AddSeconds(2),
            new ToolResultEntry[]
            {
                new ToolResultEntry("tc_1", "write", "Wrote 512 bytes to Calculator.cs", false)
            });

        // Pre-serialize a single assistant message to a JSONL line so the
        // standalone deserialize benchmark can re-read it from disk through a
        // fresh store instance (no parse cache).
        _store.AppendMessageAsync(_sessionId, _assistantMedium, CancellationToken.None).GetAwaiter().GetResult();
        _sessionFile = Path.Combine(_rootDirectory, _session.Id + ".jsonl");

        _messages = BuildMessages();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        try
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, true);
            }
        }
        catch
        {
            // Best-effort cleanup; temp dir may linger if a file handle is held.
        }
    }

    private IReadOnlyList<AgentMessage> BuildMessages()
    {
        var list = new List<AgentMessage>(MessageCount * 3);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < MessageCount; i++)
        {
            list.Add(_userMessage);
            list.Add(ContentSize == "large" ? _assistantLarge : ContentSize == "small" ? _assistantSmall : _assistantMedium);
            list.Add(_toolResultMessage);
        }
        return list;
    }

    /// <summary>
    ///     Serialize a single assistant message of the configured
    ///     <see cref="ContentSize" /> (small / medium / large) to a JSONL line.
    /// </summary>
    [Benchmark(Description = "Serialize single message (ContentSize)")]
    public void Serialize_Single()
    {
        var msg = ContentSize == "large" ? _assistantLarge : ContentSize == "small" ? _assistantSmall : _assistantMedium;
        _store.AppendMessageAsync(_sessionId, msg, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Deserialize a single message from its pre-serialized JSONL line
    ///     using the store's reload path (<see cref="JsonlSessionStore.GetMessagesAsync" />).
    ///     A fresh store instance is used per call so the parse cache does not
    ///     mask the deserialization cost.
    /// </summary>
    [Benchmark(Description = "Deserialize single message")]
    public void Deserialize_Single()
    {
        // Fresh store instance — independent parse cache, forces a real re-read
        // of the single pre-serialized message line from disk.
        var fresh = new JsonlSessionStore(_rootDirectory, NullLogger<JsonlSessionStore>.Instance);
        var result = fresh.GetMessagesAsync(_sessionId, CancellationToken.None).GetAwaiter().GetResult();
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error);
        }
    }

    /// <summary>
    ///     Round-trip N messages (MessageCount × {user, assistant, tool_result})
    ///     by appending them, then reloading the whole session from disk.
    /// </summary>
    [Benchmark(Description = "Round-trip N messages (append + reload)")]
    public void RoundTrip_N()
    {
        foreach (var msg in _messages)
        {
            _store.AppendMessageAsync(_sessionId, msg, CancellationToken.None).GetAwaiter().GetResult();
        }

        var result = _store.GetMessagesAsync(_sessionId, CancellationToken.None).GetAwaiter().GetResult();
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error);
        }
    }
}
