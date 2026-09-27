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

    /// <summary>
    ///     Resolve definition at the position (normalized to a file path).
    ///     Malformed foreign-server payloads degrade to <c>null</c> with the
    ///     reason in the log — never a throw (§A2).
    /// </summary>
    public async Task<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct)
    {
        JsonElement? result = await _client.SendRequestAsync(
            "textDocument/definition",
            new LspWire.PositionParams(
                new LspWire.TextDocumentIdentifier(FileUri(filePath)),
                new LspWire.LspPosition(line, column)),
            ct).ConfigureAwait(false);
        Result<Maybe<LspLocation>> normalized = TryNormalizeFirstLocation(result, filePath);
        if (normalized.IsFailure)
        {
            _logger.LogWarning(
                "LSP: {Language} server returned a malformed definition payload ({Reason}) — degraded to no-result",
                _definition.Language, normalized.Error);
            return null;
        }

        Maybe<LspLocation> location = normalized.Value;
        return location.HasValue ? location.Value : null;
    }

    /// <summary>
    ///     Resolve references to the symbol at the position.
    ///     Malformed items are skipped leniently (count in the log).
    /// </summary>
    public async Task<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct)
    {
        JsonElement? result = await _client.SendRequestAsync(
            "textDocument/references",
            new LspWire.PositionParams(
                new LspWire.TextDocumentIdentifier(FileUri(filePath)),
                new LspWire.LspPosition(line, column),
                new LspWire.ReferenceContext(IncludeDeclaration: true)),
            ct).ConfigureAwait(false);
        IReadOnlyList<LspLocation> locations = NormalizeAllLocations(result, filePath, out string? skippedReason);
        if (skippedReason is not null)
        {
            _logger.LogDebug(
                "LSP: {Language} server returned malformed references payloads ({Reason})",
                _definition.Language, skippedReason);
        }

        return locations;
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

                if (normalized.Value.HasValue) return normalized;
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
            if (normalized.Value.HasValue) list.Add(normalized.Value.Value);
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
            ? Result.Failure<Maybe<LspLocation>>(built.Error)
            : Result.Success(Maybe.From(built.Value));

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
