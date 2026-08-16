# Plan - Harbor.Tools.Mcp

## Status: MVP

Part of the Harbor tool split (S2): one of 14 leaf tool projects extracted out of the old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains as a thin facade that references all 14 leaves so existing consumers keep compiling.

## Done

- [x] `McpTool` implementing `ITool` from `Harbor.Abstractions`
- [x] Tool definition (name, description, JSON-schema parameters)
- [x] Result<T> for all error paths
- [x] Cancellation token propagation
- [x] Permission integration (every call goes through `IPermissionService`)
- [x] Referenced by `Harbor.Tools.Builtin` facade (backward compat)

## TODO

- [ ] Unit tests (edge cases, error paths, cancellation)
- [ ] Integration test in `Harbor.Tools.Builtin.Tests`
- [ ] Streaming output for long-running invocations (where applicable)
- [ ] Result truncation policy (long outputs capped at 1MB)
- [ ] Performance benchmark in `Harbor.Benchmarks`

## Known issues

- Requires real IMcpRegistry impl
- Result truncation cap is 1MB - longer outputs are silently truncated.

## Next priorities

1. **P0**: Unit tests (parameter validation, error paths, cancellation)
2. **P1**: Streaming output (where applicable)
3. **P1**: Result truncation policy with a "show more" hint
4. **P2**: Benchmark in Harbor.Benchmarks

## See also

- [README.md](README.md)
- [../../docs/TOOLS_CATALOG.md](../../docs/TOOLS_CATALOG.md)
- [../Harbor.Tools.Builtin/README.md](../Harbor.Tools.Builtin/README.md) - facade that aggregates all 14 leaf tools
