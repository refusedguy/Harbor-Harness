# Plan — Harbor.Tools.Builtin

## Status: Stable (14-tool facade)

## Done

- [x] 14 builtin tools: `read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task`, `webfetch`, `patch`, `notebook`, `ripgrep`, `tree`, `mcp` (`Tools/` — one subdir per tool)
- [x] All implement `ITool` from `Harbor.Abstractions`
- [x] S2 consolidation: 14 leaf tool projects merged into this facade assembly (`FacadeMarker.cs`)
- [x] Sub-agent validation via `TaskTool` (code / plan / explore) — execution NOT implemented yet, calls fail explicitly (`Tools/Task/TaskTool.cs:11-15`)
- [x] MCP trio: `McpRegistry` + `McpProcessClient` + `McpToolAdapter` under `Tools/Mcp/`, surfaced as the single `mcp` tool; servers from `~/.harbor/mcp.json` / `.harbor/mcp.json` / `HARBOR_MCP_CONFIG` (`Tools/Mcp/`, `src/Harbor.Hosting/Modules/ToolsCatalog.cs:60-72`)
- [x] `webfetch` tool (HTTP GET → markdown, safe-fetch guards)
- [x] `patch` tool (unified diff apply)
- [x] `notebook` scratchpad tool (session-scoped notes)
- [x] `ripgrep` binary-backed fast search
- [x] `tree` directory renderer
- [x] Permission integration (defaults in `PermissionRuleset.Default`, `Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs:95-123`)
- [x] Cancellation token propagation
- [x] Result<T> for all error paths (no exceptions for expected failures)
- [x] Glob matching via `Microsoft.Extensions.FileSystemGlobbing`
- [x] Regex search via `System.Text.RegularExpressions` (compiled, timeout-bounded)
- [x] Bash with timeout, working dir, env vars, stdout/stderr capture, output cap
- [x] Edit with atomic write (temp file + rename)
- [x] Read with line numbers, offset/limit
- [x] Host wiring via `Harbor.Hosting` `ToolsCatalog.CreateToolRegistry` — Full14 (14 tools) or 10-tool subset, then `registry.Freeze()` (`src/Harbor.Hosting/Modules/ToolsCatalog.cs:74-108`)

## TODO

- [ ] Sub-agent execution runner behind `TaskTool` (validation exists; loop does not)
- [ ] `multiedit` tool (batch of edits in one call)
- [ ] Streaming tool output (currently buffered/capped)
- [ ] Tool result truncation policy beyond bash's char cap (long outputs generally)
- [ ] Bash: shell session reuse (persistent cwd, env)

## Known issues

- `bash` caps captured output at `MaxOutputChars = 100_000` chars and truncates (`Tools/Bash/BashTool.cs:107`) — intentional, but tail-heavy commands lose context.
- `task` returns "not implemented" errors — sub-agent execution is missing from this build (`Tools/Task/TaskTool.cs`).
- `edit` requires exact string match — whitespace-sensitive. Could add a fuzzy mode.
- `grep` regex timeout is 2s (`Tools/Grep/GrepTool.cs:76`) — very large repos can hit it. Workaround: narrow with `glob` first.

## Next priorities

1. **P0**: Sub-agent execution (make `task` actually run code/plan/explore loops)
2. **P1**: Streaming tool output (especially `bash` for long-running commands)
3. **P1**: Tool call parallelism polish (independent parallel-mode tools concurrently)
4. **P2**: `multiedit` tool (batch edits)

## Permission defaults review

Current defaults (ask for `write`/`edit`/`bash` outside safe patterns) are balanced by the
prefix allow-list in `PermissionRuleset.Default`. Plan to add:

- Project-local permission profiles
- Auto-allow rules for read-only operations
- "Always allow for this session" prompt option
