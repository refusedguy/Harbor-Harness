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
| `write`   | `WriteTool`     | Write/overwrite a file (creates parent dirs).                              |
| `edit`    | `EditTool`      | String-replace within a file (atomic, with old/new preview).               |
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
`Harbor.Hosting` (`src/Harbor.Hosting/Modules/ToolsCatalog.cs:60-72`):

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
`src/Harbor.Hosting/Modules/ToolsCatalog.cs:74-108`), not via a registration facade here.
Ten tools are always registered; `task`, `webfetch`, `ripgrep`, and `mcp` join when the
host selects `HarborToolSetKind.Full14` (14 tools total). `tools.freeze()` runs after
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
