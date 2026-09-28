# Harbor.Lsp

Harbor's Language Server Protocol client. It auto-spawns language servers
**out of process** over stdio JSON-RPC and exposes diagnostics, go-to-definition
and find-references to the editor surfaces and to the agent's `lsp` tool.

Eleven servers ship built in (`LspServerCatalog.Builtin`,
[`LspServerCatalog.cs:85`](./LspServerCatalog.cs)): TypeScript, Python, Go, Rust,
C#, clangd (C/C++), Java, HTML, CSS, JSON and Lua. AOT-safe — all JSON goes
through source-generated contexts, never reflection.

## What it is

An **Infrastructure-layer** project implementing
`Harbor.Abstractions.Lsp.ILspService`. It spawns language servers as child
processes and speaks LSP over stdio JSON-RPC; it renders nothing and holds no
UI state.

## Public API

The contract lives in `Harbor.Abstractions`
([`Lsp/ILspService.cs`](../Harbor.Abstractions/Lsp/ILspService.cs:65)) so that
consumers depend on the interface, never on this implementation.
`LspManager` is the implementation:

```csharp
public sealed class LspManager : ILspService
{
    public LspManager(ILogger<LspManager> logger,
                      IReadOnlyList<LspServerDefinition>? definitions = null);

    public bool SupportsFile(string filePath);
    public ValueTask OpenFileAsync(string filePath, string text, CancellationToken ct = default);
    public ValueTask NotifyChangeAsync(string filePath, string newText, CancellationToken ct = default);
    public ValueTask CloseFileAsync(string filePath);
    public ValueTask<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default);
    public ValueTask<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct = default);
    public ValueTask<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct = default);
    public ValueTask DisposeAsync();

    public static string FindWorkspaceRoot(string filePath);
}
```

Supporting types:

- `LspServerDefinition` (`LspServerCatalog.cs:11`) — one builtin definition:
  `Id`, `Language`, `Command`, `Args`, `Extensions`, plus `Handles(filePath)`.
  Static presets (`TypeScript`, `Python`, `Go`, `Rust`, `CSharp`, `Clangd`,
  `Java`, `Html`, `Css`, `Json`, `Lua`) and the `Builtin` list.
- `LspServerSession` — one live server process. `StartAsync` spawns it and
  completes the `initialize` handshake within `InitializeTimeout` (15s,
  `LspServerSession.cs:17`); then `OpenAsync`/`ChangeAsync`/`CloseAsync`,
  `FindDefinitionAsync`/`FindReferencesAsync`, `GetDiagnostics(filePath)`, and
  the `DiagnosticsChanged` event. `FileUri`/`FromUri` convert paths.
- `LspClient` — the raw JSON-RPC transport over a pair of streams:
  `Start()`, `SendRequestAsync`, `SendNotificationAsync`, plus
  `ServerNotification` and `Disconnected` events. `LspRequestException` is the
  failure type.
- `LspWire` (`LspServerCatalog.cs:107`) — the source-generated wire records
  (`InitializeParams`, `ClientCapabilities`, `TextDocumentCapabilities`,
  `SyncCapabilities`, `DidOpenTextDocumentParams`).

## Wiring

Registered as a **singleton** in
[`RegistriesModule.cs:54-56`](../Harbor.Hosting/Modules/RegistriesModule.cs):

```csharp
var lspService = new Harbor.Lsp.LspManager(
    ctx.LoggerFactory.CreateLogger<Harbor.Lsp.LspManager>());
services.AddSingleton<Harbor.Abstractions.Lsp.ILspService>(lspService);
```

It is constructed eagerly rather than resolved from DI, and
[`ToolsCatalog.cs:91`](../Harbor.Hosting/Modules/ToolsCatalog.cs) takes an
optional `ILspService` to hand to the agent-facing `lsp` tool. There is **no
`HARBOR_*` env var and no config switch** — the service is always registered;
servers spawn lazily, on first use for a matching file extension.

`Harbor.Hosting` carries an unconditional `ProjectReference`, so this assembly
is present in every full build.

## Usage

```csharp
using Harbor.Abstractions.Lsp;
using Harbor.Lsp;

// Resolved from DI in the CLI/host; constructed directly in tests.
ILspService lsp = new LspManager(logger);

if (!lsp.SupportsFile("src/App/Program.cs"))
    return;

await lsp.OpenFileAsync(path, text, ct);
await lsp.NotifyChangeAsync(path, newText, ct);

IReadOnlyList<LspDiagnostic> diagnostics = await lsp.GetDiagnosticsAsync(path, ct);
LspLocation? def = await lsp.FindDefinitionAsync(path, line: 12, column: 4, ct);
```

Bringing your own server catalog instead of the eleven builtins:

```csharp
var lsp = new LspManager(logger, [LspServerCatalog.TypeScript, myServer]);
```

## Dependencies

| Reference | Why |
|-----------|-----|
| `Harbor.Abstractions` | `ILspService` and the `Lsp*` contract types |
| `Microsoft.Extensions.Logging.Abstractions` | `ILogger` |
| `CSharpFunctionalExtensions` | `Result`/`Maybe` boundaries over foreign JSON-RPC payloads (§E1, #200) |

Referenced by `src/Harbor.Hosting` (the singleton above) and
`tests/Harbor.Lsp.Tests`. The §ARCH rule is that Infrastructure references
Domain (`Harbor.Abstractions`) **only** — no edge to `Harbor.Ui.Framework.*` or
any other project. Enforced by `tests/Harbor.Architecture.Tests`.

## Known limitations

- **The server binaries must be installed separately.** Harbor spawns them from
  `PATH`; it does not download or manage them. A missing `typescript-language-server`
  surfaces as a spawn failure, not an auto-install.
- **Diagnostics are the only push channel.** `DiagnosticsChanged` is raised for
  pull requests; there is no workspace-symbol, rename, formatting or
  code-action support.
- **`line`/`column` are 0-based** on this API and converted to the LSP's
  0-based `Position` internally. Editor surfaces that think in 1-based lines
  must subtract.
- **One session per language, not per workspace.** Sessions are keyed by
  `LspServerDefinition`; concurrent workspaces for the same language share a
  server process.
- **`Clangd` covers C and C++ only** — there is no MSVC/clangd-on-Windows
  definition.
- Failure handling uses ROP boundaries (`Result`) rather than exceptions for
  expected server errors; `LspRequestException` is reserved for transport faults.

## See also

- [`../Harbor.Abstractions/README.md`](../Harbor.Abstractions/README.md) — the `ILspService` contract's home
- [`../Harbor.Tools.Builtin/README.md`](../Harbor.Tools.Builtin/README.md) — the agent-facing `lsp` tool
- [`../../docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) — layering rules
