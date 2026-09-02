#nullable enable
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="JsonlSessionStore" /> hot paths:
///     - <see cref="JsonlSessionStore.AppendMessageAsync" />: append-only write,
///     serializes one message to JSON and appends a line to the .jsonl file.
///     - <see cref="JsonlSessionStore.GetMessagesAsync" />: reads the entire
///     .jsonl file, parses each line, dedupes by message id, sorts by timestamp.
///     Each iteration uses a fresh temp directory to avoid unbounded file growth
///     contaminating later iterations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class JsonlSessionStoreBenchmark
{
    private AssistantMessage _assistantMessage = null!;
    private string _rootDirectory = null!;
    private Session _session = null!;
    private string _sessionId = null!;
    private JsonlSessionStore _store = null!;
    private UserMessage _userMessage = null!;

    [Params(10, 100)]
    public int MessageCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "harbor-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
        _store = new JsonlSessionStore(_rootDirectory, NullLogger<JsonlSessionStore>.Instance);

#pragma warning disable RS0030 // Do not use APIs banned for analyzers — BenchmarkDotNet [IterationSetup] requires sync
        var createResult = _store.CreateAsync("/tmp", "code", "stub", "stub-1").GetAwaiter().GetResult();
#pragma warning restore RS0030
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
            new ContentPart[]
            {
                new TextPart("Here is a function that reverses a string:\n\n```csharp\nstring Reverse(string s) => new string(s.Reverse().ToArray());\n```")
            },
            StopReason.Stop,
            new Usage(120, 35),
            "stub-1");
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

    [Benchmark(Description = "AppendMessageAsync (N writes)")]
    public async Task AppendMessageAsync_N()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Benchmark(Description = "AppendMessageAsync (interleaved user+assistant)")]
    public async Task AppendMessageAsync_Interleaved()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            await _store.AppendMessageAsync(_sessionId, _userMessage, CancellationToken.None).ConfigureAwait(false);
            await _store.AppendMessageAsync(_sessionId, _assistantMessage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Benchmark(Description = "GetMessagesAsync (after N writes)")]
    public async Task GetMessagesAsync_AfterN()
    {
        // Seed the session with N messages first.
        for (int i = 0; i < MessageCount; i++)
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

/// <summary>
///     Cold-parse cost of the JSONL read path (perf sprint):
///     - <see cref="ParseLine_User" /> / <see cref="ParseLine_Assistant" />:
///     the NEW path — raw UTF-8 span parse via <see cref="JsonlLineParser" />;
///     only strings that become part of the message object graph may allocate.
///     - <see cref="OldPath_ParseLine_User" /> / <see cref="OldPath_ParseLine_Assistant" />:
///     the OLD path it replaces — per-line <c>string</c> +
///     <c>Encoding.UTF8.GetBytes</c>, property names materialized via
///     <c>GetString()</c>, and the payload round-tripped through
///     <see cref="JsonElement" /> / <c>GetRawText()</c> / re-encode. The
///     delta vs the new benchmarks is the machinery overhead eliminated.
///     - <see cref="GetMessagesAsync_ColdParse" />: whole-file read + parse
///     of a pre-seeded session (file seeded once in <see cref="Setup" />,
///     fresh store per invocation so the parse cache is cold), the
///     10k-message acceptance scenario.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class JsonlParseBenchmark
{
    private const string UserLine =
        """{"type":"message","id":"msg-000123","parentId":null,"role":"user","createdAt":"2026-08-29T02:30:41.0784502+00:00","payload":{"content":"Write a C# function that reverses a string. Please include a unit test as well.","agent":"code","model":"claude-opus-4"}}""";

    private const string AssistantLine =
        """{"type":"message","id":"msg-000124","parentId":null,"role":"assistant","createdAt":"2026-08-29T02:30:42.0784502+00:00","payload":{"parts":[{"type":"text","text":"Here is a function that reverses a string:\n\n```csharp\nstring Reverse(string s) => new string(s.Reverse().ToArray());\n```"}],"stopReason":"stop","usage":{"inputTokens":120,"outputTokens":35},"model":"claude-opus-4","isSummary":false}}""";

    private string _root = null!;
    private string _fileSessionId = null!;
    private byte[] _userLineBytes = null!;
    private byte[] _assistantLineBytes = null!;
    private readonly string _sessionId = "bench-session";

    [Params(100, 10_000)]
    public int MessageCount;

    [GlobalSetup]
    public void Setup()
    {
        _userLineBytes = Encoding.UTF8.GetBytes(UserLine);
        _assistantLineBytes = Encoding.UTF8.GetBytes(AssistantLine);

        // Seed the session file once — the measured op must be the parse,
        // not the seeding (StringBuilder + WriteAllText would swamp the
        // parse-path allocation numbers).
        _root = Path.Combine(Path.GetTempPath(), "harbor-parse-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        string sessionId = "bench-" + Guid.NewGuid().ToString("N");
        _fileSessionId = sessionId;

        var sb = new StringBuilder(64 * 1024 * MessageCount / 1000);
        sb.AppendLine($$"""
            {"type":"session","version":1,"id":"{{sessionId}}","projectId":"bench","directory":"/tmp","title":"bench","agent":"code","model":"stub-1","providerId":"stub","createdAt":"2026-08-29T00:00:00.0000000+00:00","updatedAt":"2026-08-29T00:00:00.0000000+00:00"}
            """);

        for (int i = 0; i < MessageCount / 2; i++)
        {
            // Hour rolls over at i = 3600; minutes stay < 60 so every
            // timestamp parses.
            string ts1 = $"2026-08-29T{2 + i / 3600:D2}:{i / 60 % 60:D2}:{i % 60:D2}.{i % 1000:D3}+00:00";
            string ts2 = $"2026-08-29T{3 + i / 3600:D2}:{i / 60 % 60:D2}:{i % 60:D2}.{i % 1000:D3}+00:00";
            sb.AppendLine($$$"""
                {"type":"message","id":"msg-u-{{{i:D6}}}","parentId":null,"role":"user","createdAt":"{{{ts1}}}","payload":{"content":"Write a C# function that reverses a string. Include unit tests.","agent":"code","model":"stub-1"}}
                """);
            sb.AppendLine($$$"""
                {"type":"message","id":"msg-a-{{{i:D6}}}","parentId":null,"role":"assistant","createdAt":"{{{ts2}}}","payload":{"parts":[{"type":"text","text":"Here is a function that reverses a string: Reverse(s). Runs in O(n)."}],"stopReason":"stop","usage":{"inputTokens":120,"outputTokens":35},"model":"stub-1","isSummary":false}}
                """);
        }

        File.WriteAllText(Path.Combine(_root, sessionId + ".jsonl"), sb.ToString());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best-effort cleanup */ }
    }

    // ── NEW path (raw UTF-8 span parser) ──────────────────────────────────

    [Benchmark(Description = "NEW: parse one user line (raw UTF-8 span)", Baseline = true)]
    public Result<AgentMessage> ParseLine_User()
    {
        return JsonlLineParser.Parse(_userLineBytes, _sessionId);
    }

    [Benchmark(Description = "NEW: parse one assistant line (raw UTF-8 span)")]
    public Result<AgentMessage> ParseLine_Assistant()
    {
        return JsonlLineParser.Parse(_assistantLineBytes, _sessionId);
    }

    // ── OLD path (string + GetBytes + JsonElement payload round-trip) ─────

    [Benchmark(Description = "OLD: parse one user line (string + JsonElement round-trip)")]
    public Result<AgentMessage> OldPath_ParseLine_User()
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_userLineBytes)));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>("JSON does not start with an object");

            string? id = null;
            string? parentId = null;
            DateTimeOffset createdAt = default;
            JsonElement? payload = default;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                string propName = reader.GetString()!;
                reader.Read();
                switch (propName)
                {
                    case "id": id = reader.GetString()!; break;
                    case "createdAt": createdAt = reader.GetDateTimeOffset(); break;
                    case "parentId": if (reader.TokenType == JsonTokenType.String) parentId = reader.GetString()!; break;
                    case "payload": payload = JsonSerializer.Deserialize(ref reader, JsonlCodecContext.Default.JsonElement); break;
                }
            }

            if (id is null || payload is null)
                return Result.Failure<AgentMessage>("missing id/payload");

            var payloadReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payload.Value.GetRawText()));
            if (!payloadReader.Read() || payloadReader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>("payload is not an object");

            string? content = null;
            string? agent = null;
            string? model = null;
            while (payloadReader.Read())
            {
                if (payloadReader.TokenType == JsonTokenType.EndObject)
                    break;
                if (payloadReader.TokenType != JsonTokenType.PropertyName)
                    continue;

                string propName = payloadReader.GetString()!;
                payloadReader.Read();
                switch (propName)
                {
                    case "content": content = payloadReader.GetString()!; break;
                    case "agent": agent = payloadReader.GetString()!; break;
                    case "model": model = payloadReader.GetString()!; break;
                }
            }

            if (content is null || agent is null || model is null)
                return Result.Failure<AgentMessage>("missing content/agent/model");

            return Result.Success<AgentMessage>(new UserMessage(id, _sessionId, createdAt, content, agent, model, parentId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>(ex.Message);
        }
    }

    [Benchmark(Description = "OLD: parse one assistant line (string + JsonElement round-trip)")]
    public Result<AgentMessage> OldPath_ParseLine_Assistant()
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_assistantLineBytes)));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>("JSON does not start with an object");

            string? id = null;
            string? parentId = null;
            DateTimeOffset createdAt = default;
            JsonElement? payload = default;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                string propName = reader.GetString()!;
                reader.Read();
                switch (propName)
                {
                    case "id": id = reader.GetString()!; break;
                    case "createdAt": createdAt = reader.GetDateTimeOffset(); break;
                    case "parentId": if (reader.TokenType == JsonTokenType.String) parentId = reader.GetString()!; break;
                    case "payload": payload = JsonSerializer.Deserialize(ref reader, JsonlCodecContext.Default.JsonElement); break;
                }
            }

            if (id is null || payload is null)
                return Result.Failure<AgentMessage>("missing id/payload");

            var payloadReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payload.Value.GetRawText()));
            if (!payloadReader.Read() || payloadReader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>("payload is not an object");

            List<ContentPart>? parts = null;
            StopReason? stopReason = null;
            Usage? usage = null;
            string? model = null;
            bool isSummary = false;
            string? summaryFirstKeptId = null;

            while (payloadReader.Read())
            {
                if (payloadReader.TokenType == JsonTokenType.EndObject)
                    break;
                if (payloadReader.TokenType != JsonTokenType.PropertyName)
                    continue;

                string propName = payloadReader.GetString()!;
                payloadReader.Read();
                switch (propName)
                {
                    case "parts":
                        parts = [];
                        while (payloadReader.Read() && payloadReader.TokenType != JsonTokenType.EndArray)
                        {
                            if (payloadReader.TokenType != JsonTokenType.StartObject)
                                continue;

                            string? pText = null;
                            string? pType = null;
                            while (payloadReader.Read() && payloadReader.TokenType != JsonTokenType.EndObject)
                            {
                                if (payloadReader.TokenType != JsonTokenType.PropertyName)
                                    continue;

                                string pProp = payloadReader.GetString()!;
                                payloadReader.Read();
                                if (pProp == "type") pType = payloadReader.GetString()!;
                                else if (pProp == "text") pText = payloadReader.GetString()!;
                            }

                            if (pType == "text" && pText is not null)
                                parts.Add(new TextPart(pText));
                        }

                        break;
                    case "stopReason": stopReason = StopReasonJsonConverter.Parse(payloadReader.GetString()!); break;
                    case "usage": usage = JsonSerializer.Deserialize(ref payloadReader, JsonlCodecContext.Default.Usage) ?? new Usage(0, 0); break;
                    case "model": model = payloadReader.GetString()!; break;
                    case "isSummary": isSummary = payloadReader.GetBoolean(); break;
                    case "summaryFirstKeptId": summaryFirstKeptId = payloadReader.GetString()!; break;
                }
            }

            if (parts is null || stopReason is null || usage is null || model is null)
                return Result.Failure<AgentMessage>("missing parts/stopReason/usage/model");

            return Result.Success<AgentMessage>(new AssistantMessage(
                id, _sessionId, createdAt, parts, stopReason.Value, usage, model, parentId, isSummary, summaryFirstKeptId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>(ex.Message);
        }
    }

    // ── Whole-file cold parse ─────────────────────────────────────────────

    [Benchmark(Description = "GetMessagesAsync cold parse (whole file, cache-cold store)")]
    public int GetMessagesAsync_ColdParse()
    {
        // The file was seeded in GlobalSetup; a fresh store instance per
        // invocation means a cold parse cache — the measured op is exactly
        // the disk read + span parse + message materialization.
        var store = new JsonlSessionStore(_root, NullLogger<JsonlSessionStore>.Instance);

#pragma warning disable RS0030 // Do not use APIs banned for analyzers — sync body in benchmark op
        var result = store.GetMessagesAsync(_fileSessionId).GetAwaiter().GetResult();
#pragma warning restore RS0030
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error);
        }

        return result.Value.Count;
    }
}
