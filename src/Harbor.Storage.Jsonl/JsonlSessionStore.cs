using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
namespace Harbor.Storage.Jsonl;
/// <summary>
///     JSONL-based session storage. Append-only, atomic writes, no native deps.
///     Each session is one .jsonl file under the configured directory.
/// </summary>
public sealed class JsonlSessionStore : ISessionStore
{
    // TODO(principles)[PERF, байтоебля]: JsonOptions uses reflection-based
    // JsonSerializer.Deserialize<SessionHeaderEntry>(line, JsonOptions) — это
    // генерит IL2026 warnings под NativeAOT и боксит record'ы. Для AOT нужен
    // JsonTypeInfo<> через JsonSerializerContext. См. аудит §PERF-003, §AOT-001.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // TODO(principles)[CONCURRENCY]: простой `lock(_lock)` сериализует ВСЕ записи
    // во ВСЕ сессии. Если одновременно идут 10 сессий, каждая ждет другую. Это OK
    // для File.AppendAllText (atomic per-call), но плохо для batch-загрузки.
    // Альтернатива: per-session SemaphoreSlim (Dictionary<sessionId, SemaphoreSlim>),
    // либо System.Threading.Channels для write queue. См. аудит §PERF-004.
    private readonly object _lock = new();
    private readonly ILogger<JsonlSessionStore> _logger;

    private readonly string _rootDirectory;

    public JsonlSessionStore(string rootDirectory, ILogger<JsonlSessionStore> logger)
    {
        _rootDirectory = rootDirectory;
        _logger = logger;

        if (!Directory.Exists(_rootDirectory))
        {
            Directory.CreateDirectory(_rootDirectory);
        }
    }

    public Task<Result<Session>> CreateAsync(
        string directory,
        string agentName,
        string providerId,
        string modelId,
        CancellationToken ct = default)
    {
        try
        {
            var session = Session.Create(directory, agentName, providerId, modelId);
            string sessionFile = GetSessionFilePath(session.Id);

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);

                var header = new SessionHeaderEntry(
                    "session",
                    1,
                    session.Id,
                    session.ProjectId,
                    session.Directory,
                    session.Title,
                    session.Agent,
                    session.Model,
                    session.ProviderId,
                    session.CreatedAt);

                File.AppendAllText(sessionFile, JsonSerializer.Serialize(header, JsonOptions) + "\n");
            }

