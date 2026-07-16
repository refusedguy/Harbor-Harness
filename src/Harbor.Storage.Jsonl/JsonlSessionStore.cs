using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
namespace Harbor.Storage.Jsonl;
/// <summary>
///     JSONL-based session storage. Append-only, atomic writes, no native deps.
///     Each session is one .jsonl file under the configured directory.
/// </summary>
public sealed class JsonlSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
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

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        try
        {
            var sessions = new List<Session>();
            foreach (string file in Directory.EnumerateFiles(_rootDirectory, "*.jsonl"))
            {
                string sessionId = Path.GetFileNameWithoutExtension(file);
                var getResult = GetAsync(sessionId, ct).GetAwaiter().GetResult();
                if (getResult.IsSuccess)
                {
                    if (projectId is null || getResult.Value.ProjectId == projectId)
                        sessions.Add(getResult.Value);
                }
            }

            sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return Task.FromResult(Result.Success<IReadOnlyList<Session>>(sessions));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<Session>>(ex.Message));
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
        try
        {
            string sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
                return Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found.");

            var messages = new Dictionary<string, AgentMessage>();

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
                        var msg = DeserializeMessage(doc.RootElement);
                        if (msg is not null)
                            messages[msg.Id] = msg; // latest entry wins
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed line in session {SessionId}", sessionId);
                }
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

    private static AgentMessage? DeserializeMessage(JsonElement element)
    {
        string id = element.GetProperty("id").GetString()!;
        var createdAt = element.GetProperty("createdAt").GetDateTimeOffset();
        string? parentId = element.TryGetProperty("parentId", out var p) ? p.GetString() : null;
        string role = element.GetProperty("role").GetString()!;
        var payload = element.GetProperty("payload");
        string sessionId = ""; // populated by file context

        if (role == "user")
        {
            return new UserMessage(
                id,
                sessionId,
                createdAt,
                payload.GetProperty("content").GetString()!,
                payload.GetProperty("agent").GetString()!,
                payload.GetProperty("model").GetString()!,
                parentId);
        }

        if (role == "assistant")
        {
            var parts = payload.GetProperty("parts").EnumerateArray()
                .Select(DeserializePart)
                .Where(p => p is not null)
                .Cast<ContentPart>()
                .ToList();

            var stopReason = Enum.Parse<StopReason>(payload.GetProperty("stopReason").GetString()!, true);
            var usage = payload.GetProperty("usage").Deserialize<Usage>(JsonOptions) ?? new Usage(0, 0);
            string model = payload.GetProperty("model").GetString()!;
            bool isSummary = payload.TryGetProperty("isSummary", out var s) && s.GetBoolean();
            string? summaryFirstKeptId = payload.TryGetProperty("summaryFirstKeptId", out var sf) ? sf.GetString() : null;

            return new AssistantMessage(
                id,
                sessionId,
                createdAt,
                parts,
                stopReason,
                usage,
                model,
                parentId,
                isSummary,
                summaryFirstKeptId);
        }

        if (role == "tool_result")
        {
            var results = payload.GetProperty("results").EnumerateArray()
                .Select(r => new ToolResultEntry(
                    r.GetProperty("toolCallId").GetString()!,
                    r.GetProperty("toolName").GetString()!,
                    r.GetProperty("output").GetString()!,
                    r.GetProperty("isError").GetBoolean()))
                .ToList();

            return new ToolResultMessage(
                id,
                sessionId,
                createdAt,
                results,
                parentId);
        }

        return null;
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
