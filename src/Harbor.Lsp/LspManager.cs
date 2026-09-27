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
    private readonly Dictionary<string, LspServerSession> _sessions = [];
    private readonly HashSet<string> _unavailable = [];
    private readonly Lock _sync = new();
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
            _logger.LogDebug("LSP: open of {File} degraded ({Reason})", filePath, resolved.Error);
            return;
        }

        LspServerSession session = resolved.Value;
        string fullPath = Path.GetFullPath(filePath);
        try
        {
            await session.OpenAsync(fullPath, text, LanguageIdFor(session.Definition), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LSP: open of {File} on {Language} server failed ({Reason}) — degraded",
                fullPath, session.Definition.Language, ex.Message);
            return;
        }

        _logger.LogDebug("LSP: opened {File} on {Language} server", fullPath, session.Definition.Language);
    }

    /// <inheritdoc />
    public async ValueTask NotifyChangeAsync(string filePath, string newText, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            _logger.LogDebug("LSP: change of {File} degraded ({Reason})", filePath, resolved.Error);
            return;
        }

        try
        {
            await resolved.Value.ChangeAsync(Path.GetFullPath(filePath), newText, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LSP: change of {File} failed ({Reason}) — degraded",
                filePath, ex.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask CloseFileAsync(string filePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            _logger.LogDebug("LSP: close of {File} degraded ({Reason})", filePath, resolved.Error);
            return;
        }

        try
        {
            await resolved.Value.CloseAsync(Path.GetFullPath(filePath)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LSP: close of {File} failed ({Reason}) — degraded",
                filePath, ex.Message);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Result<LspServerSession> resolved = ResolveSession(filePath);
        if (resolved.IsFailure)
        {
            _logger.LogDebug("LSP: diagnostics of {File} degraded ({Reason})", filePath, resolved.Error);
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
            _logger.LogDebug("LSP: definition lookup for {File} degraded ({Reason})", filePath, resolved.Error);
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            return await resolved.Value.FindDefinitionAsync(Path.GetFullPath(filePath), line, column, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "LSP: definition lookup for {File} timed out after {Timeout} (lookup-timed-out) — degraded to no-result",
                filePath, RequestTimeout);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "LSP: definition lookup for {File} failed (request-failed: {Reason}) — degraded to no-result",
                filePath, ex.Message);
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
            _logger.LogDebug("LSP: references lookup for {File} degraded ({Reason})", filePath, resolved.Error);
            return [];
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            return await resolved.Value.FindReferencesAsync(Path.GetFullPath(filePath), line, column, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "LSP: references lookup for {File} timed out after {Timeout} (lookup-timed-out) — degraded to empty",
                filePath, RequestTimeout);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "LSP: references lookup for {File} failed (request-failed: {Reason}) — degraded to empty",
                filePath, ex.Message);
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

        lock (_sync)
        {
            if (_sessions.TryGetValue(definition.Id, out LspServerSession? session) && session is not null)
            {
                return Result.Success(session);
            }
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

        lock (_sync)
        {
            if (_sessions.TryGetValue(definition.Id, out LspServerSession? existing) && existing is not null)
            {
                return Result.Success(existing);
            }

            // Start failure is logged once below; repeat opens stay cheap no-ops.
            if (_unavailable.Contains(definition.Id))
            {
                return Result.Failure<LspServerSession>($"server-unavailable ('{definition.Id}')");
            }
        }

        string workspaceRoot = FindWorkspaceRoot(filePath);
        LspServerSession session;
        try
        {
            session = await LspServerSession.StartAsync(definition, workspaceRoot, _logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _ = _unavailable.Add(definition.Id);
            }

            _logger.LogWarning(
                ex,
                "LSP: {Language} server '{Command}' unavailable — diagnostics/definition disabled for this language",
                definition.Language, definition.Command);
            return Result.Failure<LspServerSession>($"server-start-failed ('{definition.Id}': {ex.Message})");
        }

        session.DiagnosticsChanged += (_, args) => DiagnosticsChanged?.Invoke(this, args);

        lock (_sync)
        {
            // Two opens racing the same language: keep the winner, dispose the loser.
            if (_sessions.TryGetValue(definition.Id, out LspServerSession? winner) && winner is not null)
            {
                // §D4: best-effort by design, but never unobserved — log the loser fault.
                _ = session.DisposeAsync().AsTask().ContinueWith(
                    static (t, state) =>
                    {
                        var (logger, language) = ((ILogger<LspManager>, string))state!;
                        logger.LogWarning(
                            t.Exception,
                            "LSP: dispose of superseded {Language} session failed",
                            language);
                    },
                    (_logger, definition.Language),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return Result.Success(winner);
            }

            _sessions[definition.Id] = session;
            return Result.Success(session);
        }
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

        List<LspServerSession> sessions;
        lock (_sync)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (LspServerSession session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
