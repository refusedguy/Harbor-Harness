using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Lsp;
using Microsoft.Extensions.Logging;

namespace Harbor.Lsp;

/// <summary>
///     One running language server process: spawn → initialize handshake →
///     open/change/close traffic → diagnostics cache. Owns the transport and
///     shuts the process down on dispose.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every redirected pipe is drained for the whole life of the child</b> (#908).
///         stdout goes to <see cref="LspClient" />, which reads it in a continuous loop for
///         the session's lifetime. stderr is drained by the reader
///         <see cref="StartAsync" /> starts before it waits. stderr was redirected and read
///         by nothing, and a language server is the worst case for that: it logs to stderr
///         by design, its pipe is a bounded buffer, and once it overruns, the server blocks
///         in <c>write()</c> and stops answering. Nothing kills it — unlike #884's git,
///         which had a ceiling and a kill and so degraded to an empty answer instead of a
///         hung server.
///     </para>
///     <para>
///         Neither half of this is new. The EXPOSURE is <c>McpProcessClient</c>'s — an MCP
///         stdio server is a chatty child on a redirected stderr, and that type already
///         recognises the shape and drains it. The READER is <c>ProcessGitQuery</c>'s, the
///         <c>BeginErrorReadLine</c> pair <c>BashTool</c> and <c>RipGrepTool</c> already
///         use. This spawn is async, so an async drain would also have been admissible; the
///         event-driven one is used because it leaves no task for the caller to remember to
///         await.
///     </para>
/// </remarks>
public sealed class LspServerSession : IAsyncDisposable
{
    /// <summary>Budget for the initialize handshake — a hung server must not block file opens.</summary>
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Default ceiling for one session operation (didOpen / didChange /
    ///     didClose / definition / references).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A language server that stops answering is a FAILED operation, not
    ///         a wait that never ends. Every operation on this type used to be
    ///         bounded only by the caller's cancellation token, and the default
    ///         token is <see cref="CancellationToken.None" /> — so a server that
    ///         stopped answering (a wedged child, an answer lost in a demux
    ///         race) turned into an operation that waited forever, and a
    ///         session that waited forever turned a CI job into a hang instead
    ///         of a red build.
    ///     </para>
    ///     <para>
    ///         15 s is the same ceiling the manager applies to a lookup
    ///         (<c>LspManager.RequestTimeout</c>) and the same one the
    ///         initialize handshake uses, so a stalled server now degrades
    ///         uniformly instead of hanging on one call path and timing out on
    ///         another. It is generous for a real language server: a definition
    ///         lookup on a cold index is seconds, not minutes.
    ///     </para>
    /// </remarks>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Budget for the graceful shutdown handshake on dispose.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly LspServerDefinition _definition;
    private readonly LspClient _client;
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly TimeSpan _requestTimeout;
    // #1095: diagnostics cache is on the LSP hot path — the server pushes
    // publishDiagnostics per keystroke on the notification thread while the
    // UI/tests pull per file via GetDiagnostics. A ConcurrentDictionary keeps
    // both sites lock-free; the published List is built locally and never
    // mutated after publication, so the reference swap is the whole publish.
    // Same discipline as LspClient._pending (#1402).
    private readonly ConcurrentDictionary<string, List<LspDiagnostic>> _diagnostics = new();
    private int _documentVersion;
    private int _disposed;

    private LspServerSession(
        LspServerDefinition definition,
        LspClient client,
        Process process,
        ILogger logger,
        TimeSpan requestTimeout)
    {
        _definition = definition;
        _client = client;
        _process = process;
        _logger = logger;
        _requestTimeout = requestTimeout;
        _client.ServerNotification += OnServerNotification;
        _client.Disconnected += OnClientDisconnected;
    }

    /// <summary>Raised when diagnostics were re-published for a file (file path form).</summary>
    public event EventHandler<LspDiagnosticsChangedEventArgs>? DiagnosticsChanged;

    public LspServerDefinition Definition => _definition;

    /// <summary>Spawn the server process and complete the initialize handshake.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>stderr is drained here, before anything waits</b> (#908). It was
    ///         redirected and read by nothing. A language server logs to stderr BY DESIGN —
    ///         that is where indexing progress, "could not find Cargo.toml" and parse
    ///         warnings go — and a redirected pipe is a bounded buffer, so once the server
    ///         has written more than fits it blocks in <c>write()</c> and stops. Not "the
    ///         first file open is slow": the server is wedged for the rest of the session,
    ///         because nothing kills it. #884's git had a 3 s ceiling and a kill, so it
    ///         degraded to an empty answer; this has no ceiling at all, which makes it the
    ///         worse of the two.
    ///     </para>
    ///     <para>
    ///         <b>NOT VERIFIED</b> by running anything: no language server and no dotnet on
    ///         this host, so "they log to stderr" is read off documented behaviour rather
    ///         than measured. The fix does not depend on it. The defect is a property of the
    ///         CALLER, which redirected a pipe and cannot bound how much a child writes; a
    ///         server that is silent today is a server whose logging can change without a
    ///         line of product code moving.
    ///     </para>
    ///     <para>
    ///         The reader is event-driven — the <c>BeginErrorReadLine</c> pair
    ///         <c>ProcessGitQuery</c>, <c>BashTool</c> and <c>RipGrepTool</c> already use
    ///         — rather than a <c>ReadToEndAsync</c> join. This is the spawn, so the reader
    ///         can start here and stay started for the life of the child, and the drain then
    ///         needs no cancellation of its own: it ends when the child closes the pipe, or
    ///         when <see cref="DisposeAsync" /> disposes the process.
    ///     </para>
    ///     <para>
    ///         stderr is logged at Debug rather than discarded, for the same reason
    ///         <c>ProcessGitQuery</c> does: a server that dies mid-session says why on
    ///         stderr and nowhere else, and a dead server otherwise presents as "no
    ///         diagnostics" — the same unexplained-empty-answer bug #884 was.
    ///     </para>
    /// </remarks>
    /// <param name="requestTimeout">
    ///     The ceiling for one operation on this session. Defaults to
    ///     <see cref="DefaultRequestTimeout" />; a shorter budget is how a test
    ///     exercises the timeout path without waiting for the real one.
    /// </param>
    public static async Task<LspServerSession> StartAsync(
        LspServerDefinition definition,
        string workspaceRoot,
        ILogger logger,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = definition.Command,
            WorkingDirectory = workspaceRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in definition.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start language server '{definition.Command}'.");

        // The stderr reader starts here, next to the redirect that needs it, and before
        // anything below can wait on the child. stdout is handed to LspClient, which
        // reads it in a loop for the session's lifetime; stderr had no reader at all.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                LspServerSessionLog.ServerStderr(logger, definition.Language, line);
            }
        };
        process.BeginErrorReadLine();

