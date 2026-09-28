using System.Collections.Concurrent;
using System.Text;
using Harbor.Storage.Shared;
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
///         before each <c>File.*</c> call; appends (#177) additionally pass the
///         token to <c>File.AppendAllBytesAsync</c>, so cancellation is observed
///         during the write itself, not just before it.
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

    private ValueTask<SemaphoreSlim> GetSessionLockAsync(string sessionId, CancellationToken ct) =>
        // Per-session strip shared with SqliteSessionStore (#184) — same
        // granularity as the inline GetOrAdd this replaced.
        SessionLockStrip.AcquireAsync(_sessionLocks, sessionId, ct);

    /// <summary>
    ///     Create a new session and write its header to the JSONL file.
    /// </summary>
    /// <remarks>
    ///     <b>CT note (§3.4):</b> the supplied <paramref name="ct" /> is
    ///     observed via <see cref="CancellationToken.ThrowIfCancellationRequested" />
    ///     before the directory-create and file-write.
    ///     <c>Directory.CreateDirectory</c> is synchronous I/O that does not accept
    ///     a CT; the header append itself is CT-aware (<c>File.AppendAllBytesAsync</c>).
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
            string sessionFile = SessionFilePaths.GetSessionFilePath(_rootDirectory, session.Id);

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
                    session.GitIsDirty,
                    session.Kind);

                // #177: source-gen straight to UTF-8 bytes + newline byte, async
                // append — no intermediate string, no Serialize + concat.
                byte[] line = SessionFileIO.EncodeLine(header, JsonlCodecContext.Default.SessionHeaderEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
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
    ///     A missing/unparseable header is likewise a failure naming the session,
    ///     the reason, and the file path (see <see cref="SessionFileReader.TryReadHeaderAsync" />).
    /// </summary>
    public async Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Result.Failure<Session>(resolved.Error);
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        if (!File.Exists(sessionFile))
            return Result.Failure<Session>(SessionStoreErrors.SessionNotFound(sessionId));

        // #199: header decode is its own Result step — corrupt/empty files
        // surface as failures (session + reason + path), never throws.
        var headerResult = await SessionFileReader.TryReadHeaderAsync(sessionFile, sessionId, ct).ConfigureAwait(false);
        if (headerResult.IsFailure)
            return Result.Failure<Session>(headerResult.Error);
        SessionHeaderEntry header = headerResult.Value; // guarded: returned above on failure.

        Result<Session> loaded = await Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();

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
                SessionFileIO.ResolveUpdatedAt(header, sessionFile),
                metadata.IsSuccess ? metadata.Value : SessionMetadata.Empty)
            {
                ParentSessionId = header.ParentSessionId,
                Status = header.Status,
                GitBranch = header.GitBranch,
                GitIsDirty = header.GitIsDirty,
                Kind = header.Kind
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
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Result.Failure(resolved.Error);
        string sessionFile = resolved.Value;
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Result.Failure(SessionStoreErrors.SessionNotFound(sessionId));
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

                // #177: source-gen straight to UTF-8 bytes + newline byte, async
                // append — no intermediate string, no Serialize + concat.
                byte[] line = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
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
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure(resolved.Error));
        string sessionFile = resolved.Value;
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));
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
                bool found = false;

                foreach (var line in lines)
                {
                    if (SessionFileReader.IsMessageEntryWithId(line, message.Id))
                    {
                        found = true;
                        continue;
                    }
                    kept.Add(line);
                }

                // #199: absence is a Result outcome, not a throw — the Bind
                // below maps it to "not found" with the session + message ids.
                if (!found)
                    return false;

                var entry = new MessageEntry(
                    "message",
                    message.Id,
                    message.ParentId,
                    message.Role,
                    message.CreatedAt,
                    JsonlMessageCodec.SerializeMessagePayload(message));

                // #177: pre-serialized UTF-8 entry + line-wise atomic rewrite — no
                // List<string> join of the new entry.
                byte[] entryBytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);
                SessionFileIO.WriteLinesAtomic(sessionFile, kept, entryBytes);
                return true;
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .Bind(wrote => wrote
                ? Result.Success()
                : Result.Failure(SessionStoreErrors.MessageNotFound(sessionId, message.Id)))
            .Tap(() => _messageCache.TryRemove(sessionId, out _))
            .TapError(e => _logger.LogError("Failed to update message in session {SessionId}: {Error}", sessionId, e));
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
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Result.Failure<IReadOnlyList<AgentMessage>>(resolved.Error);
        string sessionFile = resolved.Value;
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Result.Failure<IReadOnlyList<AgentMessage>>(SessionStoreErrors.SessionNotFound(sessionId));
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
                return Result.Success(cached.Messages);
            }

            // Cache miss (or stale) — parse from disk.
            // #199: the parse outcome is itself a Result; a failure (e.g. an
            // unbounded file) travels the rail via the Bind below instead of
            // crashing on an unguarded .Value.
            var parseResult = await SessionFileReader.ParseMessagesFromDiskAsync(sessionFile, sessionId, _logger, ct).ConfigureAwait(false);
            if (parseResult.IsFailure)
                return parseResult;

            // Publish the freshly parsed list to the cache. The
            // ConcurrentDictionary slot is updated atomically and the
            // cache value is an immutable record, so concurrent readers
            // see either the old entry or the new entry but never a
            // half-built one.
            // (parseResult.Value is guarded by the failure check above.)
            _messageCache[sessionId] = new SessionCacheEntry(fileMtime, parseResult.Value);
            return parseResult;
        }, ResultErrors.Message)
            .Bind(x => x)
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
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure(resolved.Error));
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        // #199: the expected "not found" outcome is decided before the try
        // boundary so it stays a plain failure (no throw, no error log).
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));
        }
        return Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();
            _messageCache.TryRemove(sessionId, out _);

            var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-checked under the lock: a concurrent delete winning the
                // race already achieved the goal, so that is success, not loss.
                if (File.Exists(sessionFile))
                {
                    File.Delete(sessionFile);
                }
                SessionLockStrip.Evict(_sessionLocks, sessionId);
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
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure<int>(resolved.Error));
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        // #199: the expected "not found" outcome is decided before the try
        // boundary so it stays a plain failure (no throw, no error log).
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Task.FromResult(Result.Failure<int>(SessionStoreErrors.SessionNotFound(sessionId)));
        }
        return Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();

            var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string[] lines = File.ReadAllLines(sessionFile);

                int anchorLine = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    // The id matcher doubles as a "message entry" filter: only
                    // message-kind lines with that exact id match, headers never do.
                    if (SessionFileReader.IsMessageEntryWithId(lines[i], messageId))
                    {
                        anchorLine = i;
                        break;
                    }
                }

                // #199: absence is a Result outcome, not a throw — the Bind
                // below maps it to "not found" with the session + message ids.
                if (anchorLine < 0)
                    return (Found: false, Removed: 0);

                // Messages append chronologically and rewrites keep relative
                // order, so file order IS insertion order — dropping every
                // message-kind line strictly after the anchor is the rewind.
                // Header/session lines are kept regardless of position.
                var kept = new List<string>(lines.Length);
                for (int i = 0; i <= anchorLine; i++)
                {
                    kept.Add(lines[i]);
                }

                int removed = 0;
                for (int i = anchorLine + 1; i < lines.Length; i++)
                {
                    if (SessionFileReader.IsAnyMessageEntry(lines[i]))
                    {
                        removed++;
                        continue;
                    }

                    kept.Add(lines[i]);
                }

                if (removed > 0)
                {
                    SessionFileIO.WriteAllLinesAtomic(sessionFile, kept);
                }

                return (Found: true, Removed: removed);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .Bind(t => t.Found
                ? Result.Success(t.Removed)
                : Result.Failure<int>(SessionStoreErrors.MessageNotFound(sessionId, messageId)))
            // Always drop the parse cache on success — cheap and immune to mtime quirks.
            .Tap(() => _messageCache.TryRemove(sessionId, out _))
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

        // Fold lives in the shared aggregator (#184) — same derive semantics.
        return Result.Success(SessionStatsAggregator.Aggregate(messagesResult.Value));
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
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, session.Id);
        if (resolved.IsFailure)
            return Result.Failure(resolved.Error);
        string sessionFile = resolved.Value;
        if (!File.Exists(sessionFile))
            return Result.Failure(SessionStoreErrors.SessionNotFound(session.Id));

        return await Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var semaphore = await GetSessionLockAsync(session.Id, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var lines = File.ReadAllLines(sessionFile).ToList();
                // #199: an empty file is a Result outcome, not a throw — the
                // Bind below maps it to a failure naming the session + path.
                if (lines.Count == 0)
                    return false;

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
                    session.GitIsDirty,
                    session.Kind);

                lines[0] = JsonSerializer.Serialize(header, JsonlCodecContext.Default.SessionHeaderEntry);
                SessionFileIO.WriteAllLinesAtomic(sessionFile, lines);
                return true;
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .Bind(wrote => wrote
                ? Result.Success()
                : Result.Failure($"Session '{session.Id}' is empty: {sessionFile}."))
            .Tap(() => _messageCache.TryRemove(session.Id, out _))
            .TapError(e => _logger.LogError("Failed to update session {SessionId}: {Error}", session.Id, e));
    }
}
