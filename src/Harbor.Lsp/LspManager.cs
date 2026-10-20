using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Lsp;
using Microsoft.Extensions.Logging;

namespace Harbor.Lsp;

/// <summary>
///     Routes files to builtin language servers and implements
///     <see cref="ILspService"/> over <see cref="LspServerSession"/> instances.
/// </summary>
/// <remarks>
///     <para>
///         <b>Auto-spawn:</b> the first open of a file whose extension a builtin
///         server handles starts that server (out-of-process, stdio) rooted at
///         the file's workspace (nearest <c>.git</c>, else the file's directory).
///     </para>
///     <para>
///         <b>Graceful degradation with reasons (§A8):</b> a missing server binary logs once and
///         marks the language unavailable — subsequent calls are cheap no-ops.
///         Every degradation carries a machine-readable reason (<c>no-server-for-language</c>,
///         <c>session-not-started</c>, <c>server-unavailable</c>, <c>server-start-failed</c>,
///         <c>request-failed</c>, <c>lookup-timed-out</c>, plus the session normalizer reasons)
///         surfaced in the log, so "no server for language" is distinguishable from
///         "server crashed". The agent loop and the editor never fail because of LSP.
///     </para>
/// </remarks>
public sealed class LspManager : ILspService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<LspManager> _logger;
    private readonly IReadOnlyList<LspServerDefinition> _definitions;
    /// <summary>Lock-free session registry (#1095): session open/get/dispose
    /// never contend — reads are request-path (<c>ResolveSession</c>, the
    /// <c>GetOrCreateSessionAsync</c> fast path), writes are one spawn per
    /// language. Same discipline as <c>LspClient._pending</c> (#1402) and
    /// <c>RendererPipeline._factories</c> (#1438).</summary>
    private readonly ConcurrentDictionary<string, LspServerSession> _sessions = new();
    /// <summary>Languages whose server failed to start (byte is an unused
    /// payload — ConcurrentDictionary as a concurrent set).</summary>
    private readonly ConcurrentDictionary<string, byte> _unavailable = new();
    private int _disposed;

    /// <summary>Create a manager over the builtin server catalog (overridable for tests).</summary>
    public LspManager(ILogger<LspManager> logger, IReadOnlyList<LspServerDefinition>? definitions = null)
    {
        _logger = logger;
        _definitions = definitions ?? LspServerDefinition.Builtin;
    }

    /// <inheritdoc />
    public event EventHandler<LspDiagnosticsChangedEventArgs>? DiagnosticsChanged;

    /// <inheritdoc />
    public bool SupportsFile(string filePath)
    {
        return _definitions.Any(d => d.Handles(filePath));
    }

    /// <inheritdoc />
    public async ValueTask OpenFileAsync(string filePath, string text, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = await GetOrCreateSessionAsync(filePath, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            LspManagerLog.OpenDegraded(_logger, filePath, resolved.Error);
            return;
        }

        LspServerSession session = resolved.Value;
        string fullPath = Path.GetFullPath(filePath);
        try
        {
            // The session bounds its own wait (LspServerSession.DefaultRequestTimeout):
            // a server that stopped answering fails the open instead of parking
            // the caller on a write that never completes.
            Result opened = await session.OpenAsync(fullPath, text, LanguageIdFor(session.Definition), ct).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                LspManagerLog.OpenDegraded(_logger, fullPath, opened.Error);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LspManagerLog.OpenFailed(_logger, ex, fullPath, session.Definition.Language, ex.Message);
            return;
        }

        LspManagerLog.Opened(_logger, fullPath, session.Definition.Language);
    }

    /// <inheritdoc />
    public async ValueTask NotifyChangeAsync(string filePath, string newText, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            LspManagerLog.ChangeDegraded(_logger, filePath, resolved.Error);
            return;
        }

        try
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            Result changed = await resolved.Value.ChangeAsync(Path.GetFullPath(filePath), newText, ct).ConfigureAwait(false);
            #pragma warning restore CFE0001
            if (changed.IsFailure)
            {
                LspManagerLog.ChangeDegraded(_logger, filePath, changed.Error);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LspManagerLog.ChangeFailed(_logger, ex, filePath, ex.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask CloseFileAsync(string filePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            LspManagerLog.CloseDegraded(_logger, filePath, resolved.Error);
            return;
        }

        try
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            Result closed = await resolved.Value.CloseAsync(Path.GetFullPath(filePath)).ConfigureAwait(false);
            #pragma warning restore CFE0001
            if (closed.IsFailure)
            {
                LspManagerLog.CloseDegraded(_logger, filePath, closed.Error);
            }
        }
        catch (Exception ex)
        {
            LspManagerLog.CloseFailed(_logger, ex, filePath, ex.Message);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            LspManagerLog.DiagnosticsDegraded(_logger, filePath, resolved.Error);
            return ValueTask.FromResult<IReadOnlyList<LspDiagnostic>>([]);
        }

        IReadOnlyList<LspDiagnostic> diagnostics = resolved.Value.GetDiagnostics(Path.GetFullPath(filePath));
        return ValueTask.FromResult(diagnostics);
    }

    /// <inheritdoc />
    public async ValueTask<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            LspManagerLog.DefinitionLookupDegraded(_logger, filePath, resolved.Error);
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            Result<Maybe<LspLocation>> found = await resolved.Value.FindDefinitionAsync(Path.GetFullPath(filePath), line, column, cts.Token).ConfigureAwait(false);
            #pragma warning restore CFE0001
            // The session reports a timeout or a malformed payload as a
            // failure with the reason; the public ILspService surface is
            // null/[]-typed, so the reason survives in the log.
            if (found.IsFailure)
            {
                LspManagerLog.DefinitionLookupDegraded(_logger, filePath, found.Error);
            }

            // ROP boundary: one Match instead of IsFailure + .Value on the
            // Result. The type argument is inferred, NOT spelled out: Match is
            // an extension whose FIRST type parameter is the Result's own, so
            // an explicit argument would bind the wrong one.
            return found.Match(
                static maybe => maybe.HasValue ? maybe.Value : null,
                static _ => (LspLocation?)null);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            LspManagerLog.DefinitionLookupTimedOut(_logger, ex, filePath, RequestTimeout);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LspManagerLog.DefinitionLookupFailed(_logger, ex, filePath, ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            LspManagerLog.ReferencesLookupDegraded(_logger, filePath, resolved.Error);
            return [];
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            Result<IReadOnlyList<LspLocation>> found = await resolved.Value.FindReferencesAsync(Path.GetFullPath(filePath), line, column, cts.Token).ConfigureAwait(false);
            #pragma warning restore CFE0001
            if (found.IsFailure)
            {
                LspManagerLog.ReferencesLookupDegraded(_logger, filePath, found.Error);
            }

            // ROP boundary: one Match instead of IsFailure + .Value on the
            // Result (same shape as the definition lookup above).
            return found.Match(
                static locations => locations,
                static _ => (IReadOnlyList<LspLocation>)Array.Empty<LspLocation>());
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            LspManagerLog.ReferencesLookupTimedOut(_logger, ex, filePath, RequestTimeout);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LspManagerLog.ReferencesLookupFailed(_logger, ex, filePath, ex.Message);
            return [];
        }
    }

    // ── Session management (ROP boundary, §A8) ───────────────────────────────
    //
    // Every degradation returns a Failure with a machine-readable reason instead
    // of null, so callers can log "no server for language" vs "server crashed"
    // instead of degrading silently. The public ILspService surface maps these
    // to its null/[] contract (reasons survive in the log).

    private Result<LspServerSession> ResolveSession(string filePath)
    {
        LspServerDefinition? definition = _definitions.FirstOrDefault(d => d.Handles(filePath));
        if (definition is null)
        {
            return Result.Failure<LspServerSession>(
                $"no-server-for-language (extension '{Path.GetExtension(filePath)}')");
        }

        // Lock-free read (#1095): ConcurrentDictionary needs no gate on this request path.
        if (_sessions.TryGetValue(definition.Id, out LspServerSession? session) && session is not null)
        {
            return Result.Success(session);
        }

        return Result.Failure<LspServerSession>(
            $"session-not-started ('{definition.Id}' — open the file first so the server spawns)");
    }

    private async Task<Result<LspServerSession>> GetOrCreateSessionAsync(string filePath, CancellationToken ct)
    {
        LspServerDefinition? definition = _definitions.FirstOrDefault(d => d.Handles(filePath));
        if (definition is null)
        {
            return Result.Failure<LspServerSession>(
                $"no-server-for-language (extension '{Path.GetExtension(filePath)}')");
        }

        // Lock-free fast path (#1095): both lookups need no gate.
        if (_sessions.TryGetValue(definition.Id, out LspServerSession? existing) && existing is not null)
        {
            return Result.Success(existing);
        }

        // Start failure is logged once below; repeat opens stay cheap no-ops.
        if (_unavailable.ContainsKey(definition.Id))
        {
            return Result.Failure<LspServerSession>($"server-unavailable ('{definition.Id}')");
        }

        string workspaceRoot = FindWorkspaceRoot(filePath);
        LspServerSession session;
        try
        {
            session = await LspServerSession.StartAsync(definition, workspaceRoot, _logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = _unavailable.TryAdd(definition.Id, 0);

            LspManagerLog.ServerUnavailable(_logger, ex, definition.Language, definition.Command);
            return Result.Failure<LspServerSession>($"server-start-failed ('{definition.Id}': {ex.Message})");
        }

        session.DiagnosticsChanged += (_, args) => DiagnosticsChanged?.Invoke(this, args);

        // Two opens racing the same language: keep the winner, dispose the loser.
        // TryAdd is atomic (#1095) — the loser path only runs when a concurrent open won.
        if (!_sessions.TryAdd(definition.Id, session))
        {
            if (_sessions.TryGetValue(definition.Id, out LspServerSession? winner) && winner is not null)
            {
                // §D4: best-effort by design, but never unobserved — log the loser fault.
                _ = session.DisposeAsync().AsTask().ContinueWith(
                    static (t, state) =>
                    {
                        var (logger, language) = ((ILogger<LspManager>, string))state!;
                        LspManagerLog.DisposeSupersededFailed(logger, t.Exception!, language);
                    },
                    (_logger, definition.Language),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return Result.Success(winner);
            }

            // Lost the race against DisposeAsync clearing the registry: the
            // manager is going down — drop our session too and degrade like
            // every other unresolvable lookup (no new exception types).
            await session.DisposeAsync().ConfigureAwait(false);
            return Result.Failure<LspServerSession>(
                $"session-not-started ('{definition.Id}' — manager is disposed)");
        }

        return Result.Success(session);
    }

    /// <summary>Nearest ancestor directory containing <c>.git</c>, else the file's directory.</summary>
    public static string FindWorkspaceRoot(string filePath)
    {
        DirectoryInfo? dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "/");
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "/";
    }

    private static string LanguageIdFor(LspServerDefinition definition) => definition.Id switch
    {
        "typescript" => "typescript",
        "python" => "python",
        "go" => "go",
        "rust" => "rust",
        "csharp" => "csharp",
        _ => definition.Language.ToLowerInvariant(),
    };

    // ── Dispose ────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Moment-in-time snapshot, then clear — ConcurrentDictionary needs no
        // gate here (same discipline as LspClient.Dispose, #1402).
        List<LspServerSession> sessions = [.. _sessions.Values];
        _sessions.Clear();

        foreach (LspServerSession session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
///     SG13: BCL <c>[LoggerMessage]</c> delegates for <see cref="LspManager" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class LspManagerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "LSP: open of {File} degraded ({Reason})")]
    public static partial void OpenDegraded(ILogger logger, string file, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "LSP: open of {File} on {Language} server failed ({Reason}) — degraded")]
    public static partial void OpenFailed(ILogger logger, Exception ex, string file, string language, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "LSP: opened {File} on {Language} server")]
    public static partial void Opened(ILogger logger, string file, string language);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "LSP: change of {File} degraded ({Reason})")]
    public static partial void ChangeDegraded(ILogger logger, string file, string reason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "LSP: change of {File} failed ({Reason}) — degraded")]
    public static partial void ChangeFailed(ILogger logger, Exception ex, string file, string reason);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "LSP: close of {File} degraded ({Reason})")]
    public static partial void CloseDegraded(ILogger logger, string file, string reason);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "LSP: close of {File} failed ({Reason}) — degraded")]
    public static partial void CloseFailed(ILogger logger, Exception ex, string file, string reason);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "LSP: diagnostics of {File} degraded ({Reason})")]
    public static partial void DiagnosticsDegraded(ILogger logger, string file, string reason);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "LSP: definition lookup for {File} degraded ({Reason})")]
    public static partial void DefinitionLookupDegraded(ILogger logger, string file, string reason);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "LSP: definition lookup for {File} timed out after {Timeout} (lookup-timed-out) — degraded to no-result")]
    public static partial void DefinitionLookupTimedOut(ILogger logger, Exception ex, string file, TimeSpan timeout);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "LSP: definition lookup for {File} failed (request-failed: {Reason}) — degraded to no-result")]
    public static partial void DefinitionLookupFailed(ILogger logger, Exception ex, string file, string reason);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "LSP: references lookup for {File} degraded ({Reason})")]
    public static partial void ReferencesLookupDegraded(ILogger logger, string file, string reason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "LSP: references lookup for {File} timed out after {Timeout} (lookup-timed-out) — degraded to empty")]
    public static partial void ReferencesLookupTimedOut(ILogger logger, Exception ex, string file, TimeSpan timeout);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "LSP: references lookup for {File} failed (request-failed: {Reason}) — degraded to empty")]
    public static partial void ReferencesLookupFailed(ILogger logger, Exception ex, string file, string reason);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "LSP: {Language} server '{Command}' unavailable — diagnostics/definition disabled for this language")]
    public static partial void ServerUnavailable(ILogger logger, Exception ex, string language, string command);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning, Message = "LSP: dispose of superseded {Language} session failed")]
    public static partial void DisposeSupersededFailed(ILogger logger, Exception ex, string language);
}
