# Harbor.Tools.Builtin

The builtin tool set for Harbor — the tools that ship out of the box and are always available to the agent loop. Consolidated facade assembly (S2 merge of 14 leaf tool projects) implementing `ITool` from `Harbor.Abstractions` for: `read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task`, `webfetch`, `patch`, `notebook`, `ripgrep`, `tree`, `mcp`.

> Plugins can add more tools via `IToolPlugin` — see [`docs/PLUGIN_SYSTEM.md`](../../docs/PLUGIN_SYSTEM.md). The full tool reference with examples and the "when to use X vs Y" matrix lives in [`docs/TOOLS_CATALOG.md`](../../docs/TOOLS_CATALOG.md).

## Layer

Infrastructure — concrete `ITool` implementations. References `Harbor.Abstractions` (Domain) and `Harbor.Extensions`.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Extensions`

## Public API

### Tools

| Tool      | Class           | Description                                                                |
|-----------|-----------------|----------------------------------------------------------------------------|
| `read`    | `ReadTool`      | Read a file's contents (with line numbers, optional offset/limit).         |
| `write`   | `WriteTool`     | Write/overwrite a file (creates parent dirs); CAS-guarded.                 |
| `edit`    | `EditTool`      | String-replace within a file (atomic, with old/new preview); CAS-guarded. |
| `bash`    | `BashTool`      | Run a shell command (with timeout, working dir, env vars, output cap).     |
| `glob`    | `GlobTool`      | Find files matching a glob pattern (uses `FileSystemGlobbing`).            |
| `grep`    | `GrepTool`      | Search file contents (regex, file-type filter, multiline mode).            |
| `ls`      | `LsTool`        | List directory contents (with file sizes, dates, ignored entries).         |
| `task`    | `TaskTool`      | Delegate to a sub-agent (`code`, `plan`, or `explore`) — see note below.   |
| `webfetch`| `WebFetchTool`  | Fetch a URL and return its content as markdown.                            |
| `patch`   | `PatchTool`     | Apply a unified diff to files.                                             |
| `notebook`| `NotebookTool`  | Scratchpad notes persisted across turns within a session.                  |
| `ripgrep` | `RipGrepTool`   | Fast content search delegating to the `rg` binary when available.          |
| `tree`    | `TreeTool`      | Render a directory tree.                                                   |
| `mcp`     | `McpToolTool`   | List/call tools exposed by configured MCP servers.                         |

Each tool class implements `ITool`:

```csharp
public sealed class ReadTool : ITool
{
    public ToolName Name { get; }
    public string DisplayName => "Read";
    public string Description => "...";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "read: ...";
    public IReadOnlyList<string> PromptGuidelines { get; }
    public JsonDocument ParameterSchema { get; }

    public Result ValidateArguments(JsonElement args);
    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct);
}
```

### MCP server integration (`Tools/Mcp/`)

MCP is wired as three cooperating pieces plus an adapter, all driven by `mcp.json`
(`~/.harbor/mcp.json`, `<project>/.harbor/mcp.json`, or `HARBOR_MCP_CONFIG`) loaded by
`Harbor.Hosting` (`src/Harbor.Hosting/Modules/ToolsCatalog.cs:61-73`):

- `McpRegistry` (`IMcpRegistry`) — server definitions from config; the agent-facing registry.
- `McpProcessClient` — spawns and talks JSON-RPC stdio to an MCP server process.
- `McpToolAdapter` — wraps a discovered server-side tool as an `ITool`.
- `McpToolTool` — the single LLM-visible `mcp` tool (list servers/tools, invoke).

### Sub-agent definitions

Agent definitions live in `Harbor.Abstractions` (`src/Harbor.Abstractions/Agents/AgentDefinition.cs:65-108`):

- `code` — focused code generation, can call any tool
- `plan` — produces a plan only (read-only)
- `explore` — read-only exploration (no write/edit/bash)

**Note:** `TaskTool` currently validates the requested sub-agent (name, registry membership,
`IsSubAgent` flag) but does **not run it yet** — sub-agent execution is not implemented in
this build and calls return an explicit error instead of fabricating success
(`src/Harbor.Tools.Builtin/Tools/Task/TaskTool.cs:11-15`). Its `PromptGuidelines` tell the
model never to call it.

## Usage

Registration happens via DI in `Harbor.Hosting` (`CreateToolRegistry`,
`src/Harbor.Hosting/Modules/ToolsCatalog.cs:75-109`), not via a registration facade here.
Fourteen tools are registered in every preset (`read`, `write`, `edit`, `bash`, `glob`,
`grep`, `ls`, `skill`, `patch`, `notebook`, `tree`, `lsp`, `harbor_inspect`,
`spill_read`); `task`, `webfetch`, `ripgrep`, `mcp`, `read_mcp_resource`,
`mcp_prompt`, `session_read` and `session_steer` join when the host selects
`HarborToolSetKind.Full14` (22 tools total). `tools.freeze()` runs after
registration, and the CLI's `DisabledTools` config list can keep specific tools out at
startup (`apps/Harbor.App.Cli/Configuration/CliConfig.cs:67-70`).

## Permission model

Each tool call goes through the permission ruleset before invocation. Defaults are defined
in `PermissionRuleset.Default`
(`src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs:95-123`):

- `read`, `glob`, `grep`, `ls`, `tree`, `ripgrep`, `notebook`: allow always
- `write` / `edit` / `patch`: allow under `src/*`, deny `.env*` (edit), ask otherwise
- `bash`: allow-listed safe prefixes (`ls *`, `git log *`, ...), ask by default
- `webfetch`, `mcp`: ask by default
- `task`: allow always (the tool itself errors until sub-agents land)

## File freshness (compare-and-swap)

`read`, `write` and `edit` speak a version token (`Tools/FileVersion.cs`): `sha256:` plus
the first 16 hex characters of the SHA-256 digest of the file's raw bytes.

- `read` reports the token of the file it read (`[file-version: sha256:…]` at the end of
  the output, also in `ToolResult.Metadata.fileVersion`).
- `write` / `edit` accept the token back as `expectedVersion` and refuse to touch a file
  whose bytes moved on since — the write is rejected with a diagnostic that names both
  versions and tells the model to re-read and retry. `write` / `edit` also report the
  token of what they just wrote, so a model can chain writes without re-reading.
- The token is optional. Without it the tools behave exactly as before (#1757) — the
  mandatory "read before write" gate needs a per-session record of what was observed,
  which this assembly has no home for yet.

The digest covers the BYTES, not the decoded text, and not mtime+size: a timestamp is not
a fact about the content (`touch`, a `tar -x` that preserves mtimes, or a filesystem with
coarse timestamp granularity all report "unchanged" for a file that changed). The price is
one sequential read of the file, which `read`/`edit` already pay for the file they touch.

## Known limitations

- `bash` captures stdout+stderr into a capped buffer — output above `MaxOutputChars = 100_000`
  chars is truncated rather than streamed
  (`src/Harbor.Tools.Builtin/Tools/Bash/BashTool.cs:107`).
- `task` validates but does not execute sub-agents (`Tools/Task/TaskTool.cs`).

## See also

- [../../docs/TOOLS_CATALOG.md](../../docs/TOOLS_CATALOG.md) — full tool reference with examples
- [../../docs/PLUGIN_DEVELOPMENT.md](../../docs/PLUGIN_DEVELOPMENT.md) — add new tools via plugins
- [../../docs/PATTERNS.md](../../docs/PATTERNS.md) — `Result<T>` in tool implementations
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — sample tool calls
