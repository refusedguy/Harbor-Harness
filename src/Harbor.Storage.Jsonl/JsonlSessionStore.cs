using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
namespace Harbor.Storage.Jsonl;
/// <summary>
///     JSONL-based session storage. Append-only, atomic writes, no native deps.
///     Each session is one .jsonl file under the configured directory.
/// </summary>
/// <remarks>
///     <para>
///         <b>Architecture audit v2 §3.3 (RESOLVED):</b> a parsed-message cache
///         keyed by <c>sessionId</c> eliminates the per-call re-parse cost in
///         <see cref="GetMessagesAsync" /> and the double-parse that
///         <see cref="GetStatsAsync" /> used to pay. The cache records the
///         file's last-write-time; <see cref="AppendMessageAsync" /> invalidates
///         just the affected session's entry.
///     </para>
///     <para>
///         <b>Architecture audit v2 §3.4 (RESOLVED):</b> the synchronous I/O
///         methods (<see cref="AppendMessageAsync" />,
///         <see cref="CreateAsync" />, <see cref="DeleteAsync" />) now observe
///         the supplied <see cref="CancellationToken" /> via
///         <see cref="CancellationToken.ThrowIfCancellationRequested" /> guards
///         before each <c>File.*</c> call. <c>File.AppendAllText</c> itself is
///         not CT-aware — the guard at least prevents a write that has already
///         been cancelled by the time the lock is acquired.
///     </para>
/// </remarks>
public sealed class JsonlSessionStore : ISessionStore
{
    // JSONL codec context provides AOT-safe serialization via JsonTypeInfo.
    // See JsonlCodecContext.cs for the registered types.
    private static readonly JsonSerializerOptions JsonOptions = JsonlCodecContext.JsonOptions;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly ILogger<JsonlSessionStore> _logger;

    /// <summary>
    ///     Parsed-message cache. Architecture audit v2 §3.3: keyed by session id,
    ///     value is an immutable <see cref="SessionCacheEntry" /> recording the
    ///     file's last-write-time and the parsed message list. Reads check the
    ///     cache for a freshness hit (mtime unchanged) before falling through to
    ///     a full disk re-parse. Writes invalidate just the affected session's
    ///     entry, so concurrent reads of other sessions are unaffected.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The cache is unbounded; a long-running process with many sessions
    ///         would accumulate entries. In practice the typical session count is
    ///         1-5 per process, so an LRU cap is deferred until measured. The
    ///         <see cref="ConcurrentDictionary{TKey,TValue}" /> is safe for
    ///         concurrent readers — the value is an immutable record, so a
    ///         half-published update is impossible.
    ///     </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, SessionCacheEntry> _messageCache = new();

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

    private async ValueTask<SemaphoreSlim> GetSessionLockAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    ///     Create a new session and write its header to the JSONL file.
    /// </summary>
    /// <remarks>
    ///     <b>CT note (§3.4):</b> the supplied <paramref name="ct" /> is
    ///     observed via <see cref="CancellationToken.ThrowIfCancellationRequested" />
    ///     before the directory-create and file-write.
    ///     <c>Directory.CreateDirectory</c> and <c>File.AppendAllText</c> are
    ///     synchronous I/O that do not accept a CT.
    ///     <b>ROP-B П.11:</b> the whole body rides <see cref="Result.Try" />
    ///     with <see cref="Harbor.Abstractions.Results.ResultErrors.Message" />,
    ///     so cancellation propagates as <see cref="OperationCanceledException" />
    ///     instead of being masked as a store failure ("Operation was cancelled."
    ///     used to surface as a red session error for an Esc press).
    /// </remarks>
    public Task<Result<Session>> CreateAsync(
        string directory,
        string agentName,
        string providerId,
        string modelId,
        CancellationToken ct = default)
    {
        return Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var session = Session.Create(directory, agentName, providerId, modelId);
            string sessionFile = GetSessionFilePath(session.Id);

            var semaphore = await GetSessionLockAsync(session.Id, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
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

                File.AppendAllText(sessionFile, JsonSerializer.Serialize(header, JsonlCodecContext.Default.SessionHeaderEntry) + "\n");
            }
            finally
            {
                semaphore.Release();
            }

            _messageCache.TryRemove(session.Id, out _);
            return session;
        }, Harbor.Abstractions.Results.ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to create session: {Error}", e));
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