        LspServerSessionLog.Started(logger, definition.Language, definition.Command, process.Id, workspaceRoot);

        var client = new LspClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, logger);
        client.Start();
        var session = new LspServerSession(definition, client, process, logger, requestTimeout ?? DefaultRequestTimeout);

        try
        {
            using var timeoutCts = new CancellationTokenSource(InitializeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.SendRequestAsync(
                "initialize",
                new LspWire.InitializeParams(
                    ProcessId: Environment.ProcessId,
                    RootUri: FileUri(workspaceRoot),
                    Capabilities: new LspWire.ClientCapabilities(
                        new LspWire.TextDocumentCapabilities(new LspWire.SyncCapabilities()))),
                ct: linked.Token).ConfigureAwait(false);

            await client.SendNotificationAsync("initialized", null, ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    /// <summary>Send didOpen for a file (tracked by version).</summary>
    /// <remarks>
    ///     Bounded by <see cref="DefaultRequestTimeout" />: a server that stopped
    ///     reading its input fails the operation instead of parking the caller
    ///     on a write that never completes.
    /// </remarks>
    public Task<Result> OpenAsync(string filePath, string text, string languageId, CancellationToken ct = default)
    {
        int version = Interlocked.Increment(ref _documentVersion);
        return RunBoundedNotificationAsync(
            "didOpen",
            token => _client.SendNotificationAsync(
                "textDocument/didOpen",
                new LspWire.DidOpenTextDocumentParams(new LspWire.TextDocumentItem(
                    FileUri(filePath), languageId, version, text)),
                token),
            ct);
    }

    /// <summary>Send a full-text didChange for a file.</summary>
    public Task<Result> ChangeAsync(string filePath, string text, CancellationToken ct = default)
    {
        int version = Interlocked.Increment(ref _documentVersion);
        return RunBoundedNotificationAsync(
            "didChange",
            token => _client.SendNotificationAsync(
                "textDocument/didChange",
                new LspWire.DidChangeTextDocumentParams(
                    new LspWire.VersionedTextDocumentIdentifier(FileUri(filePath), version),
                    [new LspWire.FullTextChange(text)]),
                token),
            ct);
    }

    /// <summary>Send didClose for a file.</summary>
    public Task<Result> CloseAsync(string filePath, CancellationToken ct = default)
    {
        return RunBoundedNotificationAsync(
            "didClose",
            token => _client.SendNotificationAsync(
                "textDocument/didClose",
                new LspWire.DidCloseTextDocumentParams(new LspWire.TextDocumentIdentifier(FileUri(filePath))),
                token),
            ct);
    }

    /// <summary>
    ///     Resolve definition at the position (normalized to a file path).
    ///     Malformed foreign-server payloads and a server that stopped
    ///     answering both fail with a reason, never a throw (§A2).
    /// </summary>
    /// <remarks>
    ///     The result is <c>Result&lt;Maybe&lt;LspLocation&gt;&gt;</c>: the lookup
    ///     can fail (timeout, malformed payload, server error), the answer can
    ///     legitimately be absent (no definition at that position), and the
    ///     reason is right there when it failed. A bare
    ///     <c>LspLocation?</c> cannot tell those apart.
    /// </remarks>
    public async Task<Result<Maybe<LspLocation>>> FindDefinitionAsync(
        string filePath,
        int line,
        int column,
        CancellationToken ct = default)
    {
        return await RunBoundedAsync<Maybe<LspLocation>>(
            "definition",
            async token =>
            {
                JsonElement? result = await _client.SendRequestAsync(
                    "textDocument/definition",
                    new LspWire.PositionParams(
                        new LspWire.TextDocumentIdentifier(FileUri(filePath)),
                        new LspWire.LspPosition(line, column)),
                    token).ConfigureAwait(false);
                Result<Maybe<LspLocation>> normalized = TryNormalizeFirstLocation(result, filePath);
                if (normalized.IsFailure)
                {
                    LspServerSessionLog.MalformedDefinitionPayload(_logger, _definition.Language, normalized.Error);
                }

                return normalized;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolve references to the symbol at the position.
    ///     Malformed items are skipped leniently (count in the log).
    /// </summary>
    public async Task<Result<IReadOnlyList<LspLocation>>> FindReferencesAsync(
        string filePath,
        int line,
        int column,
        CancellationToken ct = default)
    {
        return await RunBoundedAsync<IReadOnlyList<LspLocation>>(
            "references",
            async token =>
            {
                JsonElement? result = await _client.SendRequestAsync(
                    "textDocument/references",
                    new LspWire.PositionParams(
                        new LspWire.TextDocumentIdentifier(FileUri(filePath)),
                        new LspWire.LspPosition(line, column),
                        new LspWire.ReferenceContext(IncludeDeclaration: true)),
                    token).ConfigureAwait(false);
                IReadOnlyList<LspLocation> locations = NormalizeAllLocations(result, filePath, out string? skippedReason);
                if (skippedReason is not null)
                {
                    LspServerSessionLog.MalformedReferencesPayloads(_logger, _definition.Language, skippedReason);
                }

                return Result.Success(locations);
            },
            ct).ConfigureAwait(false);
    }

    // ── Bounded waits (#1885) ──────────────────────────────────────────────
    //
    // Every operation above runs under a deadline. A language server that
    // stops answering is a FAILED operation: it comes back as a
    // Result.Failure with the reason, never as a wait that never ends. The
    // deadline is DefaultRequestTimeout (15s) unless the caller cancels
    // first, in which case the cancellation is the caller's business and
    // travels on unchanged.

    /// <summary>
    ///     Runs one operation under a deadline. The operation's own failure is
    ///     a <see cref="Result{T}" />; the deadline's failure is a timeout
    ///     <see cref="Result{T}" />. Nothing here throws for an expected
    ///     failure.
    /// </summary>
    private async Task<Result<T>> RunBoundedAsync<T>(
        string operation,
        Func<CancellationToken, Task<Result<T>>> body,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        TimeSpan budget = timeout ?? _requestTimeout;
        using var deadline = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            // The WaitAsync is the outer bound: it releases the caller even if
            // the underlying write cannot be cancelled mid-flight (a pipe write
            // already in progress is not interruptible). The write keeps
            // running under the write lock, so the next operation queues
            // behind it and times out too instead of piling up.
            return await body(linked.Token).WaitAsync(budget, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            LspServerSessionLog.OperationTimedOut(_logger, _definition.Language, operation, budget);
            return Result.Failure<T>($"request-timeout ('{operation}')");
        }
        catch (LspRequestException ex)
        {
            // The server answered with a JSON-RPC error. That is an expected
            // failure of the operation, not a defect of the session: it comes
            // back as a failure with the reason (the manager logs it).
            LspServerSessionLog.RequestFailed(_logger, _definition.Language, operation, ex.Message);
            return Result.Failure<T>($"request-failed ('{operation}': {ex.Message})");
        }
    }

    /// <summary>
    ///     The no-answer half of <see cref="RunBoundedAsync{T}" />: a
    ///     notification that could not be delivered inside the deadline.
    /// </summary>
    private async Task<Result> RunBoundedNotificationAsync(
        string operation,
        Func<CancellationToken, Task> body,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        Result<bool> outcome = await RunBoundedAsync<bool>(
            operation,
            async token =>
            {
                await body(token).ConfigureAwait(false);
                return Result.Success(true);
            },
            ct,
            timeout).ConfigureAwait(false);

        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error);
    }

    /// <summary>Diagnostics last published for the file.</summary>
    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string filePath)
    {
        return _diagnostics.TryGetValue(filePath, out List<LspDiagnostic>? list)
            ? [.. list]
            : [];
    }

    // ── Notifications ──────────────────────────────────────────────────────

    /// <summary>
    ///     The client's read loop ended: the server is gone, or its stdout is
    ///     no longer being drained. Either way the child must not be left
    ///     running: a child blocked on a full stdout pipe is a process that
    ///     never exits on its own, and nothing would ever kill it.
    /// </summary>
    private void OnClientDisconnected(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                LspServerSessionLog.KilledAfterDisconnect(_logger, _definition.Language);
            }
        }
        catch (Exception ex)
        {
            LspServerSessionLog.KillFailed(_logger, ex, _definition.Language);
        }
    }

    private void OnServerNotification(object? sender, LspNotificationEventArgs args)
    {
        if (args.Method != "textDocument/publishDiagnostics" || args.Parameters.ValueKind != JsonValueKind.Object) return;

        try
        {
            LspWire.PublishDiagnosticsParams? published =
                args.Parameters.Deserialize(LspJsonContext.Default.PublishDiagnosticsParams);
            if (published is null) return;

            string filePath = FromUri(published.Uri);
            var list = new List<LspDiagnostic>(published.Diagnostics.Count);
            foreach (LspWire.DiagnosticDto dto in published.Diagnostics)
            {
                list.Add(new LspDiagnostic(
                    filePath,
                    dto.Range.Start.Line,
                    dto.Range.Start.Character,
                    dto.Range.End.Line,
                    dto.Range.End.Character,
                    (LspSeverity)(dto.Severity ?? (int)LspSeverity.Error),
                    dto.Source ?? _definition.Id,
                    dto.Message));
            }

            _diagnostics[filePath] = list;

            DiagnosticsChanged?.Invoke(this, new LspDiagnosticsChangedEventArgs(filePath));
        }
        catch (JsonException ex)
        {
            LspServerSessionLog.MalformedPublishDiagnostics(_logger, ex);
        }
    }

    /// <summary>
    ///     Normalize a definition payload: the first usable location wins,
    ///     garbage fails with a reason instead of vanishing into <c>null</c>.
    /// </summary>
    /// <remarks>
    ///     Location normalization (ROP boundary): definition returns
    ///     Location | Location[] | LocationLink[] | null, references return
    ///     Location[] | null. Foreign servers send garbage, so every shape
    ///     violation carries a machine-readable reason: Success(Some) = usable
    ///     location, Success(None) = legitimately absent (JSON null / [] /
    ///     skippable array items), Failure(reason) = malformed payload.
    ///     Callers map failures to degrade-warnings; the ILspService surface
    ///     itself stays null/[]-typed.
    /// </remarks>
    internal static Result<Maybe<LspLocation>> TryNormalizeFirstLocation(JsonElement? element, string fallbackPath)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Result.Success(Maybe<LspLocation>.None);
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            string? firstFailure = null;
            foreach (JsonElement item in e.EnumerateArray())
            {
                Result<Maybe<LspLocation>> normalized = TryNormalizeSingle(item, fallbackPath);
                if (normalized.IsFailure)
                {
                    firstFailure ??= normalized.Error;
                    continue;
                }

                #pragma warning disable CFE0001
                // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
                // return or continue, a control-flow shape the analyzer does not model.
                // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
                if (normalized.Value.HasValue) return normalized;
                #pragma warning restore CFE0001
            }

            return firstFailure is null
                ? Result.Success(Maybe<LspLocation>.None)
                : Result.Failure<Maybe<LspLocation>>(firstFailure);
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure<Maybe<LspLocation>>($"not-object-or-array (got {e.ValueKind})");
        }

        return TryNormalizeSingle(e, fallbackPath);
    }

    private static IReadOnlyList<LspLocation> NormalizeAllLocations(
        JsonElement? element, string fallbackPath, out string? skippedReason)
    {
        skippedReason = null;
        if (element is not { ValueKind: JsonValueKind.Array } e) return [];
        var list = new List<LspLocation>();
        int skipped = 0;
        foreach (JsonElement item in e.EnumerateArray())
        {
            Result<Maybe<LspLocation>> normalized = TryNormalizeSingle(item, fallbackPath);
            if (normalized.IsFailure)
            {
                skipped++;
                skippedReason ??= normalized.Error;
                continue;
            }

            // Success(None) (e.g. null array items) skips silently — lenient by design.
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            if (normalized.Value.HasValue) list.Add(normalized.Value.Value);
            #pragma warning restore CFE0001
        }

        if (skipped > 0) skippedReason = $"{skipped} malformed location(s) skipped ({skippedReason})";
        return list;
    }

    /// <summary>
    ///     Normalize one Location | LocationLink. Non-objects (e.g. null array
    ///     items) skip leniently as <c>None</c>; misshapen objects fail with a reason.
    /// </summary>
    internal static Result<Maybe<LspLocation>> TryNormalizeSingle(JsonElement item, string fallbackPath)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return Result.Success(Maybe<LspLocation>.None);
        }

        if (item.TryGetProperty("uri", out JsonElement uri))
        {
            if (!item.TryGetProperty("range", out JsonElement range)
                || range.ValueKind != JsonValueKind.Object
                || !range.TryGetProperty("start", out JsonElement start)
                || start.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure<Maybe<LspLocation>>("missing-range");
            }

            return MapBuilt(TryBuildLocation(uri, start, fallbackPath));
        }

        if (item.TryGetProperty("targetUri", out JsonElement targetUri))
        {
            if (!item.TryGetProperty("targetSelectionRange", out JsonElement selection)
                || selection.ValueKind != JsonValueKind.Object
                || !selection.TryGetProperty("start", out JsonElement start)
                || start.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure<Maybe<LspLocation>>("missing-target-selection-range");
            }

            return MapBuilt(TryBuildLocation(targetUri, start, fallbackPath));
        }

        return Result.Failure<Maybe<LspLocation>>("missing-uri");
    }

    private static Result<Maybe<LspLocation>> MapBuilt(Result<LspLocation> built) =>
        built.IsFailure
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            ? built.ConvertFailure<Maybe<LspLocation>>()
            : Result.Success(Maybe.From(built.Value));
            #pragma warning restore CFE0001

    internal static Result<LspLocation> TryBuildLocation(JsonElement uriElement, JsonElement start, string fallbackPath)
    {
        if (uriElement.ValueKind != JsonValueKind.String)
        {
            return Result.Failure<LspLocation>("uri-not-string");
        }

        string path = FromUri(uriElement.GetString() ?? string.Empty);
        if (string.IsNullOrEmpty(path)) path = fallbackPath;
        int line = start.TryGetProperty("line", out JsonElement l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        int character = start.TryGetProperty("character", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        return Result.Success(new LspLocation(path, line, character));
    }

    // ── URI helpers ────────────────────────────────────────────────────────

    /// <summary>Converts a file path to a file:// URI (LSP wire form).</summary>
    public static string FileUri(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        string separators = fullPath.Replace('\\', '/');
        return separators.StartsWith('/') ? "file://" + separators : "file:///" + separators;
    }

    /// <summary>Converts a file:// URI back to a local path; non-file URIs become empty.</summary>
    public static string FromUri(string uri)
    {
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        try
        {
            var parsed = new Uri(uri);
            return parsed.IsFile ? parsed.LocalPath : string.Empty;
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }

    // ── Dispose ────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _client.ServerNotification -= OnServerNotification;
        _client.Disconnected -= OnClientDisconnected;
        try
        {
            await _client.SendRequestAsync("shutdown", null).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            await _client.SendNotificationAsync("exit", null).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LspServerSessionLog.GracefulShutdownFailed(_logger, ex, _definition.Language);
        }
        finally
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                LspServerSessionLog.KillFailed(_logger, ex, _definition.Language);
            }

            // Disposing the process closes the stderr pipe the reader `StartAsync` started,
            // which is what ends it: the drain is tied to the child's lifetime rather than
            // to a task this type has to remember to await (#908).
            _process.Dispose();
        }
    }
}

