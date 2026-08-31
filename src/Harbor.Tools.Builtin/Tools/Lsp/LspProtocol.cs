using System.Text;
using System.Text.Json.Serialization;

namespace Harbor.Tools.Lsp;

/// <summary>
///     LSP base-protocol constants: JSON-RPC method names spoken by the
///     Language Server Protocol 3.17 subset Harbor implements, plus the
///     <c>Content-Length</c> framing reader/writer.
/// </summary>
/// <remarks>
///     Framing is byte-precise (ASCII headers + exact-length UTF-8 body) and
///     implemented on raw <see cref="Stream"/>s with no reflection — NativeAOT-safe.
/// </remarks>
public static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidClose = "textDocument/didClose";

    public const string Definition = "textDocument/definition";
    public const string References = "textDocument/references";

    public const string PublishDiagnostics = "textDocument/publishDiagnostics";

    public const string WorkspaceConfiguration = "workspace/configuration";
    public const string RegisterCapability = "client/registerCapability";
    public const string ApplyEdit = "workspace/applyEdit";
    public const string LogMessage = "window/logMessage";
    public const string ShowMessage = "window/showMessage";
}

/// <summary>
///     A single LSP position: 0-based line and 0-based UTF-16 code-unit
///     character offset (the default <c>positionEncoding</c>, which matches
///     C# string indexing semantics).
/// </summary>
public sealed record LspPosition(
    [property: System.Text.Json.Serialization.JsonPropertyName("line")] int Line,
    [property: System.Text.Json.Serialization.JsonPropertyName("character")] int Character);

/// <summary>LSP range — <see cref="LspPosition"/> start/end pair.</summary>
public sealed record LspRange(
    [property: System.Text.Json.Serialization.JsonPropertyName("start")] LspPosition Start,
    [property: System.Text.Json.Serialization.JsonPropertyName("end")] LspPosition End);

/// <summary>A resolved LSP location (file + range) normalized from
/// <c>Location</c>, <c>Location[]</c>, or <c>LocationLink[]</c> results.</summary>
public sealed record LspLocation(
    [property: System.Text.Json.Serialization.JsonPropertyName("uri")] string Uri,
    [property: System.Text.Json.Serialization.JsonPropertyName("range")] LspRange Range);

/// <summary>One published diagnostic (subset of LSP Diagnostic).</summary>
public sealed record LspDiagnostic(
    [property: System.Text.Json.Serialization.JsonPropertyName("range")] LspRange Range,
    [property: System.Text.Json.Serialization.JsonPropertyName("severity")] int? Severity,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("source")] string? Source,
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string? Code)
{
    /// <summary>Severity label used in tool output (1=error … 4=hint).</summary>
    public string SeverityLabel => Severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "info",
        4 => "hint",
        _ => "diagnostic",
    };
}

/// <summary>Client parameters for the <c>initialize</c> request.</summary>
public sealed record LspInitializeParams(
    [property: JsonPropertyName("processId")] int? ProcessId,
    [property: JsonPropertyName("clientInfo")] LspClientInfo ClientInfo,
    [property: JsonPropertyName("rootUri")] string RootUri,
    [property: JsonPropertyName("workspaceFolders")] LspWorkspaceFolder[] WorkspaceFolders,
    [property: JsonPropertyName("capabilities")] LspClientCapabilities Capabilities);

public sealed record LspClientInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record LspWorkspaceFolder(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);

public sealed record LspClientCapabilities(
    [property: JsonPropertyName("general")] LspGeneralCapabilities General,
    [property: JsonPropertyName("textDocument")] LspTextDocumentCapabilities TextDocument,
    [property: JsonPropertyName("workspace")] LspWorkspaceCapabilities Workspace);

public sealed record LspGeneralCapabilities(
    [property: JsonPropertyName("positionEncodings")] string[] PositionEncodings);

public sealed record LspTextDocumentCapabilities(
    [property: JsonPropertyName("synchronization")] LspSyncCapabilities Synchronization,
    [property: JsonPropertyName("publishDiagnostics")] LspPublishDiagnosticsCapabilities PublishDiagnostics);

public sealed record LspSyncCapabilities(
    [property: JsonPropertyName("didSave")] bool DidSave,
    [property: JsonPropertyName("dynamicRegistration")] bool DynamicRegistration);

public sealed record LspPublishDiagnosticsCapabilities(
    [property: JsonPropertyName("relatedInformation")] bool RelatedInformation);

public sealed record LspWorkspaceCapabilities(
    [property: JsonPropertyName("configuration")] bool Configuration,
    [property: JsonPropertyName("didChangeConfiguration")] LspDidChangeConfigurationCapability DidChangeConfiguration);

public sealed record LspDidChangeConfigurationCapability(
    [property: JsonPropertyName("dynamicRegistration")] bool DynamicRegistration);

/// <summary><c>textDocument/didOpen</c> parameters.</summary>
public sealed record LspDidOpenParams(
    [property: JsonPropertyName("textDocument")] LspTextDocumentItem TextDocument);

public sealed record LspTextDocumentItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("languageId")] string LanguageId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("text")] string Text);

/// <summary><c>textDocument/didChange</c> parameters (full-text sync).</summary>
public sealed record LspDidChangeParams(
    [property: JsonPropertyName("textDocument")] LspVersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("contentChanges")] LspContentChange[] ContentChanges);

public sealed record LspVersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int Version);

public sealed record LspContentChange(
    [property: JsonPropertyName("text")] string Text);

/// <summary><c>textDocument/didClose</c> parameters.</summary>
public sealed record LspDidCloseParams(
    [property: JsonPropertyName("textDocument")] LspTextDocumentIdentifier TextDocument);

public sealed record LspTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);

/// <summary>Shared position-bearing request parameters (definition/references).</summary>
public sealed record LspPositionParams(
    [property: JsonPropertyName("textDocument")] LspTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] LspPosition Position);

/// <summary><c>textDocument/references</c> parameters.</summary>
public sealed record LspReferenceParams(
    [property: JsonPropertyName("textDocument")] LspTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] LspPosition Position,
    [property: JsonPropertyName("context")] LspReferenceContext Context);

public sealed record LspReferenceContext(
    [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);

/// <summary>
///     AOT-safe System.Text.Json contract for every type Harbor serializes on
///     the LSP wire. All serialization goes through this
///     <see cref="JsonSerializerContext"/> — no reflection-based
///     <c>Serialize&lt;object&gt;</c> anywhere (§PERF-002).
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LspInitializeParams))]
[JsonSerializable(typeof(LspDidOpenParams))]
[JsonSerializable(typeof(LspDidChangeParams))]
[JsonSerializable(typeof(LspDidCloseParams))]
[JsonSerializable(typeof(LspPositionParams))]
[JsonSerializable(typeof(LspReferenceParams))]
[JsonSerializable(typeof(LspLocation))]
[JsonSerializable(typeof(LspDiagnostic))]
[JsonSerializable(typeof(LspRange))]
internal sealed partial class LspJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
