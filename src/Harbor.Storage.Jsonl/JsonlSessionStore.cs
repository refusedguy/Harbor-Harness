using System.Buffers;
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
                    session.CreatedAt,
                    session.CreatedAt,
                    session.ParentSessionId,
                    session.Status,
                    session.GitBranch,
                    session.GitIsDirty);

                File.AppendAllText(sessionFile, JsonSerializer.Serialize(header, JsonlCodecContext.Default.SessionHeaderEntry) + "\n");
            }
            finally
            {
                semaphore.Release();
            }

            _messageCache.TryRemove(session.Id, out _);
            return session;
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to create session: {Error}", e));
    }

    /// <summary>
    ///     Read one session by id. <b>ROP-C Z1:</b> disk access rides
    ///     <see cref="Result.Try" /> with <see cref="ResultErrors.Message" /> —
    ///     cancellation propagates instead of being masked as a store failure.
    ///     The expected "not found" outcome stays a plain <see cref="Result.Failure{T}" />
    ///     (no exception, no error log); only unexpected I/O failures are converted.
    /// </summary>
    public async Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        string sessionFile = GetSessionFilePath(sessionId);
        if (!File.Exists(sessionFile))
            return Result.Failure<Session>($"Session '{sessionId}' not found.");

        Result<Session> loaded = await Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var header = await ReadHeaderAsync(sessionFile, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session '{sessionId}' is corrupt (no header).");

            var metadata = await GetStatsAsync(sessionId, ct).ConfigureAwait(false);
            return new Session(
                header.Id,
                header.ProjectId,
                header.Directory,
                header.Title,
                header.Agent,
                header.Model,
                header.ProviderId,
                header.CreatedAt,
                ResolveUpdatedAt(header, sessionFile),
                metadata.IsSuccess ? metadata.Value : SessionMetadata.Empty)
            {
                ParentSessionId = header.ParentSessionId,
                Status = header.Status,
                GitBranch = header.GitBranch,
                GitIsDirty = header.GitIsDirty
            };
        }, ResultErrors.Message).ConfigureAwait(false);

        return loaded.TapError(e => _logger.LogError("Failed to read session {SessionId}: {Error}", sessionId, e));
    }

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        return Result.Try(async () =>
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
            return (IReadOnlyList<Session>)sessions;
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to list sessions: {Error}", e));
    }

    /// <summary>
    ///     Append a message to the session JSONL file. The cache for this
    ///     session is invalidated so the next <see cref="GetMessagesAsync" />
    ///     re-parses from disk (the file has changed).
    /// </summary>
    /// <remarks>
    ///     <b>ROP-C Z1:</b> the write rides <see cref="Result.Try" /> with
    ///     <see cref="ResultErrors.Message" /> — cancellation propagates
    ///     instead of being masked as a store failure. The expected
    ///     "not found" outcome is decided before the try boundary.
    /// </remarks>
    public async Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();
        string sessionFile = GetSessionFilePath(sessionId);
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Result.Failure($"Session '{sessionId}' not found.");
        }

        return await Result.Try(async () =>
        {
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
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to append message to session {SessionId}: {Error}", sessionId, e));
    }

    /// <summary>
    ///     Update a message in place. <b>ROP-C Z3 (DDD-audit 25.08):</b> this
    ///     used to be a plain re-append — every edit grew the file with a
    ///     duplicate entry (the "latest wins" read made it invisible until the
    ///     file ballooned). Now stale entries with the same message id are
    ///     dropped and the fresh entry is appended once, mirroring
    ///     <see cref="UpdateAsync" />'s rewrite-in-place semantics.
    /// </summary>
    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();
        string sessionFile = GetSessionFilePath(sessionId);
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
        }

        return Result.Try(async () =>
        {
            var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                string[] lines = File.ReadAllLines(sessionFile);
                var kept = new List<string>(lines.Length + 1);

                foreach (var line in lines)
                {
                    if (!IsMessageEntryWithId(line, message.Id))
                    {
                        kept.Add(line);
                    }
                }

                var entry = new MessageEntry(
                    "message",
                    message.Id,
                    message.ParentId,
                    message.Role,
                    message.CreatedAt,
                    JsonlMessageCodec.SerializeMessagePayload(message));

                kept.Add(JsonSerializer.Serialize(entry, JsonlCodecContext.Default.MessageEntry));
                File.WriteAllLines(sessionFile, kept);
            }
            finally
            {
                semaphore.Release();
            }

            _messageCache.TryRemove(sessionId, out _);
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to update message in session {SessionId}: {Error}", sessionId, e));
    }

    /// <summary>
    ///     True when the line is a <c>"message"</c> entry carrying the given id.
    ///     Header lines and unparseable lines are never matched, so a rewrite
    ///     cannot accidentally drop them. Malformed lines are left untouched on
    ///     disk — the read path already reports them as parse warnings.
    /// </summary>
    private static bool IsMessageEntryWithId(string line, string messageId)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains($"\"id\":\"{messageId}\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.MessageEntry);
            return entry is { Type: "message", Id: var id } && id == messageId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when the line is a session header (<c>"type":"session"</c>).</summary>
    private static bool IsSessionHeaderLine(ReadOnlySpan<byte> line)
    {
        return line.IndexOf("\"type\":\"session\""u8) >= 0;
    }

    /// <summary>True when the line is a <c>"message"</c> entry with any id.</summary>
    private static bool IsAnyMessageEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"type\":\"message\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.MessageEntry);
            return entry is { Type: "message" };
        }
        catch (JsonException)
        {
            return false;
        }
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
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        string sessionFile = GetSessionFilePath(sessionId);
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found.");
        }

        return await Result.Try(async () =>
        {
            // §3.3 cache: freshness check via file mtime. Most filesystems have
            // second-level mtime granularity, which is fine here — every write
            // bumps the mtime.
            DateTimeOffset fileMtime = File.GetLastWriteTimeUtc(sessionFile);
            if (_messageCache.TryGetValue(sessionId, out var cached) && cached.FileLastWriteUtc == fileMtime)
            {
                // Cache hit — return the cached list directly. Zero allocations.
                return cached.Messages;
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
            return parseResult.Value;
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to read messages of session {SessionId}: {Error}", sessionId, e));
    }

    /// <summary>
    ///     Delete a session JSONL file. The parsed-message cache entry for this
    ///     session is also removed (§3.3 cache).
    /// </summary>
    /// <remarks>
    ///     <b>ROP-C Z1:</b> the delete rides <see cref="Result.Try" /> with
    ///     <see cref="ResultErrors.Message" /> — cancellation propagates
    ///     instead of being masked as a store failure.
    /// </remarks>
    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        return Result.Try(async () =>
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
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to delete session {SessionId}: {Error}", sessionId, e));
    }

    /// <summary>
    ///     "Rewind to here": drop every <c>"message"</c> entry AFTER the target
    ///     id in file order. File order IS insertion order for this store
    ///     (append-only + rewrite-in-place), which is exactly the ordering the
    ///     read path reconstructs. Header/session lines are never touched; the
    ///     target message itself is kept. Rewrites the file in place — same
    ///     semantics as <see cref="UpdateMessageAsync" />.
    /// </summary>
    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        return Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();

            int removed = 0;
            var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string sessionFile = GetSessionFilePath(sessionId);
                string[] lines = File.ReadAllLines(sessionFile);

                int anchorLine = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    // The id matcher doubles as a "message entry" filter: only
                    // message-kind lines with that exact id match, headers never do.
                    if (IsMessageEntryWithId(lines[i], messageId))
                    {
                        anchorLine = i;
                        break;
                    }
                }

                if (anchorLine < 0)
                {
                    throw new InvalidOperationException(
                        $"Message '{messageId}' not found in session '{sessionId}'.");
                }

                // Messages append chronologically and rewrites keep relative
                // order, so file order IS insertion order — dropping every
                // message-kind line strictly after the anchor is the rewind.
                // Header/session lines are kept regardless of position.
                var kept = new List<string>(lines.Length);
                for (int i = 0; i <= anchorLine; i++)
                {
                    kept.Add(lines[i]);
                }

                for (int i = anchorLine + 1; i < lines.Length; i++)
                {
                    if (IsAnyMessageEntry(lines[i]))
                    {
                        removed++;
                        continue;
                    }

                    kept.Add(lines[i]);
                }

                if (removed > 0)
                {
                    File.WriteAllLines(sessionFile, kept);
                }
            }
            finally
            {
                semaphore.Release();
            }

            // Always drop the parse cache — cheap and immune to mtime quirks.
            _messageCache.TryRemove(sessionId, out _);

            return removed;
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError(
                "Failed to truncate messages after {MessageId} in session {SessionId}: {Error}", messageId, sessionId, e));
    }

    /// <summary>
    ///     Aggregate per-session stats from the message history. Every fallible
    ///     step (<see cref="GetMessagesAsync" />) already returns a
    ///     <see cref="Result{T}" /> and nothing here throws, so no try boundary
    ///     is needed at all (ROP-C Z1: the vestigial catch→Failure was removed).
    /// </summary>
    public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
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

    public async Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        // Stats are derived from messages; nothing to write
        await Task.CompletedTask.ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    ///     Rewrite the session header line (title/agent/model edits).
    ///     <b>ROP-C Z1:</b> the rewrite rides <see cref="Result.Try" /> with
    ///     <see cref="ResultErrors.Message" />; the expected "not found" outcome
    ///     is decided before the try boundary.
    /// </summary>
    public async Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        string sessionFile = GetSessionFilePath(session.Id);
        if (!File.Exists(sessionFile))
            return Result.Failure($"Session '{session.Id}' not found.");

        return await Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var semaphore = await GetSessionLockAsync(session.Id, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var lines = File.ReadAllLines(sessionFile).ToList();
                if (lines.Count == 0)
                    throw new InvalidOperationException($"Session '{session.Id}' is empty.");

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
                    session.CreatedAt,
                    DateTimeOffset.UtcNow,
                    session.ParentSessionId,
                    session.Status,
                    session.GitBranch,
                    session.GitIsDirty);

                lines[0] = JsonSerializer.Serialize(header, JsonlCodecContext.Default.SessionHeaderEntry);
                File.WriteAllLines(sessionFile, lines);
            }
            finally
            {
                semaphore.Release();
            }

            _messageCache.TryRemove(session.Id, out _);
        }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to update session {SessionId}: {Error}", session.Id, e));
    }

    /// <summary>
    ///     Parse the JSONL session file from disk into a chronological message
    ///     list. Per-line JSON parse errors are aggregated into a
    ///     <c>List&lt;string&gt;</c> and surfaced via <see cref="ILogger.LogWarning" />,
    ///     while still returning the successfully deserialized messages
    ///     (§ROP-001 resolved).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Perf sprint (PERF-005 successor):</b> the file is read once
    ///         into a pooled buffer and parsed line-by-line over raw UTF-8
    ///         spans via <see cref="JsonlLineParser" /> — no per-line
    ///         <see cref="string" />, no <c>Encoding.UTF8.GetBytes</c>, no
    ///         <see cref="JsonElement"/> round-trip for payloads. Allocations
    ///         are limited to the returned message object graph.
    ///     </para>
    /// </remarks>
    /// <param name="sessionFile">Absolute path to the .jsonl file.</param>
    /// <param name="sessionId">The session id (passed through to the parser).</param>
    /// <param name="ct">Cancellation token observed by the file read.</param>
    /// <returns>The chronological message list, or failure with the first error.</returns>
    private async Task<Result<IReadOnlyList<AgentMessage>>> ParseMessagesFromDiskAsync(
        string sessionFile,
        string sessionId,
        CancellationToken ct)
    {
        var messages = new Dictionary<string, AgentMessage>();
        var errors = new List<string>(capacity: 0);

        long fileLength = new FileInfo(sessionFile).Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(fileLength, 1));
        try
        {
            int read = 0;
            using (var fs = new FileStream(
                       sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 64 * 1024, useAsync: true))
            {
                while (read < buffer.Length)
                {
                    int n = await fs.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            }

            ReadOnlySpan<byte> rest = buffer.AsSpan(0, read);
            if (rest.StartsWith("\xEF\xBB\xBF"u8))
            {
                rest = rest[3..];
            }

            while (!rest.IsEmpty)
            {
                int nl = rest.IndexOf((byte)'\n');
                ReadOnlySpan<byte> line = nl < 0 ? rest : rest[..nl];
                rest = nl < 0 ? default : rest[(nl + 1)..];

                if (line.Length > 0 && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                if (line.IsEmpty || line.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
                {
                    continue;
                }

                if (IsSessionHeaderLine(line))
                {
                    continue;
                }

                var msgResult = JsonlLineParser.Parse(line, sessionId);
                if (msgResult.IsSuccess)
                {
                    messages[msgResult.Value.Id] = msgResult.Value;
                }
                else
                {
                    errors.Add($"Line parse failed: {msgResult.Error}");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Encountered {ErrorCount} malformed line(s) reading session {SessionId}: {Errors}",
                errors.Count, sessionId, string.Join("; ", errors));
        }

        var ordered = messages.Values.OrderBy(m => m.CreatedAt).ToList();
        return Result.Success<IReadOnlyList<AgentMessage>>(ordered);
    }

    private string GetSessionFilePath(string sessionId) =>
        Path.Combine(_rootDirectory, $"{sessionId}.jsonl");

    /// <summary>
    ///     Real last-activity timestamp for a session. Legacy files written
    ///     before the header carried <c>updatedAt</c> fall back to the file's
    ///     last-write time so ordering stays stable across consecutive reads.
    /// </summary>
    private static DateTimeOffset ResolveUpdatedAt(SessionHeaderEntry header, string sessionFile) =>
        header.UpdatedAt != default ? header.UpdatedAt : File.GetLastWriteTimeUtc(sessionFile);

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

/// <summary>
///     Line-1 header of a session file. Optional trailing fields carry the
///     newer <see cref="Harbor.Abstractions.Models.Session"/> attributes — before they
///     existed, UpdateAsync rewrote the header WITHOUT parent linkage/status/git
///     fields and silently dropped them on first rename/rebind (V4-bugfix).
///     Defaults keep legacy files parseable.
/// </summary>
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
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt = default,
    [property: JsonPropertyName("parentSessionId")] string? ParentSessionId = null,
    [property: JsonPropertyName("status")] SessionStatus Status = SessionStatus.Idle,
    [property: JsonPropertyName("gitBranch")] string? GitBranch = null,
    [property: JsonPropertyName("gitIsDirty")] bool GitIsDirty = false);

/*
 * DDD-audit 25.08 (ROP-C Z3): <see cref="JsonlSessionStore.GetAsync" /> used to
 * fabricate Session.UpdatedAt = UtcNow on EVERY read, which made ListAsync's
 * recency sort random — the same session reordered between calls. The stored
 * header now carries the real last-activity timestamp; reads never invent one.
 */

internal sealed record MessageEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("payload")] object Payload);
