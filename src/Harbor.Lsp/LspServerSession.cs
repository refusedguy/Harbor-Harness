using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Lsp;
using Microsoft.Extensions.Logging;

namespace Harbor.Lsp;

/// <summary>
///     One running language server process: spawn → initialize handshake →
///     open/change/close traffic → diagnostics cache. Owns the transport and
///     shuts the process down on dispose.
/// </summary>
public sealed class LspServerSession : IAsyncDisposable
{
    /// <summary>Budget for the initialize handshake — a hung server must not block file opens.</summary>
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(15);

    private readonly LspServerDefinition _definition;
    private readonly LspClient _client;
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly Dictionary<string, List<LspDiagnostic>> _diagnostics = [];
    private readonly Lock _diagnosticsLock = new();
    private int _documentVersion;
    private int _disposed;

    private LspServerSession(
        LspServerDefinition definition,
        LspClient client,
        Process process,
        ILogger logger)
    {
        _definition = definition;
        _client = client;
        _process = process;
        _logger = logger;
        _client.ServerNotification += OnServerNotification;
    }

    /// <summary>Raised when diagnostics were re-published for a file (file path form).</summary>
    public event EventHandler<LspDiagnosticsChangedEventArgs>? DiagnosticsChanged;

    public LspServerDefinition Definition => _definition;

    /// <summary>Spawn the server process and complete the initialize handshake.</summary>
    public static async Task<LspServerSession> StartAsync(
        LspServerDefinition definition,
        string workspaceRoot,
        ILogger logger,
        CancellationToken ct = default)
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

        logger.LogInformation(
            "LSP: started {Language} server ({Command}) pid={Pid} root={Root}",
            definition.Language, definition.Command, process.Id, workspaceRoot);

        var client = new LspClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, logger);
        client.Start();
        var session = new LspServerSession(definition, client, process, logger);

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
    public async Task OpenAsync(string filePath, string text, string languageId, CancellationToken ct = default)
    {
        int version = Interlocked.Increment(ref _documentVersion);
        await _client.SendNotificationAsync(
            "textDocument/didOpen",
            new LspWire.DidOpenTextDocumentParams(new LspWire.TextDocumentItem(
                FileUri(filePath), languageId, version, text)),
            ct).ConfigureAwait(false);
    }

    /// <summary>Send a full-text didChange for a file.</summary>
    public async Task ChangeAsync(string filePath, string text, CancellationToken ct = default)
    {
        int version = Interlocked.Increment(ref _documentVersion);
        await _client.SendNotificationAsync(
            "textDocument/didChange",
            new LspWire.DidChangeTextDocumentParams(
                new LspWire.VersionedTextDocumentIdentifier(FileUri(filePath), version),
                [new LspWire.FullTextChange(text)]),
            ct).ConfigureAwait(false);
    }

    /// <summary>Send didClose for a file.</summary>
    public async Task CloseAsync(string filePath, CancellationToken ct = default)
    {
        await _client.SendNotificationAsync(
            "textDocument/didClose",
            new LspWire.DidCloseTextDocumentParams(new LspWire.TextDocumentIdentifier(FileUri(filePath))),
            ct).ConfigureAwait(false);
    }

    /// <summary>Resolve definition at the position (normalized to a file path).</summary>
    public async Task<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct)
    {
        JsonElement? result = await _client.SendRequestAsync(
            "textDocument/definition",
            new LspWire.PositionParams(
                new LspWire.TextDocumentIdentifier(FileUri(filePath)),
                new LspWire.LspPosition(line, column)),
            ct).ConfigureAwait(false);
        return NormalizeFirstLocation(result, filePath);
    }

    /// <summary>Resolve references to the symbol at the position.</summary>
    public async Task<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct)
    {
        JsonElement? result = await _client.SendRequestAsync(
            "textDocument/references",
            new LspWire.PositionParams(
                new LspWire.TextDocumentIdentifier(FileUri(filePath)),
                new LspWire.LspPosition(line, column),
                new LspWire.ReferenceContext(IncludeDeclaration: true)),
            ct).ConfigureAwait(false);
        return NormalizeAllLocations(result, filePath);
    }

    /// <summary>Diagnostics last published for the file.</summary>
    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string filePath)
    {
        lock (_diagnosticsLock)
        {
            return _diagnostics.TryGetValue(filePath, out List<LspDiagnostic>? list)
                ? [.. list]
                : [];
        }
    }

    // ── Notifications ──────────────────────────────────────────────────────

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

            lock (_diagnosticsLock)
            {
                _diagnostics[filePath] = list;
            }

            DiagnosticsChanged?.Invoke(this, new LspDiagnosticsChangedEventArgs(filePath));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "LSP: malformed publishDiagnostics payload");
        }
    }

    // ── Location normalization ─────────────────────────────────────────────

    /// <summary>
    ///     Definition returns Location | Location[] | LocationLink[] | null;
    ///     references return Location[] | null. Normalize both leniently.
    /// </summary>
    private static LspLocation? NormalizeFirstLocation(JsonElement? element, string fallbackPath)
    {
        if (element is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } e) return null;

        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("targetUri", out JsonElement linkUri))
        {
            return FromLocationLike(linkUri, e.GetProperty("targetSelectionRange").GetProperty("start"), fallbackPath);
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in e.EnumerateArray())
            {
                LspLocation? location = NormalizeSingle(item, fallbackPath);
                if (location is not null) return location;
            }

            return null;
        }

        return NormalizeSingle(e, fallbackPath);
    }

    private static IReadOnlyList<LspLocation> NormalizeAllLocations(JsonElement? element, string fallbackPath)
    {
        if (element is not { ValueKind: JsonValueKind.Array } e) return [];
        var list = new List<LspLocation>();
        foreach (JsonElement item in e.EnumerateArray())
        {
            LspLocation? location = NormalizeSingle(item, fallbackPath);
            if (location is not null) list.Add(location);
        }

        return list;
    }

    private static LspLocation? NormalizeSingle(JsonElement item, string fallbackPath)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (item.TryGetProperty("uri", out JsonElement uri))
        {
            JsonElement start = item.GetProperty("range").GetProperty("start");
            return FromLocationLike(uri, start, fallbackPath);
        }

        if (item.TryGetProperty("targetUri", out JsonElement targetUri))
        {
            JsonElement start = item.GetProperty("targetSelectionRange").GetProperty("start");
            return FromLocationLike(targetUri, start, fallbackPath);
        }

        return null;
    }

    private static LspLocation? FromLocationLike(JsonElement uriElement, JsonElement start, string fallbackPath)
    {
        if (uriElement.ValueKind != JsonValueKind.String) return null;
        string path = FromUri(uriElement.GetString() ?? string.Empty);
        if (string.IsNullOrEmpty(path)) path = fallbackPath;
        int line = start.TryGetProperty("line", out JsonElement l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        int character = start.TryGetProperty("character", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        return new LspLocation(path, line, character);
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
        try
        {
            await _client.SendRequestAsync("shutdown", null).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await _client.SendNotificationAsync("exit", null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP: graceful shutdown of {Language} server failed — killing", _definition.Language);
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
                _logger.LogDebug(ex, "LSP: kill of {Language} server failed", _definition.Language);
            }

            _process.Dispose();
        }
    }
}
