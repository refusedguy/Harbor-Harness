namespace Harbor.Abstractions.Lsp;

/// <summary>
///     One diagnostic published by a language server for an open file
///     (mirrors LSP <c>textDocument/publishDiagnostics</c>, normalized to
///     zero-based line/column like the protocol itself).
/// </summary>
public sealed record LspDiagnostic(
    string FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    LspSeverity Severity,
    string Source,
    string Message);

/// <summary>Diagnostic severity, mapped from the LSP 1–4 codes.</summary>
public enum LspSeverity
{
    /// <summary>Emergency — unused, keeps 1-based LSP alignment; treated as Error.</summary>
    None = 0,

    /// <summary>LSP 1 — Error.</summary>
    Error = 1,

    /// <summary>LSP 2 — Warning.</summary>
    Warning = 2,

    /// <summary>LSP 3 — Information.</summary>
    Information = 3,

    /// <summary>LSP 4 — Hint.</summary>
    Hint = 4,
}

/// <summary>A file location returned by definition/references lookups.</summary>
public sealed record LspLocation(string FilePath, int Line, int Column);

/// <summary>
///     Raised when a language server re-publishes diagnostics for a file.
///     The payload is the file path; callers fetch the fresh set through
///     <see cref="ILspService.GetDiagnosticsAsync" />.
/// </summary>
public sealed class LspDiagnosticsChangedEventArgs(string filePath) : EventArgs
{
    /// <summary>The file whose diagnostics changed.</summary>
    public string FilePath { get; } = filePath;
}

/// <summary>
///     In-process LSP facade over the builtin language servers. The manager
///     auto-spawns the right server (out-of-process, stdio) when a file of a
///     supported language is opened, forwards edits, caches published
///     diagnostics, and answers definition/references lookups.
/// </summary>
/// <remarks>
///     Server processes are external binaries located on PATH
///     (typescript-language-server, pyright/pylsp, gopls, rust-analyzer,
///     csharp-ls). Every operation degrades gracefully when a binary is
///     missing: it logs once and returns an empty result — the agent loop and
///     the editor must never fail because an LSP binary is absent.
/// </remarks>
public interface ILspService : IAsyncDisposable
{
    /// <summary>Raised when diagnostics were re-published for a file.</summary>
    event EventHandler<LspDiagnosticsChangedEventArgs>? DiagnosticsChanged;

    /// <summary>True when a builtin language server covers this file's extension.</summary>
    bool SupportsFile(string filePath);

    /// <summary>
    ///     Open a file: lazily spawn the language server for its language,
    ///     run the initialize handshake, and send <c>textDocument/didOpen</c>.
    ///     Safe to call for unsupported files (no-op) and repeatedly.
    /// </summary>
    ValueTask OpenFileAsync(string filePath, string text, CancellationToken ct = default);

    /// <summary>Push a full-text change for an open file (<c>didChange</c>, full sync).</summary>
    ValueTask NotifyChangeAsync(string filePath, string newText, CancellationToken ct = default);

    /// <summary>Close a file (<c>didClose</c>) and clear its diagnostics cache.</summary>
    ValueTask CloseFileAsync(string filePath);

    /// <summary>Diagnostics currently published for the file (empty when none).</summary>
    ValueTask<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default);

    /// <summary>Resolve the definition at the position (null when none).</summary>
    ValueTask<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct = default);

    /// <summary>Resolve references to the symbol at the position.</summary>
    ValueTask<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct = default);
}
