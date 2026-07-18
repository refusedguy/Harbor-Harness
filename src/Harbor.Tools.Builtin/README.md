# Harbor.Tools.Builtin

The builtin tool set for Harbor — the tools that ship out of the box and are always available to the agent loop. Implements `ITool` from `Harbor.Abstractions` for: `read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task`.

> These are the same tools Claude Code, Cursor, kilocode, etc. ship with. Plugins can add more tools via `IToolPlugin` — see [`docs/PLUGIN_SYSTEM.md`](../../docs/PLUGIN_SYSTEM.md).

## Layer

Infrastructure — concrete `ITool` implementations. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)

## Public API

### Tools

| Tool     | Class                | Description                                                             |
|----------|----------------------|-------------------------------------------------------------------------|
| `read`   | `ReadFileTool`       | Read a file's contents (with line numbers, optional offset/limit).     |
| `write`  | `WriteFileTool`      | Write/overwrite a file (creates parent dirs).                          |
| `edit`   | `EditFileTool`       | String-replace within a file (atomic, with old/new preview).           |
| `bash`   | `BashTool`           | Run a shell command (with timeout, working dir, env vars).             |
| `glob`   | `GlobTool`           | Find files matching a glob pattern (uses `FileSystemGlobbing`).        |
| `grep`   | `GrepTool`           | Search file contents (regex, file-type filter, multiline mode).        |
| `ls`     | `ListFilesTool`      | List directory contents (with file sizes, dates, ignored entries).     |
| `task`   | `TaskTool`           | Delegate to a sub-agent (`code`, `plan`, or `explore`).                |

Each tool class implements `ITool`:

```csharp
public sealed class ReadFileTool : ITool
{
    public ToolDefinition Definition { get; }
    public Result<ToolResult> Invoke(ToolInvocation invocation, CancellationToken ct);
}
```

### Registration helper

```csharp
public static class BuiltinTools
{
    public static IReadOnlyList<ITool> All { get; }    // singleton instances
    public static IReadOnlyList<ToolDefinition> Definitions { get; }
    public static void Register(ToolRegistry registry); // convenience: register all
}
```

### Sub-agent definitions

`TaskTool` uses these agent definitions (defined in `Harbor.Abstractions`):

- `code` — focused code generation, can call any tool
- `plan` — produces a plan only (read-only)
- `explore` — read-only exploration (read, glob, grep, ls — no write/edit/bash)

Each sub-agent runs in its own `AgentLoop` instance with its own session, system prompt, and permission set.

## Usage

Registered via DI in the composition root:

```csharp
// In HostBuilder.cs:
var toolRegistry = new ToolRegistry(logger);
BuiltinTools.Register(toolRegistry);
services.AddSingleton(toolRegistry);
```

Or register individually:

```csharp
services.AddSingleton<ITool, ReadFileTool>();
services.AddSingleton<ITool, WriteFileTool>();
// ...
```

## Permission model

Each tool call goes through `IPermissionService` before invocation. Default rules:

- `read`, `glob`, `grep`, `ls`: allow always
- `task`: allow always (sub-agents inherit parent permissions)
- `write`, `edit`: ask by default (user can approve per call or per pattern)
- `bash`: ask by default (shell injection risk)

Override via `~/.harbor/permissions.json`:

```json
{
  "rules": [
    { "tool": "write", "glob": "src/**", "decision": "allow" },
    { "tool": "write", "glob": "**/.env*", "decision": "deny" },
    { "tool": "bash", "glob": "*", "decision": "ask" }
  ]
}
```

## See also

- [../../docs/TOOLS_CATALOG.md](../../docs/TOOLS_CATALOG.md) — full tool reference with examples
- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md) — add new tools via `IToolPlugin`
- [../../docs/PATTERNS.md](../../docs/PATTERNS.md) — `Result<T>` in tool implementations
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — sample tool calls
