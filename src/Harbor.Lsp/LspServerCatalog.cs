using System.Text.Json.Serialization;

namespace Harbor.Lsp;

/// <summary>One builtin language server definition.</summary>
/// <param name="Id">Stable identifier (e.g. <c>typescript</c>).</param>
/// <param name="Language">Human-readable language name for logs.</param>
/// <param name="Command">Executable launched on PATH.</param>
/// <param name="Args">Standard stdio-mode arguments.</param>
/// <param name="Extensions">File extensions (with dot, lowercase) handled by this server.</param>
public sealed record LspServerDefinition(
    string Id,
    string Language,
    string Command,
    string[] Args,
    IReadOnlyList<string> Extensions)
{
    /// <summary>typescript-language-server (ts/tsx/js/jsx).</summary>
    public static LspServerDefinition TypeScript { get; } = new(
        "typescript", "TypeScript",
        "typescript-language-server", ["--stdio"],
        [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);

    /// <summary>pyright-langserver (py).</summary>
    public static LspServerDefinition Python { get; } = new(
        "python", "Python",
        "pyright-langserver", ["--stdio"],
        [".py", ".pyi"]);

    /// <summary>gopls (go).</summary>
    public static LspServerDefinition Go { get; } = new(
        "go", "Go",
        "gopls", ["run"],
        [".go"]);

    /// <summary>rust-analyzer (rs).</summary>
    public static LspServerDefinition Rust { get; } = new(
        "rust", "Rust",
        "rust-analyzer", [],
        [".rs"]);

    /// <summary>csharp-ls (cs).</summary>
    public static LspServerDefinition CSharp { get; } = new(
        "csharp", "C#",
        "csharp-ls", [],
        [".cs"]);

    /// <summary>clangd (C/C++).</summary>
    public static LspServerDefinition Clangd { get; } = new(
        "clangd", "C/C++",
        "clangd", [],
        [".c", ".h", ".cpp", ".hpp", ".cc", ".cxx"]);

    /// <summary>Eclipse JDT language server (Java).</summary>
    public static LspServerDefinition Java { get; } = new(
        "java", "Java",
        "jdtls", [],
        [".java"]);

    /// <summary>vscode-html-language-server (HTML).</summary>
    public static LspServerDefinition Html { get; } = new(
        "html", "HTML",
        "vscode-html-language-server", ["--stdio"],
        [".html", ".htm"]);

    /// <summary>vscode-css-language-server (CSS/SCSS/Less).</summary>
    public static LspServerDefinition Css { get; } = new(
        "css", "CSS",
        "vscode-css-language-server", ["--stdio"],
        [".css", ".scss", ".less"]);

    /// <summary>vscode-json-language-server (JSON).</summary>
    public static LspServerDefinition Json { get; } = new(
        "json", "JSON",
        "vscode-json-language-server", ["--stdio"],
        [".json", ".jsonc"]);

    /// <summary>lua-language-server (Lua).</summary>
    public static LspServerDefinition Lua { get; } = new(
        "lua", "Lua",
        "lua-language-server", [],
        [".lua"]);

    /// <summary>The builtin language servers, in default startup order.</summary>
    public static IReadOnlyList<LspServerDefinition> Builtin { get; } =
    [
        TypeScript, Python, Go, Rust, CSharp,
        Clangd, Java, Html, Css, Json, Lua,
    ];

    /// <summary>True when this server handles the file (by extension, case-insensitive).</summary>
    public bool Handles(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        // ReSharper disable once LoopCanBeConvertedToQuery — hot-ish path, keep it allocation-free
        foreach (string ext in Extensions)
        {
            if (string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

/// <summary>LSP wire DTOs. Flat, explicit, and AOT-safe — all serialization goes
/// through <see cref="LspJsonContext" /> (§PERF-002: no reflection serialization).</summary>
public static class LspWire
{
    /// <summary><c>initialize</c> request parameters.</summary>
    public sealed record InitializeParams(
        [property: JsonPropertyName("processId")] int? ProcessId,
        [property: JsonPropertyName("rootUri")] string? RootUri,
        [property: JsonPropertyName("capabilities")] ClientCapabilities Capabilities);

    /// <summary>Client capabilities — intentionally minimal (we only consume).</summary>
    public sealed record ClientCapabilities(
        [property: JsonPropertyName("textDocument")] TextDocumentCapabilities? TextDocument = null);

    /// <summary>Synchronization capability advertisement (full-sync consumer).</summary>
    public sealed record TextDocumentCapabilities(
        [property: JsonPropertyName("synchronization")] SyncCapabilities? Synchronization = null);

    /// <summary>Synchronization sub-capabilities.</summary>
    public sealed record SyncCapabilities(
        [property: JsonPropertyName("dynamicRegistration")] bool DynamicRegistration = false,
        [property: JsonPropertyName("didSave")] bool DidSave = false);

    /// <summary><c>textDocument/didOpen</c> parameters.</summary>
    public sealed record DidOpenTextDocumentParams(
        [property: JsonPropertyName("textDocument")] TextDocumentItem TextDocument);

    /// <summary>An opened text document.</summary>
    public sealed record TextDocumentItem(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("languageId")] string LanguageId,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("text")] string Text);

    /// <summary><c>textDocument/didChange</c> parameters (full-text sync).</summary>
    public sealed record DidChangeTextDocumentParams(
        [property: JsonPropertyName("textDocument")] VersionedTextDocumentIdentifier TextDocument,
        [property: JsonPropertyName("contentChanges")] IReadOnlyList<FullTextChange> ContentChanges);

    /// <summary>A full-text content change (no range — full sync).</summary>
    public sealed record FullTextChange(
        [property: JsonPropertyName("text")] string Text);

    /// <summary>Versioned document identifier for changes.</summary>
    public sealed record VersionedTextDocumentIdentifier(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("version")] int Version);

    /// <summary><c>textDocument/didClose</c> parameters.</summary>
    public sealed record DidCloseTextDocumentParams(
        [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

    /// <summary>Document identifier without version.</summary>
    public sealed record TextDocumentIdentifier(
        [property: JsonPropertyName("uri")] string Uri);

    /// <summary><c>textDocument/definition</c> and <c>textDocument/references</c> parameters.</summary>
    public sealed record PositionParams(
        [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
        [property: JsonPropertyName("position")] LspPosition Position,
        [property: JsonPropertyName("context")] ReferenceContext? Context = null);

    /// <summary>A zero-based position.</summary>
    public sealed record LspPosition(
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("character")] int Character);

    /// <summary>References request context.</summary>
    public sealed record ReferenceContext(
        [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);

    /// <summary>A protocol Location (uri + range).</summary>
    public sealed record LspLocationDto(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("range")] LspRange Range);

    /// <summary>A start/end range.</summary>
    public sealed record LspRange(
        [property: JsonPropertyName("start")] LspPosition Start,
        [property: JsonPropertyName("end")] LspPosition End);

    /// <summary><c>textDocument/publishDiagnostics</c> notification.</summary>
    public sealed record PublishDiagnosticsParams(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("version")] int? Version,
        [property: JsonPropertyName("diagnostics")] IReadOnlyList<DiagnosticDto> Diagnostics);

    /// <summary>One published diagnostic.</summary>
    public sealed record DiagnosticDto(
        [property: JsonPropertyName("range")] LspRange Range,
        [property: JsonPropertyName("severity")] int? Severity,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("message")] string Message);
}

/// <summary>
///     AOT-safe System.Text.Json contract for the LSP wire format.
///     Case-sensitive member names (LSP is camelCase) — no naming policy needed.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LspWire.InitializeParams))]
[JsonSerializable(typeof(LspWire.DidOpenTextDocumentParams))]
[JsonSerializable(typeof(LspWire.DidChangeTextDocumentParams))]
[JsonSerializable(typeof(LspWire.DidCloseTextDocumentParams))]
[JsonSerializable(typeof(LspWire.PositionParams))]
[JsonSerializable(typeof(LspWire.PublishDiagnosticsParams))]
internal sealed partial class LspJsonContext : JsonSerializerContext;
