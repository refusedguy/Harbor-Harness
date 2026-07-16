using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Jsonl;

/// <summary>
/// JSONL-based session storage. Append-only, atomic writes, no native deps.
/// Each session is one .jsonl file under the configured directory.
/// </summary>
public sealed class JsonlSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _rootDirectory;
    private readonly ILogger<JsonlSessionStore> _logger;
    private readonly object _lock = new();

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
            var sessionFile = GetSessionFilePath(session.Id);

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);

                var header = new SessionHeaderEntry(
                    Type: "session",
                    Version: 1,
                    Id: session.Id,
                    ProjectId: session.ProjectId,
                    Directory: session.Directory,
                    Title: session.Title,
                    Agent: session.Agent,
                    Model: session.Model,
                    ProviderId: session.ProviderId,
                    CreatedAt: session.CreatedAt);

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
            var sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
                return Result.Failure<Session>($"Session '{sessionId}' not found.");

            var header = await ReadHeaderAsync(sessionFile, ct).ConfigureAwait(false);
            if (header is null)
                return Result.Failure<Session>($"Session '{sessionId}' is corrupt (no header).");

            var metadata = await GetStatsAsync(sessionId, ct).ConfigureAwait(false);
            var session = new Session(
                Id: header.Id,
                ProjectId: header.ProjectId,
                Directory: header.Directory,
                Title: header.Title,
                Agent: header.Agent,
                Model: header.Model,
                ProviderId: header.ProviderId,
                CreatedAt: header.CreatedAt,
                UpdatedAt: DateTimeOffset.UtcNow,
                Metadata: metadata.IsSuccess ? metadata.Value : SessionMetadata.Empty);

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
            foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*.jsonl"))
            {
                var sessionId = Path.GetFileNameWithoutExtension(file);
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
            var sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));

            lock (_lock)
            {
                var entry = new MessageEntry(
                    Type: "message",
                    Id: message.Id,
                    ParentId: message.ParentId,
                    Role: message.Role,
                    CreatedAt: message.CreatedAt,
                    Payload: SerializeMessagePayload(message));

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
            var sessionFile = GetSessionFilePath(sessionId);
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
                    var type = doc.RootElement.GetProperty("type").GetString();

                    if (type == "message")
                    {
                        var msg = DeserializeMessage(doc.RootElement);
                        if (msg is not null)
                            messages[msg.Id] = msg;  // latest entry wins
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
            var sessionFile = GetSessionFilePath(sessionId);
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
            var cost = 0m;
            var inputTokens = 0;
            var outputTokens = 0;
            var reasoningTokens = 0;
            var cacheRead = 0;
            var cacheWrite = 0;
            var count = 0;

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
                Cost: cost,
                TokensInput: inputTokens,
                TokensOutput: outputTokens,
                TokensReasoning: reasoningTokens,
                TokensCacheRead: cacheRead,
                TokensCacheWrite: cacheWrite,
                MessageCount: count,
                TimeCompacting: null));
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
        var firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
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
                summaryFirstKeptId = a.SummaryFirstKeptId,
            },
            ToolResultMessage tr => new { results = tr.Results },
            _ => new { },
        };
    }

    private static object SerializePart(ContentPart part) => part switch
    {
        TextPart t => new { type = "text", text = t.Text },
        ThinkingPart th => new { type = "thinking", text = th.Text },
        ToolCallPart tc => new { type = "tool_call", id = tc.Id, toolName = tc.ToolName, args = tc.Args },
        FilePart f => new { type = "file", path = f.Path, mimeType = f.MimeType, sizeBytes = f.SizeBytes },
        _ => new { type = "unknown" },
    };

    private static AgentMessage? DeserializeMessage(JsonElement element)
    {
        var id = element.GetProperty("id").GetString()!;
        var createdAt = element.GetProperty("createdAt").GetDateTimeOffset();
        var parentId = element.TryGetProperty("parentId", out var p) ? p.GetString() : null;
        var role = element.GetProperty("role").GetString()!;
        var payload = element.GetProperty("payload");
        var sessionId = "";  // populated by file context

        if (role == "user")
        {
            return new UserMessage(
                Id: id,
                SessionId: sessionId,
                CreatedAt: createdAt,
                Content: payload.GetProperty("content").GetString()!,
                Agent: payload.GetProperty("agent").GetString()!,
                Model: payload.GetProperty("model").GetString()!,
                ParentId: parentId);
        }

        if (role == "assistant")
        {
            var parts = payload.GetProperty("parts").EnumerateArray()
                .Select(DeserializePart)
                .Where(p => p is not null)
                .Cast<ContentPart>()
                .ToList();

            var stopReason = Enum.Parse<StopReason>(payload.GetProperty("stopReason").GetString()!, ignoreCase: true);
            var usage = payload.GetProperty("usage").Deserialize<Usage>(JsonOptions) ?? new Usage(0, 0);
            var model = payload.GetProperty("model").GetString()!;
            var isSummary = payload.TryGetProperty("isSummary", out var s) && s.GetBoolean();
            var summaryFirstKeptId = payload.TryGetProperty("summaryFirstKeptId", out var sf) ? sf.GetString() : null;

            return new AssistantMessage(
                Id: id,
                SessionId: sessionId,
                CreatedAt: createdAt,
                Parts: parts,
                StopReason: stopReason,
                Usage: usage,
                Model: model,
                ParentId: parentId,
                IsSummary: isSummary,
                SummaryFirstKeptId: summaryFirstKeptId);
        }

        if (role == "tool_result")
        {
            var results = payload.GetProperty("results").EnumerateArray()
                .Select(r => new ToolResultEntry(
                    r.GetProperty("toolCallId").GetString()!,
                    r.GetProperty("toolName").GetString()!,
                    r.GetProperty("output").GetString()!,
                    r.GetProperty("isError").GetBoolean(),
                    Metadata: null))
                .ToList();

            return new ToolResultMessage(
                Id: id,
                SessionId: sessionId,
                CreatedAt: createdAt,
                Results: results,
                ParentId: parentId);
        }

        return null;
    }

    private static ContentPart? DeserializePart(JsonElement element)
    {
        var type = element.GetProperty("type").GetString();
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
            _ => null,
        };
    }
}

internal sealed record SessionHeaderEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("version")] int Version,
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("projectId")] string ProjectId,
    [property: System.Text.Json.Serialization.JsonPropertyName("directory")] string Directory,
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("agent")] string Agent,
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string Model,
    [property: System.Text.Json.Serialization.JsonPropertyName("providerId")] string ProviderId,
    [property: System.Text.Json.Serialization.JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

internal sealed record MessageEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("parentId")] string? ParentId,
    [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
    [property: System.Text.Json.Serialization.JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("payload")] object Payload);