/// <summary>
///     SG13: BCL <c>[LoggerMessage]</c> delegates for <see cref="LspServerSession" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class LspServerSessionLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "LSP: {Language} server stderr: {Line}")]
    public static partial void ServerStderr(ILogger logger, string language, string line);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "LSP: started {Language} server ({Command}) pid={Pid} root={Root}")]
    public static partial void Started(ILogger logger, string language, string command, int pid, string root);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "LSP: {Language} server returned a malformed definition payload ({Reason}) — degraded to no-result")]
    public static partial void MalformedDefinitionPayload(ILogger logger, string language, string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "LSP: {Language} server returned malformed references payloads ({Reason})")]
    public static partial void MalformedReferencesPayloads(ILogger logger, string language, string reason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "LSP: malformed publishDiagnostics payload")]
    public static partial void MalformedPublishDiagnostics(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "LSP: graceful shutdown of {Language} server failed — killing")]
    public static partial void GracefulShutdownFailed(ILogger logger, Exception ex, string language);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "LSP: kill of {Language} server failed")]
    public static partial void KillFailed(ILogger logger, Exception ex, string language);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "LSP: {Language} server did not answer '{Operation}' within {Timeout}; degraded")]
    public static partial void OperationTimedOut(ILogger logger, string language, string operation, TimeSpan timeout);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "LSP: {Language} server connection lost; killed the child so it cannot block on a full pipe")]
    public static partial void KilledAfterDisconnect(ILogger logger, string language);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "LSP: {Language} server refused '{Operation}' ({Reason}); degraded")]
    public static partial void RequestFailed(ILogger logger, string language, string operation, string reason);
}