            return Task.FromResult(Result.Success(session));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session");
            return Task.FromResult(Result.Failure<Session>(ex.Message));
        }
    }

    public async Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            string sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
                return Result.Failure<Session>($"Session '{sessionId}' not found.");

            var header = await ReadHeaderAsync(sessionFile, ct).ConfigureAwait(false);
            if (header is null)
                return Result.Failure<Session>($"Session '{sessionId}' is corrupt (no header).");

            var metadata = await GetStatsAsync(sessionId, ct).ConfigureAwait(false);
            var session = new Session(
                header.Id,
                header.ProjectId,
                header.Directory,
                header.Title,
                header.Agent,
                header.Model,
                header.ProviderId,
                header.CreatedAt,
                DateTimeOffset.UtcNow,
                metadata.IsSuccess ? metadata.Value : SessionMetadata.Empty);

            return Result.Success(session);
        }
        catch (Exception ex)
        {
            return Result.Failure<Session>(ex.Message);
        }
    }

    public async Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        try
        {
            var sessions = new List<Session>();
            foreach (string file in Directory.EnumerateFiles(_rootDirectory, "*.jsonl"))
            {
                string sessionId = Path.GetFileNameWithoutExtension(file);
                var getResult = await GetAsync(sessionId, ct).ConfigureAwait(false);
                if (getResult.IsSuccess)
                {
                    if (projectId is null || getResult.Value.ProjectId == projectId)
                        sessions.Add(getResult.Value);
                }
            }

            sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return Result.Success<IReadOnlyList<Session>>(sessions);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<Session>>(ex.Message);
        }
    }

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        try
        {
            string sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));

            lock (_lock)
            {
                var entry = new MessageEntry(
                    "message",
                    message.Id,
                    message.ParentId,
                    message.Role,
                    message.CreatedAt,
                    SerializeMessagePayload(message));

                File.AppendAllText(sessionFile, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append message to session {SessionId}", sessionId);
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        // JSONL is append-only; updates are recorded as new entries with same id
        // For simplicity, we just append again (the latest entry wins on read)
        return AppendMessageAsync(sessionId, message, ct);
    }

    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        // TODO(principles)[PERF, байтоебля]: на каждый чанк строки вызывается
        // JsonDocument.Parse(line) — тысячи аллокаций. Для длинных сессий (10k+ строк)
        // это существенный overhead. Альтернативы: (1) Utf8JsonReader на ReadOnlySpan<byte>,
        // (2) MemoryPack-encoded binary формат вместо JSONL (есть же [MemoryPackable] на
        // всех сообщениях!), (3) streaming deserialize. См. аудит §PERF-005.
        // §PERF-005 (PARTIAL): the full Utf8JsonReader rewrite is risky without AOT
        // testing, so we keep JsonDocument.Parse for now. The ROP path (§ROP-001) is
        // fixed: per-line deserialization errors are aggregated into a `List<string>`
        // and surfaced via _logger.LogWarning, while still returning the successfully
        // deserialized messages (instead of silently swallowing the failure).
        try
        {
            string sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
                return Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found.");

            var messages = new Dictionary<string, AgentMessage>();
            var errors = new List<string>(capacity: 0); // capacity 0 → lazily allocated on first error

            using var reader = new StreamReader(sessionFile);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    string? type = doc.RootElement.GetProperty("type").GetString();

                    if (type == "message")
                    {
                        var msgResult = DeserializeMessage(sessionId, doc.RootElement);
                        if (msgResult.IsSuccess)
                            messages[msgResult.Value.Id] = msgResult.Value; // latest entry wins
                        else
                            errors.Add(msgResult.Error);
                    }
                }
                catch (Exception ex)
                {
                    // §ROP-001 (RESOLVED): per-line JSON parse errors are aggregated
                    // and logged at Warning level. Previously the caller silently
                    // swallowed these, leaving the user with a truncated session
                    // transcript and no diagnostic.
                    errors.Add($"Line parse failed: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                _logger.LogWarning("Encountered {ErrorCount} malformed line(s) reading session {SessionId}: {Errors}",
                    errors.Count, sessionId, string.Join("; ", errors));
            }

            var ordered = messages.Values.OrderBy(m => m.CreatedAt).ToList();
            return Result.Success<IReadOnlyList<AgentMessage>>(ordered);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<AgentMessage>>(ex.Message);
        }
    }

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            string sessionFile = GetSessionFilePath(sessionId);
            if (File.Exists(sessionFile))
            {
                File.Delete(sessionFile);
            }
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var messagesResult = await GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
            if (messagesResult.IsFailure)
                return Result.Failure<SessionMetadata>(messagesResult.Error);

            var messages = messagesResult.Value;
            decimal cost = 0m;
            int inputTokens = 0;
            int outputTokens = 0;
            int reasoningTokens = 0;
            int cacheRead = 0;
            int cacheWrite = 0;
            int count = 0;

            foreach (var msg in messages)
            {
                if (msg is AssistantMessage a)
                {
                    inputTokens += a.Usage.InputTokens;
                    outputTokens += a.Usage.OutputTokens;
                    reasoningTokens += a.Usage.ReasoningTokens ?? 0;
                    cacheRead += a.Usage.CacheReadTokens ?? 0;
                    cacheWrite += a.Usage.CacheWriteTokens ?? 0;
                    count++;
                }
            }

            return Result.Success(new SessionMetadata(
                cost,
                inputTokens,
                outputTokens,
                reasoningTokens,
                cacheRead,
                cacheWrite,
                count,
                null));
        }
        catch (Exception ex)
        {
            return Result.Failure<SessionMetadata>(ex.Message);
        }
    }

    public async Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        // Stats are derived from messages; nothing to write
        await Task.CompletedTask.ConfigureAwait(false);
        return Result.Success();
    }

    private string GetSessionFilePath(string sessionId) =>
        Path.Combine(_rootDirectory, $"{sessionId}.jsonl");

    private async Task<SessionHeaderEntry?> ReadHeaderAsync(string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        string? firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(firstLine)) return null;

        try
        {
            return JsonSerializer.Deserialize<SessionHeaderEntry>(firstLine, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static object SerializeMessagePayload(AgentMessage message)
    {
        return message switch
        {
            UserMessage u => new { content = u.Content, agent = u.Agent, model = u.Model },
            AssistantMessage a => new
            {
                parts = a.Parts.Select(SerializePart).ToArray(),
                stopReason = a.StopReason.ToString().ToLowerInvariant(),
                usage = a.Usage,
                model = a.Model,
                isSummary = a.IsSummary,
                summaryFirstKeptId = a.SummaryFirstKeptId
            },
            ToolResultMessage tr => new { results = tr.Results },
            _ => new { }
        };
    }

    private static object SerializePart(ContentPart part) => part switch
    {
        TextPart t => new { type = "text", text = t.Text },
        ThinkingPart th => new { type = "thinking", text = th.Text },
        ToolCallPart tc => new { type = "tool_call", id = tc.Id, toolName = tc.ToolName, args = tc.Args },
        FilePart f => new { type = "file", path = f.Path, mimeType = f.MimeType, sizeBytes = f.SizeBytes },
        _ => new { type = "unknown" }
    };

    private static Result<AgentMessage> DeserializeMessage(string sessionId, JsonElement element)
    {
        // §ROP-001 (RESOLVED): returns Result<AgentMessage> instead of `null` so
        // the caller can surface a diagnostic message rather than silently dropping
        // the line. Each branch returns Result.Failure with a specific message
        // (missing field, unknown role, etc.) instead of `null`.
        // §OOP-003 (RESOLVED): `sessionId` is now a parameter (previously a
        // placeholder `""`), so the reconstructed AgentMessage is always in a valid
        // state — no escape hatch where the caller is expected to backfill it.
        string? id;
        try
        {
            id = element.GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"missing 'id': {ex.Message}");
        }
        if (string.IsNullOrEmpty(id))
            return Result.Failure<AgentMessage>("'id' is null or empty");

        DateTimeOffset createdAt;
        try
        {
            createdAt = element.GetProperty("createdAt").GetDateTimeOffset();
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"message {id}: missing/invalid 'createdAt': {ex.Message}");
        }

        string? parentId = element.TryGetProperty("parentId", out var p) ? p.GetString() : null;
        string? role = element.TryGetProperty("role", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(role))
            return Result.Failure<AgentMessage>($"message {id}: missing 'role'");

        if (!element.TryGetProperty("payload", out var payload))
            return Result.Failure<AgentMessage>($"message {id}: missing 'payload'");

        if (role == "user")
        {
            string? content = payload.TryGetProperty("content", out var c) ? c.GetString() : null;
            string? agent = payload.TryGetProperty("agent", out var a) ? a.GetString() : null;
            string? model = payload.TryGetProperty("model", out var m) ? m.GetString() : null;
            if (content is null || agent is null || model is null)
                return Result.Failure<AgentMessage>($"user message {id}: missing content/agent/model");

            return Result.Success<AgentMessage>(new UserMessage(
                id!, sessionId, createdAt, content!, agent!, model!, parentId));
        }

        if (role == "assistant")
        {
            if (!payload.TryGetProperty("parts", out var partsEl) || partsEl.ValueKind != JsonValueKind.Array)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'parts'");

            var parts = new List<ContentPart>();
            foreach (var partEl in partsEl.EnumerateArray())
            {
                var part = DeserializePart(partEl);
                if (part is not null) parts.Add(part);
            }

            if (!payload.TryGetProperty("stopReason", out var srEl) || srEl.ValueKind != JsonValueKind.String)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'stopReason'");
            StopReason stopReason;
            try
            {
                stopReason = Enum.Parse<StopReason>(srEl.GetString()!, true);
            }
            catch (Exception ex)
            {
                return Result.Failure<AgentMessage>($"assistant message {id}: invalid stopReason: {ex.Message}");
            }

            var usage = payload.TryGetProperty("usage", out var u)
                ? u.Deserialize<Usage>(JsonOptions) ?? new Usage(0, 0)
                : new Usage(0, 0);
            string? model = payload.TryGetProperty("model", out var m) ? m.GetString() : null;
            if (model is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'model'");

            bool isSummary = payload.TryGetProperty("isSummary", out var s) && s.GetBoolean();
            string? summaryFirstKeptId = payload.TryGetProperty("summaryFirstKeptId", out var sf) ? sf.GetString() : null;

            return Result.Success<AgentMessage>(new AssistantMessage(
                id!, sessionId, createdAt, parts, stopReason, usage, model!, parentId, isSummary, summaryFirstKeptId));
        }

        if (role == "tool_result")
        {
            if (!payload.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                return Result.Failure<AgentMessage>($"tool_result message {id}: missing 'results'");

            var results = new List<ToolResultEntry>();
            foreach (var rEl in resultsEl.EnumerateArray())
            {
                string? tcId = rEl.TryGetProperty("toolCallId", out var tci) ? tci.GetString() : null;
                string? tn = rEl.TryGetProperty("toolName", out var tnEl) ? tnEl.GetString() : null;
                string? output = rEl.TryGetProperty("output", out var o) ? o.GetString() : null;
                bool isError = rEl.TryGetProperty("isError", out var ie) && ie.GetBoolean();
                if (tcId is null || tn is null || output is null)
                    return Result.Failure<AgentMessage>($"tool_result message {id}: malformed result entry");

                results.Add(new ToolResultEntry(tcId!, tn!, output!, isError));
            }

            return Result.Success<AgentMessage>(new ToolResultMessage(
                id!, sessionId, createdAt, results, parentId));
        }

        return Result.Failure<AgentMessage>($"message {id}: unknown role '{role}'");
    }

    private static ContentPart? DeserializePart(JsonElement element)
    {
        string? type = element.GetProperty("type").GetString();
        return type switch
        {
            "text" => new TextPart(element.GetProperty("text").GetString()!),
            "thinking" => new ThinkingPart(element.GetProperty("text").GetString()!),
            "tool_call" => new ToolCallPart(
                element.GetProperty("id").GetString()!,
                element.GetProperty("toolName").GetString()!,
                element.GetProperty("args").Deserialize<JsonElement>()),
            "file" => new FilePart(
                element.GetProperty("path").GetString()!,
                element.GetProperty("mimeType").GetString()!,
                element.GetProperty("sizeBytes").GetInt64()),
            _ => null
        };
    }
}

internal sealed record SessionHeaderEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

internal sealed record MessageEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("payload")] object Payload);
