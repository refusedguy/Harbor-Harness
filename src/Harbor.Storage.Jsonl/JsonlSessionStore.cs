using System.Collections.Concurrent;
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
///         file's <c>(mtime, length)</c> identity; <see cref="AppendMessageAsync" />
///         invalidates just the affected session's entry.
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
public sealed partial class JsonlSessionStore : ISessionStore, ISessionCheckpointStore, ISessionFeedbackStore, ICompactionEventStore
{
    // JSONL codec context provides AOT-safe serialization via JsonTypeInfo.
    // See JsonlCodecContext.cs for the registered types.
    private static readonly JsonSerializerOptions JsonOptions = JsonlCodecContext.JsonOptions;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly ILogger<JsonlSessionStore> _logger;

    // #1513 (#95 slice): the checkpoint/rewind axis lives in
    // JsonlCheckpointStore (verbatim move); this store keeps thin forwarders
    // so the ISessionCheckpointStore impl site stays where it is.
    private readonly JsonlCheckpointStore _checkpoints;

    // #1746: the feedback axis lives in JsonlFeedbackStore; the store keeps
    // the forwarders so the ISessionFeedbackStore impl site stays here, the
    // way the checkpoint axis does.
    private readonly JsonlFeedbackStore _feedback;

    /// <summary>
    ///     Parsed-message cache. Architecture audit v2 §3.3: keyed by session id,
    ///     value is an immutable <see cref="SessionCacheEntry" /> recording the
    ///     <c>(mtime, length)</c> identity the parse was taken from and the
    ///     parsed message list. Reads check the cache for a freshness hit
    ///     (identity unchanged) before falling through to a full disk
    ///     re-parse. Writes invalidate just the affected session's entry, so
    ///     concurrent reads of other sessions are unaffected. Both identity
    ///     fields matter — mtime alone is not a safe key (#459).
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

    /// <summary>
    ///     The tier this store writes in (#1740). The default tier is the
    ///     local one, where nothing is scrubbed — the behaviour every session
    ///     log had before the tier existed.
    /// </summary>
    private readonly SessionLogTier _tier;

    /// <summary>
    ///     The scrubber the keyless tier writes through (#1740). It is built
    ///     once, in the constructor: the set of literal secrets it knows is
    ///     fixed for the life of the store.
    /// </summary>
    private readonly SecretRedactor _redactor;

    public JsonlSessionStore(string rootDirectory, ILogger<JsonlSessionStore> logger)
        : this(rootDirectory, logger, SessionLogTier.Local, null)
    {
    }

    /// <summary>
    ///     Construct the store.
    /// </summary>
    /// <param name="rootDirectory">The directory the session files live in.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tier">
    ///     The tier the logs are written in. <see cref="SessionLogTier.Keyless" />
    ///     scrubs provider credentials out of every text-bearing field before
    ///     it reaches the file (#1740).
    /// </param>
    /// <param name="literalSecrets">
    ///     The credential values the process holds. The keyless tier replaces
    ///     every occurrence of them, which is what removes a provider key
    ///     whose shape no rule could recognise.
    /// </param>
    public JsonlSessionStore(
        string rootDirectory,
        ILogger<JsonlSessionStore> logger,
        SessionLogTier tier,
        IReadOnlyCollection<string>? literalSecrets = null)
    {
        _rootDirectory = rootDirectory;
        _logger = logger;
        _tier = tier;
        _redactor = new SecretRedactor(literalSecrets);
        _checkpoints = new JsonlCheckpointStore(this, rootDirectory, logger);
        _feedback = new JsonlFeedbackStore(this, rootDirectory, logger);

        if (!Directory.Exists(_rootDirectory))
        {
            Directory.CreateDirectory(_rootDirectory);
        }
    }

    /// <summary>
    ///     The marker the header carries for this store's tier, or null for
    ///     the local tier — whose header keeps the bytes it always had.
    /// </summary>
    private string? TierMarker() =>
        _tier == SessionLogTier.Keyless ? SessionLogTierMarkers.Keyless : null;

    /// <summary>
    ///     The message to write. In the keyless tier the text-bearing fields
    ///     are scrubbed HERE, on the way out, so the agent keeps working with
    ///     the real text in memory and only the file changes (#1740).
    /// </summary>
    private AgentMessage ForWrite(AgentMessage message) =>
        _tier == SessionLogTier.Keyless ? _redactor.Scrub(message) : message;

