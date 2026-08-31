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
///         <b>Graceful degradation:</b> a missing server binary logs once and
///         marks the language unavailable — subsequent calls are cheap no-ops.
///         The agent loop and the editor never fail because of LSP.
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

        LspServerSession? session = await GetOrCreateSessionAsync(filePath, ct).ConfigureAwait(false);
        if (session is null) return;

        string fullPath = Path.GetFullPath(filePath);
        await session.OpenAsync(fullPath, text, LanguageIdFor(session.Definition), ct).ConfigureAwait(false);
        _logger.LogDebug("LSP: opened {File} on {Language} server", fullPath, session.Definition.Language);
    }

    /// <inheritdoc />
    public async ValueTask NotifyChangeAsync(string filePath, string newText, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        LspServerSession? session = GetSessionFor(filePath);
        if (session is null) return;
        await session.ChangeAsync(Path.GetFullPath(filePath), newText, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CloseFileAsync(string filePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        LspServerSession? session = GetSessionFor(filePath);
        if (session is null) return;
        await session.CloseAsync(Path.GetFullPath(filePath)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        LspServerSession? session = GetSessionFor(filePath);
        IReadOnlyList<LspDiagnostic> diagnostics = session?.GetDiagnostics(Path.GetFullPath(filePath)) ?? [];
        return ValueTask.FromResult(diagnostics);
    }

    /// <inheritdoc />
    public async ValueTask<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        LspServerSession? session = GetSessionFor(filePath);
        if (session is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        return await session.FindDefinitionAsync(Path.GetFullPath(filePath), line, column, cts.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        LspServerSession? session = GetSessionFor(filePath);
        if (session is null) return [];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        return await session.FindReferencesAsync(Path.GetFullPath(filePath), line, column, cts.Token).ConfigureAwait(false);
    }

    // ── Session management ─────────────────────────────────────────────────

    private LspServerSession? GetSessionFor(string filePath)
    {
        LspServerDefinition? definition = _definitions.FirstOrDefault(d => d.Handles(filePath));
        if (definition is null) return null;

        lock (_sync)
        {
            return _sessions.TryGetValue(definition.Id, out LspServerSession? session) ? session : null;
        }
    }

    private async ValueTask<LspServerSession?> GetOrCreateSessionAsync(string filePath, CancellationToken ct)
    {
        LspServerDefinition? definition = _definitions.FirstOrDefault(d => d.Handles(filePath));
        if (definition is null) return null;

        lock (_sync)
        {
            if (_sessions.TryGetValue(definition.Id, out LspServerSession? existing)) return existing;
            if (_unavailable.Contains(definition.Id)) return null; // logged once, degrade silently
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
            return null;
        }

        session.DiagnosticsChanged += (_, args) => DiagnosticsChanged?.Invoke(this, args);

        lock (_sync)
        {
            // Two opens racing the same language: keep the winner, dispose the loser.
            if (_sessions.TryGetValue(definition.Id, out LspServerSession? winner))
            {
                _ = session.DisposeAsync().AsTask();
                return winner;
            }

            _sessions[definition.Id] = session;
            return session;
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
