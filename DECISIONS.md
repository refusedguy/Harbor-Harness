# ADR-001: Variant V1 — Production Stabilization

## Status
Accepted

## Context
The Harbor codebase has reached ~70% completion of the original refactoring spec. Most major architectural changes (AgentLoop decomposition, OpenAiSseParser extraction, MCP core, RemoteGateway, DaemonCommand, ActivitySource telemetry) are already implemented. What remains are integration gaps, stubs, and project-structure cleanup.

## Decision
Chose **Variant V1 (narrowest)** from the recon options.

### What we change
1. **Solution restructuring** — move WPF, MAUI, Sixel, Termina, TerminalGui, RazorConsole from `Harbor.slnx` → `Harbor.Samples.slnx`. Keep only production-ready CLI + core TUI (Ansi, Plain, Spectre*, Notifications) in main solution.
2. **TerminalQrRenderer** — implement Unicode half-block QR generator (█ ▀ ▄) without GDI dependencies.
3. **MCP AOT compliance** — add `JsonSerializerContext` source generation to `McpJsonRpcTransport`; add `harbor.mcp.json` config file support in `HostBuilder`.
4. **IPC timing tests** — write 4–6 tests using `Channel<T>` / `TaskCompletionSource` instead of `Task.Delay`; cover connect, subscribe, dispose races on Linux/macOS.
5. **BuildRequest perf** — replace `Dictionary<string, object?>` + reflection `JsonSerializer.Serialize` with `Utf8JsonWriter` writing directly to the HTTP content stream.

### What we consciously do NOT change
- Existing architectural layering (already enforced by 46 architecture tests)
- Public interfaces (`ILlmClient`, `ITool`, `IHarborClient`, `IMcpRegistry`)
- Harbor.Core → Application/Registries split (already done)
- Existing test suite (no breaking changes to passing tests)
- DI container structure

## Consequences
- `Harbor.slnx` compiles faster (fewer projects, no desktop workloads)
- `Harbor.Samples.slnx` becomes the home for experimental/desktop UI
- MCP tools are AOT-safe
- QR codes work in pure terminal environments
- IPC tests are deterministic on Linux

## Alternatives considered
- **V2 (ideal architecture)** — would introduce a new store/record layer, rewrite runtime, add second message bus. Rejected: over-engineering for current pain; spec warns against this explicitly.
- **V3 (skip restructuring)** — keep experimental UI in main solution. Rejected: spec explicitly demands CI noise reduction; architecture tests already enforce layering.