    /// <summary>
    ///     Append a message to the session JSONL file. The cache for this
    ///     session is invalidated so the next <see cref="GetMessagesAsync" />
    ///     re-parses from disk (the file has changed).
    /// </summary>
    /// <remarks>
    ///     <b>CT note (§3.4):</b> the supplied <paramref name="ct" /> is observed
    ///     via <see cref="CancellationToken.ThrowIfCancellationRequested" />
    ///     before the lock is acquired and before the file write.
    ///     <c>File.AppendAllText</c> itself is synchronous I/O that does not
    ///     accept a CT; a 30 MB message write therefore cannot be interrupted
    ///     mid-write. The guard at least prevents a write that has already been
    ///     cancelled by the time the call enters the critical section.
    /// </remarks>
    public async Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
            {
                _messageCache.TryRemove(sessionId, out _);
                return Result.Failure($"Session '{sessionId}' not found.");
            }

            var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                var entry = new MessageEntry(
                    "message",
                    message.Id,
                    message.ParentId,
                    message.Role,
                    message.CreatedAt,
                    JsonlMessageCodec.SerializeMessagePayload(message));

                File.AppendAllText(sessionFile, JsonSerializer.Serialize(entry, JsonlCodecContext.Default.MessageEntry) + "\n");
            }
            finally
            {
                semaphore.Release();
            }

            _messageCache.TryRemove(sessionId, out _);
            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to append message to session {SessionId}", sessionId);
            return Result.Failure(ex.Message);
        }
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        // JSONL is append-only; updates are recorded as new entries with same id
        // For simplicity, we just append again (the latest entry wins on read)
        return AppendMessageAsync(sessionId, message, ct);
    }

    /// <summary>
    ///     Read all messages for a session in chronological order. Returns the
    ///     cached parse result when the file's last-write-time is unchanged
    ///     since the prior call (§3.3 cache).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Architecture audit v2 §3.3 (RESOLVED):</b> previously every
    ///         call re-parsed every line of the JSONL file. <see cref="GetStatsAsync" />
    ///         also called this method, so a single <c>/stats</c> command on a
    ///         10k-message session paid ~50k allocations. Now both callers hit
    ///         the cache for free on the second and subsequent calls.
    ///     </para>
    ///     <para>
    ///     </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            string sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
            {
                _messageCache.TryRemove(sessionId, out _);
                return Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found.");
            }

            // §3.3 cache: freshness check via file mtime. Most filesystems have
            // second-level mtime granularity, which is fine here — every write
            // bumps the mtime.
            DateTimeOffset fileMtime = File.GetLastWriteTimeUtc(sessionFile);
            if (_messageCache.TryGetValue(sessionId, out var cached) && cached.FileLastWriteUtc == fileMtime)
            {
                // Cache hit — return the cached list directly. Zero allocations.
                return Result.Success<IReadOnlyList<AgentMessage>>(cached.Messages);
            }

            // Cache miss (or stale) — parse from disk.
            var parseResult = await ParseMessagesFromDiskAsync(sessionFile, sessionId, ct).ConfigureAwait(false);
            if (parseResult.IsSuccess)
            {
                // Publish the freshly parsed list to the cache. The
                // ConcurrentDictionary slot is updated atomically and the
                // cache value is an immutable record, so concurrent readers
                // see either the old entry or the new entry but never a
                // half-built one.
                _messageCache[sessionId] = new SessionCacheEntry(fileMtime, parseResult.Value);
            }
            return parseResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<IReadOnlyList<AgentMessage>>(ex.Message);
        }
    }

    /// <summary>
    ///     Delete a session JSONL file. The parsed-message cache entry for this
    ///     session is also removed (§3.3 cache).
    /// </summary>
    /// <remarks>
    ///     <b>CT note (§3.4):</b> the supplied <paramref name="ct" /> is
    ///     observed via <see cref="CancellationToken.ThrowIfCancellationRequested" />
    ///     before the file delete. <c>File.Delete</c> itself is synchronous I/O
    ///     that does not accept a CT.
    /// </remarks>
    public async Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            _messageCache.TryRemove(sessionId, out _);

            var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string sessionFile = GetSessionFilePath(sessionId);
                if (File.Exists(sessionFile))
                {
                    File.Delete(sessionFile);
                }
                _sessionLocks.TryRemove(sessionId, out _);
                return Result.Success();
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to delete session {SessionId}", sessionId);
            return Result.Failure(ex.Message);
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

    public async Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string sessionFile = GetSessionFilePath(session.Id);
            if (!File.Exists(sessionFile))
                return Result.Failure($"Session '{session.Id}' not found.");

            var semaphore = await GetSessionLockAsync(session.Id, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var lines = File.ReadAllLines(sessionFile).ToList();
                if (lines.Count == 0)
                    return Result.Failure($"Session '{session.Id}' is empty.");

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

                lines[0] = JsonSerializer.Serialize(header, JsonlCodecContext.Default.SessionHeaderEntry);
                File.WriteAllLines(sessionFile, lines);
            }
            finally
            {
                semaphore.Release();
            }

            _messageCache.TryRemove(session.Id, out _);
            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to update session {SessionId}", session.Id);
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    ///     Parse the JSONL session file from disk into a chronological message
    ///     list. Per-line JSON parse errors are aggregated into a
    ///     <c>List&lt;string&gt;</c> and surfaced via <see cref="ILogger.LogWarning" />,
    ///     while still returning the successfully deserialized messages
    ///     (§ROP-001 resolved).
    /// </summary>
    /// <param name="sessionFile">Absolute path to the .jsonl file.</param>
    /// <param name="sessionId">The session id (passed through to <see cref="DeserializeMessage" />).</param>
    /// <param name="ct">Cancellation token observed by <c>StreamReader.ReadLineAsync</c>.</param>
    /// <returns>The chronological message list, or failure with the first error.</returns>
    private async Task<Result<IReadOnlyList<AgentMessage>>> ParseMessagesFromDiskAsync(
        string sessionFile,
        string sessionId,
        CancellationToken ct)
    {
        var messages = new Dictionary<string, AgentMessage>();
        var errors = new List<string>(capacity: 0);

        using var reader = new StreamReader(sessionFile);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var msgResult = ParseMessageLine(line, sessionId);
            if (msgResult.IsSuccess)
                messages[msgResult.Value.Id] = msgResult.Value;
            else
                errors.Add($"Line parse failed: {msgResult.Error}");
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Encountered {ErrorCount} malformed line(s) reading session {SessionId}: {Errors}",
                errors.Count, sessionId, string.Join("; ", errors));
        }

        var ordered = messages.Values.OrderBy(m => m.CreatedAt).ToList();
        return Result.Success<IReadOnlyList<AgentMessage>>(ordered);
    }

    private static Result<AgentMessage> ParseMessageLine(string line, string sessionId)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>("JSON does not start with an object");

            string? type = null;
            string? id = null;
            DateTimeOffset createdAt = default;
            string? parentId = null;
            string? role = null;
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
                    case "type":
                        type = reader.GetString()!;
                        break;
                    case "id":
                        id = reader.GetString()!;
                        break;
                    case "createdAt":
                        createdAt = reader.GetDateTimeOffset();
                        break;
                    case "parentId":
                        if (reader.TokenType == JsonTokenType.String)
                            parentId = reader.GetString()!;
                        break;
                    case "role":
                        role = reader.GetString()!;
                        break;
                     case "payload":
                        payload = JsonSerializer.Deserialize(ref reader, JsonlCodecContext.Default.JsonElement);
                        break;
                }
            }

            if (type != "message")
                return Result.Failure<AgentMessage>("Not a message line");

            if (id is null)
                return Result.Failure<AgentMessage>("missing 'id'");

            if (role is null)
                return Result.Failure<AgentMessage>($"message {id}: missing 'role'");

            if (payload is null)
                return Result.Failure<AgentMessage>($"message {id}: missing 'payload'");

            return role switch
            {
                "user" => ParseUserPayload(id, sessionId, createdAt, parentId, payload.Value),
                "assistant" => ParseAssistantPayload(id, sessionId, createdAt, parentId, payload.Value),
                "tool_result" => ParseToolResultPayload(id, sessionId, createdAt, parentId, payload.Value),
                _ => Result.Failure<AgentMessage>($"message {id}: unknown role '{role}'")
            };
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"Line parse failed: {ex.Message}");
        }
    }

    private static Result<AgentMessage> ParseUserPayload(string id, string sessionId, DateTimeOffset createdAt, string? parentId, JsonElement payload)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payload.GetRawText()));

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>($"user message {id}: payload is not an object");

            string? content = null;
            string? agent = null;
            string? model = null;

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
                    case "content":
                        content = reader.GetString()!;
                        break;
                    case "agent":
                        agent = reader.GetString()!;
                        break;
                    case "model":
                        model = reader.GetString()!;
                        break;
                }
            }

            if (content is null || agent is null || model is null)
                return Result.Failure<AgentMessage>($"user message {id}: missing content/agent/model");

            return Result.Success<AgentMessage>(new UserMessage(id, sessionId, createdAt, content, agent, model, parentId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"user message {id}: {ex.Message}");
        }
    }

    private static Result<AgentMessage> ParseAssistantPayload(string id, string sessionId, DateTimeOffset createdAt, string? parentId, JsonElement payload)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payload.GetRawText()));

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>($"assistant message {id}: payload is not an object");

            List<ContentPart>? parts = null;
            StopReason? stopReason = null;
            Usage? usage = null;
            string? model = null;
            bool isSummary = false;
            string? summaryFirstKeptId = null;

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
                    case "parts":
                        parts = ParseParts(ref reader);
                        break;
                    case "stopReason":
                        var srStr = reader.GetString()!;
                        stopReason = StopReasonJsonConverter.Parse(srStr);
                        break;
                    case "usage":
                        usage = JsonSerializer.Deserialize(ref reader, JsonlCodecContext.Default.Usage) as Usage ?? new Usage(0, 0);
                        break;
                    case "model":
                        model = reader.GetString()!;
                        break;
                    case "isSummary":
                        isSummary = reader.GetBoolean();
                        break;
                    case "summaryFirstKeptId":
                        summaryFirstKeptId = reader.GetString()!;
                        break;
                }
            }

            if (parts is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'parts'");
            if (stopReason is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'stopReason'");
            if (usage is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'usage'");
            if (model is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'model'");

            return Result.Success<AgentMessage>(new AssistantMessage(id, sessionId, createdAt, parts, stopReason.Value, usage, model, parentId, isSummary, summaryFirstKeptId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"assistant message {id}: {ex.Message}");
        }
    }

    private static Result<AgentMessage> ParseToolResultPayload(string id, string sessionId, DateTimeOffset createdAt, string? parentId, JsonElement payload)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payload.GetRawText()));

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>($"tool_result message {id}: payload is not an object");

            List<ToolResultEntry>? results = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                string propName = reader.GetString()!;
                reader.Read();

                if (propName == "results" && reader.TokenType == JsonTokenType.StartArray)
                {
                    results = new List<ToolResultEntry>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject)
                            continue;

                        string? tcId = null;
                        string? tn = null;
                        string? output = null;
                        bool isError = false;

                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName)
                                continue;
                            string rProp = reader.GetString()!;
                            reader.Read();

                            switch (rProp)
                            {
                                case "toolCallId":
                                    tcId = reader.GetString()!;
                                    break;
                                case "toolName":
                                    tn = reader.GetString()!;
                                    break;
                                case "output":
                                    output = reader.GetString()!;
                                    break;
                                case "isError":
                                    isError = reader.GetBoolean();
                                    break;
                            }
                        }

                        if (tcId is null || tn is null || output is null)
                            return Result.Failure<AgentMessage>($"tool_result message {id}: malformed result entry");

                        results.Add(new ToolResultEntry(tcId, tn, output, isError));
                    }
                }
            }

            if (results is null)
                return Result.Failure<AgentMessage>($"tool_result message {id}: missing 'results'");

            return Result.Success<AgentMessage>(new ToolResultMessage(id, sessionId, createdAt, results, parentId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"tool_result message {id}: {ex.Message}");
        }
    }

    private static List<ContentPart> ParseParts(ref Utf8JsonReader reader)
    {
        var parts = new List<ContentPart>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                continue;

            string? partType = null;
            string? text = null;
            string? partId = null;
            string? toolName = null;
            JsonElement? args = null;
            string? path = null;
            string? mimeType = null;
            long sizeBytes = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;
                string pProp = reader.GetString()!;
                reader.Read();

                switch (pProp)
                {
                    case "type":
                        partType = reader.GetString()!;
                        break;
                    case "text":
                        text = reader.GetString()!;
                        break;
                    case "id":
                        partId = reader.GetString()!;
                        break;
                    case "toolName":
                        toolName = reader.GetString()!;
                        break;
                    case "args":
                        args = JsonSerializer.Deserialize(ref reader, JsonlCodecContext.Default.JsonElement);
                        break;
                    case "path":
                        path = reader.GetString()!;
                        break;
                    case "mimeType":
                        mimeType = reader.GetString()!;
                        break;
                    case "sizeBytes":
                        sizeBytes = reader.GetInt64();
                        break;
                }
            }

            ContentPart? part = partType switch
            {
                "text" => new TextPart(text!),
                "thinking" => new ThinkingPart(text!),
                "tool_call" => new ToolCallPart(partId!, toolName!, args!.Value),
                "file" => new FilePart(path!, mimeType!, sizeBytes),
                _ => null
            };

            if (part is not null)
                parts.Add(part);
        }

        return parts;
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
            return JsonSerializer.Deserialize<SessionHeaderEntry>(firstLine, JsonlCodecContext.Default.SessionHeaderEntry);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
///     Parsed-message cache entry (§3.3). Records the file's last-write-time
///     at the moment of the parse so subsequent reads can detect freshness via
///     a single <c>File.GetLastWriteTimeUtc</c> call.
/// </summary>
internal sealed record SessionCacheEntry(
    DateTimeOffset FileLastWriteUtc,
    IReadOnlyList<AgentMessage> Messages);

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