    /// <summary>
    ///     The text to write, scrubbed in the keyless tier (#1740). The title
    ///     of a session is written by the caller and can be anything the
    ///     conversation was about, so it is scrubbed like every other
    ///     text-bearing field.
    /// </summary>
    internal string ScrubTextForWrite(string text) =>
        _tier == SessionLogTier.Keyless ? _redactor.ScrubText(text) : text;

    /// <summary>
    ///     The optional text to write, scrubbed in the keyless tier (#1740).
    /// </summary>
    internal string? ScrubOptionalTextForWrite(string? text) =>
        text is null ? null : ScrubTextForWrite(text);

    /// <summary>
    ///     Per-session mutex. <b>#1513:</b> shared with the checkpoint axis —
    ///     it is the same semaphore, so a checkpoint append still serializes
    ///     against a message append on the same session.
    /// </summary>
    internal ValueTask<SemaphoreSlim> GetSessionLockAsync(string sessionId, CancellationToken ct) =>
        // Per-session strip shared with SqliteSessionStore (#184) — same
        // granularity as the inline GetOrAdd this replaced.
        SessionLockStrip.AcquireAsync(_sessionLocks, sessionId, ct);

    /// <summary>
    ///     Drop the parsed-message cache entry for one session (the file
    ///     changed). <b>#1513:</b> shared with the checkpoint axis, so a
    ///     checkpoint write invalidates exactly the entry a message write
    ///     does.
    /// </summary>
    internal void InvalidateMessageCache(string sessionId) =>
        _messageCache.TryRemove(sessionId, out _);

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
                    SessionFormatCatalog.Current,
                    session.Id,
                    session.ProjectId,
                    session.Directory,
                    ScrubTextForWrite(session.Title),
                    session.Agent,
                    session.Model,
                    session.ProviderId,
                    session.CreatedAt,
                    session.CreatedAt,
                    session.ParentSessionId,
                    session.Status,
                    session.GitBranch,
                    session.GitIsDirty,
                    session.Kind,
                    TierMarker());

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
            .TapError(e => JsonlSessionStoreLog.CreateSessionFailed(_logger, e));
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
            return resolved.ConvertFailure<Session>();
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        if (!File.Exists(sessionFile))
            return Result.Failure<Session>(SessionStoreErrors.SessionNotFound(sessionId));

        // #199: header decode is its own Result step — corrupt/empty files
        // surface as failures (session + reason + path), never throws.
        var headerResult = await SessionFileReader.TryReadHeaderAsync(sessionFile, sessionId, ct).ConfigureAwait(false);
        if (headerResult.IsFailure)
            return headerResult.ConvertFailure<Session>();
        SessionHeaderEntry header = headerResult.Value; // guarded: returned above on failure.

        Result<Session> loaded = await Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();

            var metadata = await GetStatsAsync(sessionId, ct).ConfigureAwait(false);
            return Session.Rehydrate(
                id: header.Id,
                projectId: header.ProjectId,
                directory: header.Directory,
                title: header.Title,
                agent: header.Agent,
                model: header.Model,
                providerId: header.ProviderId,
                createdAt: header.CreatedAt,
                updatedAt: SessionFileIO.ResolveUpdatedAt(header, sessionFile),
                metadata: metadata.IsSuccess ? metadata.Value : SessionMetadata.Empty,
                parentSessionId: header.ParentSessionId,
                status: header.Status,
                gitBranch: header.GitBranch,
                gitIsDirty: header.GitIsDirty,
                kind: header.Kind);
        }, ResultErrors.Message).ConfigureAwait(false);

        return loaded.TapError(e => JsonlSessionStoreLog.ReadSessionFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     List sessions header-only (#1101 N+1): one header-line read per
    ///     <c>.jsonl</c> file, no <see cref="GetMessagesAsync" /> reparse.
    ///     <see cref="GetAsync" /> derives <see cref="SessionMetadata" /> from
    ///     history, so routing <c>List</c> through it paid a full file parse
    ///     per session. Callers (session picker, lifecycle switch, IPC list)
    ///     only need identity + recency — metadata stays
    ///     <see cref="SessionMetadata.Empty" /> here, <see cref="GetStatsAsync" />
    ///     remains the derived-stats path.
    /// </summary>
    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        return Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var sessions = new List<Session>();
            foreach (string file in Directory.EnumerateFiles(_rootDirectory, "*.jsonl"))
            {
                ct.ThrowIfCancellationRequested();
                string sessionId = Path.GetFileNameWithoutExtension(file);
                var headerResult = await SessionFileReader.TryReadHeaderAsync(file, sessionId, ct).ConfigureAwait(false);
                if (headerResult.IsFailure)
                {
                    // #1749: a file this build cannot read is not skipped in
                    // silence. The reason travels into the log — a session in
                    // a format generation this build does not read is exactly
                    // the case a reader must be told about, not the case a
                    // reader may forget.
                    JsonlSessionStoreLog.SessionHeaderUnreadable(_logger, sessionId, headerResult.Error);
                    continue;
                }
#pragma warning disable CFE0001
                // CFE0001: false positive — the IsFailure guard above continues,
                // a control-flow shape the analyzer does not model.
                SessionHeaderEntry header = headerResult.Value;
#pragma warning restore CFE0001
                if (projectId is not null && header.ProjectId != projectId)
                    continue;
                sessions.Add(Session.Rehydrate(
                    id: header.Id,
                    projectId: header.ProjectId,
                    directory: header.Directory,
                    title: header.Title,
                    agent: header.Agent,
                    model: header.Model,
                    providerId: header.ProviderId,
                    createdAt: header.CreatedAt,
                    updatedAt: SessionFileIO.ResolveUpdatedAt(header, file),
                    metadata: SessionMetadata.Empty,
                    parentSessionId: header.ParentSessionId,
                    status: header.Status,
                    gitBranch: header.GitBranch,
                    gitIsDirty: header.GitIsDirty,
                    kind: header.Kind));
            }

            sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return (IReadOnlyList<Session>)sessions;
        }, ResultErrors.Message)
            .TapError(e => JsonlSessionStoreLog.ListSessionsFailed(_logger, e));
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
            return resolved.ConvertFailure();
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
                    JsonlMessageCodec.SerializeMessagePayload(ForWrite(message)));

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
            .TapError(e => JsonlSessionStoreLog.AppendMessageFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Update a message IN PLACE: the fresh record takes the position the
    ///     stale one held, so "file order = insertion order" survives an edit
    ///     (<b>#1798</b>). <b>ROP-C Z3 (DDD-audit 25.08):</b> this used to be a
    ///     plain re-append — every edit grew the file with a duplicate entry
    ///     (the "latest wins" read made it invisible until the file ballooned)
    ///     — and then a drop-and-append rewrite, which dropped the stale
    ///     entry and appended the fresh one at the END of the file. That
    ///     second shape kept the file free of duplicates but MOVED the record:
    ///     the edited message jumped to the tail of the history, and a rewind
    ///     anchored on it (<see cref="DeleteMessagesAfterAsync" />) removed
    ///     nothing, because the anchor was the last record in the file. The
    ///     record now keeps its position, and a stale duplicate of the same id
    ///     (a file written before the duplicate-drop, or an append that raced
    ///     the edit) is still dropped, so the file holds one record per id.
    /// </summary>
    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Task.FromResult(resolved.ConvertFailure());
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

                var entry = new MessageEntry(
                    "message",
                    message.Id,
                    message.ParentId,
                    message.Role,
                    message.CreatedAt,
                    JsonlMessageCodec.SerializeMessagePayload(ForWrite(message)));

                // #177: pre-serialized UTF-8 entry + line-wise atomic rewrite —
                // no List<string> join of the new entry.
                byte[] entryBytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);

                // #460: streamed record by record into the temp sibling, so
                // dropping the stale entry no longer reads the whole session
                // into a List<string> (twice) under this semaphore. False means
                // the id was not in the file: the rewrite is decided before any
                // temp is created, so the original — and its directory's mtime —
                // are left exactly as they were, which is what makes this a
                // "not found" outcome instead of an appended duplicate.
                //
                // #1798: the fresh record REPLACES the stale one where it
                // stands. The rewrite is the same one it always was (a full
                // atomic copy of the file), so the cost of an update does not
                // change — only the position of the record does.
                return SessionFileIO.RewriteRecordsAtomic(
                    sessionFile,
                    new ReplaceMessagePlan(message.Id, entryBytes));
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
            .TapError(e => JsonlSessionStoreLog.UpdateMessageFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Read all messages for a session in chronological order. Returns the
    ///     cached parse result when the file's <c>(mtime, length)</c> is
    ///     unchanged since the prior call (§3.3 cache).
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
    ///         <b>#459:</b> freshness is keyed on <c>(mtime, length)</c>, not on
    ///         mtime alone, and only a snapshot the reader marked
    ///         <see cref="MessageReadSnapshot.IsStable" /> is published. A read
    ///         that raced a concurrent append used to hand back a list missing
    ///         the record that was being written, and it was cached under the
    ///         pre-append timestamp — so the message stayed invisible until the
    ///         next write or a restart. The short list is still returned (it is
    ///         a consistent prefix, never a record cut in half), it is just not
    ///         remembered.
    ///     </para>
    ///     <para>
    ///         <b>#1744:</b> this returns the LOG's messages — what the file
    ///         holds, unaltered. The reader's view of the same read (the
    ///         transcript with the interrupted turns closed in memory, and what
    ///         the read could not deliver) is <see cref="GetTranscriptAsync" />.
    ///         The two are kept apart on purpose: a fork copies these messages
    ///         and the stats fold over them, so nothing may be added to them
    ///         behind the store's back.
    ///     </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        var read = await ReadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (read.IsFailure)
            return read.ConvertFailure<IReadOnlyList<AgentMessage>>();

        // (read.Value is guarded by the failure check above.)
        var messages = read.Value.Messages;
        return Result.Success(messages);
    }

    /// <summary>
    ///     Read the session's transcript: the messages a reader gets — with the
    ///     turns the log never closed closed in memory — together with what the
    ///     read could not deliver (#1744).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The read never fails on a broken record and never drops one
    ///         silently: a record whose bytes are not a complete document is
    ///         skipped (its content is unknowable) and COUNTED, and the turns
    ///         the log never closed are closed here, in memory, so the
    ///         transcript a reader gets is balanced and says what it could not
    ///         deliver. Nothing is written back.
    ///     </para>
    ///     <para>
    ///         The transcript and the integrity come from ONE pass over the
    ///         file — a second read would be a second generation, and the two
    ///         halves could describe different moments.
    ///     </para>
    /// </remarks>
    public async Task<Result<SessionTranscript>> GetTranscriptAsync(string sessionId, CancellationToken ct = default)
    {
        var read = await ReadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (read.IsFailure)
            return read.ConvertFailure<SessionTranscript>();

        // (read.Value is guarded by the failure check above.)
        var transcript = read.Value.Transcript;
        return Result.Success(transcript);
    }

    /// <summary>
    ///     ONE cold read of a session: the log's messages and the reader's view
    ///     of the same read, from the same pass over the file (#1744).
    /// </summary>
    private async Task<Result<SessionReadResult>> ReadSessionAsync(string sessionId, CancellationToken ct)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return resolved.ConvertFailure<SessionReadResult>();
        string sessionFile = resolved.Value;
        if (!File.Exists(sessionFile))
        {
            _messageCache.TryRemove(sessionId, out _);
            return Result.Failure<SessionReadResult>(SessionStoreErrors.SessionNotFound(sessionId));
        }

        return await Result.Try(async () =>
        {
            // §3.3 cache: freshness check against the file's identity. One
            // FileInfo yields mtime AND length from a single stat, so widening
            // the key past mtime costs nothing.
            var stat = SessionFileStat.Read(sessionFile);
            if (_messageCache.TryGetValue(sessionId, out var cached) && cached.IsFreshFor(stat))
            {
                // Cache hit — return the cached list directly. Zero allocations.
                return Result.Success(new SessionReadResult(cached.Messages, cached.Transcript));
            }

            // Cache miss (or stale) — parse from disk.
            // #199: the parse outcome is itself a Result; a failure (e.g. an
            // unbounded file) travels the rail via the Bind below instead of
            // crashing on an unguarded .Value.
            var parseResult = await SessionFileReader.ParseMessagesFromDiskAsync(sessionFile, sessionId, _logger, ct).ConfigureAwait(false);
            if (parseResult.IsFailure)
                return parseResult.ConvertFailure<SessionReadResult>();

            // (parseResult.Value is guarded by the failure check above.)
            var snapshot = parseResult.Value;
            var transcript = BuildTranscript(snapshot);
            if (snapshot.IsStable)
            {
                // The ConcurrentDictionary slot is updated atomically and the
                // cache value is an immutable record, so concurrent readers see
                // either the old entry or the new entry but never a half-built
                // one.
                _messageCache[sessionId] = new SessionCacheEntry(
                    snapshot.Stat.LastWriteUtc, snapshot.Stat.Length, snapshot.Messages, transcript);
            }
            else
            {
                // #459: the file moved under the read, so these messages are a
                // prefix of an unknown generation. Evicting any older entry
                // forces the next caller to re-read instead of inheriting a
                // list we cannot vouch for.
                _messageCache.TryRemove(sessionId, out _);
            }

            return Result.Success(new SessionReadResult(snapshot.Messages, transcript));
        }, ResultErrors.Message)
            .Bind(x => x)
            .TapError(e => JsonlSessionStoreLog.ReadMessagesFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     The reader's view of one read: the messages with the turns the log
    ///     never closed closed IN MEMORY, plus the honest statement of what
    ///     the read could not deliver (#1744).
    /// </summary>
    private static SessionTranscript BuildTranscript(MessageReadSnapshot snapshot)
    {
        var unclosed = TranscriptInspector.FindUnclosedToolCalls(snapshot.Messages);
        var integrity = new TranscriptIntegrity(
            snapshot.UnreadableRecordCount,
            snapshot.IsTailTruncated,
            unclosed);
        var balanced = TranscriptInspector.CloseUnclosedToolCalls(snapshot.Messages);
        return new SessionTranscript(balanced, integrity);
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
            return Task.FromResult(resolved.ConvertFailure());
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
            .TapError(e => JsonlSessionStoreLog.DeleteSessionFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     "Rewind to here": drop every <c>"message"</c> entry AFTER the target
    ///     id in file order. File order IS insertion order for this store
    ///     (append-only + rewrite-in-place — <see cref="UpdateMessageAsync" />
    ///     keeps an edited record at its position, <b>#1798</b>), which is
    ///     exactly the ordering the read path reconstructs. Header/session lines
    ///     are never touched; the target message itself is kept. Rewrites the
    ///     file in place — same semantics as
    ///     <see cref="UpdateMessageAsync" />.
    /// </summary>
    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Task.FromResult(resolved.ConvertFailure<int>());
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
                // #460: streamed. The plan walks the file, keeping every record
                // up to the anchor and dropping every message record after it,
                // so nothing but the current record is ever in memory. The
                // anchor is the FIRST id match and file order is insertion
                // order for this store, which makes a single pass exact — the
                // old code got the same answer from an index scan over a
                // List<string> of the whole file, held under this semaphore.
                //
                // The rewrite hands back the plan that carried the counts, so
                // these are read off the object the decision was made on:
                // reaching the last message still reports a successful rewind of
                // zero messages and leaves the file (and its mtime) alone, which
                // is the pre-#460 behaviour where the rewrite was skipped when
                // nothing was actually removed.
                var plan = SessionFileIO.RewriteRecords(
                    sessionFile, new DeleteAfterAnchorPlan(messageId));
                return (Found: plan.Found, Removed: plan.Removed);
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
            .TapError(e => JsonlSessionStoreLog.TruncateMessagesFailed(_logger, messageId, sessionId, e));
    }

    /// <summary>
    ///     Mark a checkpoint over a message (#1247): the anchor's zero-based
    ///     index in message order is recorded as a <c>"type":"checkpoint"</c>
    ///     line appended to the session file.
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="JsonlCheckpointStore" /> (#1513, #95 slice:
    ///     verbatim move) — the checkpoint axis has one home; the
    ///     <see cref="ISessionCheckpointStore" /> impl site stays here.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Anchor message id to mark.</param>
    /// <param name="label">Optional human label.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded checkpoint, or failure if session/message not found.</returns>
    public Task<Result<SessionCheckpoint>> CreateCheckpointAsync(
        string sessionId, string messageId, string? label = null, CancellationToken ct = default) =>
        _checkpoints.CreateCheckpointAsync(sessionId, messageId, label, ct);

    /// <summary>
    ///     List checkpoints (and rewind trail markers) for a session in file
    ///     order (#1247). Unparseable checkpoint lines are skipped with a
    ///     warning, never a whole-list failure (§ROP-001).
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="JsonlCheckpointStore" /> (#1513, #95 slice:
    ///     verbatim move).
    /// </remarks>
    public Task<Result<IReadOnlyList<SessionCheckpoint>>> ListCheckpointsAsync(
        string sessionId, CancellationToken ct = default) =>
        _checkpoints.ListCheckpointsAsync(sessionId, ct);

    /// <summary>
    ///     Rewind a session to a checkpoint (#1247): the session is truncated
    ///     to the checkpoint's anchor message and a trail marker is appended
    ///     — the honest trail: which checkpoint was restored, how many
    ///     messages were dropped, when.
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="JsonlCheckpointStore" /> (#1513, #95 slice:
    ///     verbatim move).
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="checkpointId">Id of the checkpoint to restore (from <see cref="ListCheckpointsAsync" />).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many messages were dropped and how many remain.</returns>
    public Task<Result<SessionRewindOutcome>> RewindToCheckpointAsync(
        string sessionId, string checkpointId, CancellationToken ct = default) =>
        _checkpoints.RewindToCheckpointAsync(sessionId, checkpointId, ct);

    /// <summary>
    ///     Record the user's rating of one message (#1746): the rating line
    ///     is appended to the session file, so it survives a restart and
    ///     stays out of the message history the model reads.
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="JsonlFeedbackStore" /> — the feedback
    ///     axis has one home; the <see cref="ISessionFeedbackStore" /> impl
    ///     site stays here.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">The rated message id.</param>
    /// <param name="rating">The value the user gave.</param>
    /// <param name="category">The axis the value was given on.</param>
    /// <param name="note">Optional free-text note from the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded rating, or failure if the session/message is not found.</returns>
    public Task<Result<MessageRating>> RecordMessageRatingAsync(
        string sessionId,
        string messageId,
        FeedbackRating rating,
        FeedbackCategory category,
        string? note = null,
        CancellationToken ct = default) =>
        _feedback.RecordMessageRatingAsync(sessionId, messageId, rating, category, note, ct);

    /// <summary>
    ///     Record a remark about the session as a whole (#1746): the remark
    ///     line is appended to the session file, so it survives a restart
    ///     and stays out of the message history the model reads.
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="JsonlFeedbackStore" /> — the feedback
    ///     axis has one home; the <see cref="ISessionFeedbackStore" /> impl
    ///     site stays here.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="text">The user's text; it must not be blank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded remark, or failure if the session is not found.</returns>
    public Task<Result<SessionRemark>> RecordSessionRemarkAsync(
        string sessionId,
        string text,
        CancellationToken ct = default) =>
        _feedback.RecordSessionRemarkAsync(sessionId, text, ct);

    /// <summary>
    ///     Read the session's feedback (#1746): the ratings in force and the
    ///     remarks, both recorded in the session file and both invisible to
    ///     the message history.
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="JsonlFeedbackStore" /> — the feedback
    ///     axis has one home; the <see cref="ISessionFeedbackStore" /> impl
    ///     site stays here.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The feedback, or failure if the session is not found.</returns>
    public Task<Result<SessionFeedback>> GetFeedbackAsync(
        string sessionId,
        CancellationToken ct = default) =>
        _feedback.GetFeedbackAsync(sessionId, ct);

    /// <summary>
    ///     Aggregate per-session stats from the message history. Every fallible
    ///     step (<see cref="GetMessagesAsync" />) already returns a
    ///     <see cref="Result{T}" /> and nothing here throws, so no try boundary
    ///     is needed at all (ROP-C Z1: the vestigial catch→Failure was removed).
    /// </summary>
    public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    {
        // #1558: the guard + `.Value` pair is one `Map` link now. `Map`
        // short-circuits on failure, so the aggregate is derived only from a
        // readable history and the store's own error travels unchanged.
        // Fold lives in the shared aggregator (#184) — same derive semantics.
        return (await GetMessagesAsync(sessionId, ct).ConfigureAwait(false))
            .Map(messages => SessionStatsAggregator.Aggregate(messages));
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
            return resolved.ConvertFailure();
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

                // #199: an empty file is a Result outcome, not a throw — the
                // Bind below maps it to a failure naming the session + path.
                // The plan reports it by never seeing a record to replace.
                //
                // #1749: the rewritten header states the CURRENT format
                // generation, so a legacy file is upgraded on disk by the
                // first header rewrite. The writer always writes the current
                // generation — that is what makes the generation a contract
                // rather than a description.
                var header = new SessionHeaderEntry(
                    "session",
                    1,
                    SessionFormatCatalog.Current,
                    session.Id,
                    session.ProjectId,
                    session.Directory,
                    ScrubTextForWrite(session.Title),
                    session.Agent,
                    session.Model,
                    session.ProviderId,
                    session.CreatedAt,
                    DateTimeOffset.UtcNow,
                    session.ParentSessionId,
                    session.Status,
                    session.GitBranch,
                    session.GitIsDirty,
                    session.Kind,
                    TierMarker());

                // #460: streamed. Only the first record is replaced; every
                // record after it is copied through byte for byte, so a
                // title/status/git-branch edit no longer materializes the
                // session twice inside this semaphore.
                byte[] headerRecord = JsonSerializer.SerializeToUtf8Bytes(
                    header, JsonlCodecContext.Default.SessionHeaderEntry);
                return SessionFileIO.RewriteRecordsAtomic(
                    sessionFile, new HeaderRewritePlan(headerRecord));
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
            .TapError(e => JsonlSessionStoreLog.UpdateSessionFailed(_logger, session.Id, e));
    }
}

/// <summary>
///     SG8: BCL <c>[LoggerMessage]</c> delegates for <see cref="JsonlSessionStore" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class JsonlSessionStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to create session: {Error}")]
    public static partial void CreateSessionFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to read session {SessionId}: {Error}")]
    public static partial void ReadSessionFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to list sessions: {Error}")]
    public static partial void ListSessionsFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to append message to session {SessionId}: {Error}")]
    public static partial void AppendMessageFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Failed to update message in session {SessionId}: {Error}")]
    public static partial void UpdateMessageFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to read messages of session {SessionId}: {Error}")]
    public static partial void ReadMessagesFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to delete session {SessionId}: {Error}")]
    public static partial void DeleteSessionFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Failed to truncate messages after {MessageId} in session {SessionId}: {Error}")]
    public static partial void TruncateMessagesFailed(ILogger logger, string messageId, string sessionId, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Failed to list checkpoints of session {SessionId}: {Error}")]
    public static partial void ListCheckpointsFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Failed to append checkpoint to session {SessionId}: {Error}")]
    public static partial void AppendCheckpointFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Skipping malformed checkpoint line in session {SessionId}.")]
    public static partial void SkippingMalformedCheckpointLine(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Skipping malformed checkpoint line in session {SessionId}: missing id/messageId.")]
    public static partial void CheckpointLineMissingIds(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "Failed to update session {SessionId}: {Error}")]
    public static partial void UpdateSessionFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Skipping malformed feedback line in session {SessionId}.")]
    public static partial void SkippingMalformedFeedbackLine(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "Feedback line in session {SessionId} is missing its ids.")]
    public static partial void FeedbackLineMissingIds(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning, Message = "Skipping feedback line in session {SessionId}: unknown rating '{Rating}' or category '{Category}'.")]
    public static partial void SkippingUnknownFeedbackValue(ILogger logger, string sessionId, string rating, string category);

    [LoggerMessage(EventId = 17, Level = LogLevel.Error, Message = "Failed to record a message rating in session {SessionId}: {Error}")]
    public static partial void RecordMessageRatingFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 18, Level = LogLevel.Error, Message = "Failed to record a session remark in session {SessionId}: {Error}")]
    public static partial void RecordSessionRemarkFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 19, Level = LogLevel.Error, Message = "Failed to read the feedback of session {SessionId}: {Error}")]
    public static partial void ReadFeedbackFailed(ILogger logger, string sessionId, string error);

    /// <summary>
    ///     #1749: a session file this build cannot read is reported, not
    ///     skipped in silence. The reason names the format generation when
    ///     that is what refused the file.
    /// </summary>
    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "Skipping session {SessionId}: its header could not be read ({Reason}).")]
    public static partial void SessionHeaderUnreadable(ILogger logger, string sessionId, string reason);
}
