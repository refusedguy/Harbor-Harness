using System.Collections.Concurrent;
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
    ///         <see cref="ConcurrentDictionary{TKey, TValue}" /> is safe for
    ///         concurrent readers — the value is an immutable record, so a
    ///         half-published update is impossible.
    ///     </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, SessionCacheEntry> _messageCache = new();

    public JsonlSessionStore(string rootDirectory, ILogger<JsonlSessionStore> logger)
    {
        _rootDirectory = rootDirectory;
        _logger = logger;

        if (!Directory.Exists(_rootDirectory))
        {
            Directory.CreateDirectory(_rootDirectory);
        }
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
    /// </remarks>
    public Task<Result<Session>> CreateAsync(
        string directory,
        string agentName,
        string providerId,
        string modelId,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var session = Session.Create(directory, agentName, providerId, modelId);
            string sessionFile = GetSessionFilePath(session.Id);

            lock (_lock)
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

                File.AppendAllText(sessionFile, JsonSerializer.Serialize(header, JsonOptions) + "\n");
            }

            // New session — no cache entry to invalidate, but be defensive.
            _messageCache.TryRemove(session.Id, out _);
            return Task.FromResult(Result.Success(session));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Result.Failure<Session>("Operation was cancelled."));
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
    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string sessionFile = GetSessionFilePath(sessionId);
            if (!File.Exists(sessionFile))
            {
                // Invalidate any stale cache entry for the missing session.
                _messageCache.TryRemove(sessionId, out _);
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
            }

            lock (_lock)
            {
                // Re-check cancellation after acquiring the lock — the wait may
                // have been long under contention.
                ct.ThrowIfCancellationRequested();

                var entry = new MessageEntry(
                    "message",
                    message.Id,
                    message.ParentId,
                    message.Role,
                    message.CreatedAt,
                    JsonlMessageCodec.SerializeMessagePayload(message));

                File.AppendAllText(sessionFile, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            }

            // Invalidate the parsed-message cache for this session. The next
            // GetMessagesAsync call will re-parse from disk (the file has
            // changed). Other sessions' cache entries are untouched.
            _messageCache.TryRemove(sessionId, out _);
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Result.Failure("Operation was cancelled."));
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
    ///         <b>TODO(principles)[PERF]:</b> на каждый чанк строки вызывается
    ///         <c>JsonDocument.Parse(line)</c> — тысячи аллокаций. Для длинных
    ///         сессий (10k+ строк) это существенный overhead даже с кэшем (первый
    ///         reads всё ещё pays full parse). Альтернативы: (1) Utf8JsonReader
    ///         на ReadOnlySpan&lt;byte&gt;, (2) MemoryPack-encoded binary формат
    ///         вместо JSONL (есть же [MemoryPackable] на всех сообщениях!),
    ///         (3) streaming deserialize. См. аудит §PERF-005.
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
        catch (OperationCanceledException)
        {
            return Result.Failure<IReadOnlyList<AgentMessage>>("Operation was cancelled.");
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<AgentMessage>>(ex.Message);
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
                    var msgResult = JsonlMessageCodec.DeserializeMessage(sessionId, doc.RootElement);
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
    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // Always invalidate the cache — even if the file is missing, a
            // stale cache entry should not survive a DeleteAsync call.
            _messageCache.TryRemove(sessionId, out _);

            string sessionFile = GetSessionFilePath(sessionId);
            if (File.Exists(sessionFile))
            {
                File.Delete(sessionFile);
            }
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Result.Failure("Operation was cancelled."));
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

}

/// <summary>
///     Parsed-message cache entry (§3.3). Records the file's last-write-time
/// at the moment of the parse so subsequent reads can detect freshness via
/// a single <c>File.GetLastWriteTimeUtc</c> call.
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
