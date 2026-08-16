# Plan — Harbor.Tools.Builtin

## Status: Stable

## Done

- [x] 8 builtin tools: `read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task`
- [x] All implement `ITool` from `Harbor.Abstractions`
- [x] `BuiltinTools.All` + `BuiltinTools.Register(registry)` convenience
- [x] Sub-agent delegation via `TaskTool` (code / plan / explore)
- [x] Permission integration (every call goes through `IPermissionService`)
- [x] Cancellation token propagation
- [x] Result<T> for all error paths (no exceptions for expected failures)
- [x] Glob matching via `Microsoft.Extensions.FileSystemGlobbing`
- [x] Regex search via `System.Text.RegularExpressions` (compiled, timeout-bounded)
- [x] Bash with timeout, working dir, env vars, stdout/stderr capture
- [x] Edit with atomic write (temp file + rename)
- [x] Read with line numbers, offset/limit

## TODO

- [ ] `patch` tool (unified diff apply)
- [ ] `multiedit` tool (batch of edits in one call)
- [ ] `notebookedit` tool (Jupyter notebooks)
- [ ] `webfetch` tool (HTTP GET -> markdown)
- [ ] `websearch` tool (moved to plugin? — currently a sample plugin)
- [ ] Streaming tool output (currently buffered)
- [ ] Tool call parallelism (currently sequential per turn)
- [ ] Tool result truncation policy (long outputs)
- [ ] Bash: shell session reuse (persistent cwd, env)

## Known issues

- `bash` tool captures stdout+stderr as a single string — large outputs (10MB+) can OOM. Mitigation: 1MB truncation cap.
- `edit` tool requires exact string match — whitespace-sensitive. Could add a fuzzy mode.
- `grep` regex timeout is 5s — very large repos can hit it. Workaround: narrow with `glob` first.
- `task` sub-agents can't be canceled individually yet — parent cancel cascades (planned).

## Next priorities

1. **P0**: Streaming tool output (especially `bash` for long-running commands)
2. **P1**: `multiedit` tool (batch edits)
3. **P1**: Tool call parallelism (independent tools run concurrently)
4. **P2**: `patch` tool (unified diff)
5. **P2**: `notebookedit` for Jupyter
6. **P2**: Bash shell session reuse

## Permission defaults review

Current defaults (`ask` for `write`/`edit`/`bash`) can be noisy for trusted workflows. Plan to add:

- Project-local permission profiles (`~/.harbor/projects/<path>/permissions.json`)
- Auto-allow rules for read-only operations
- "Always allow for this session" prompt option
