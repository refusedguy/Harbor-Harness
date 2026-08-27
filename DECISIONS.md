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

---

# ADR-002: Result-rail refactors — ROP-B/C/D waves

## Status
Accepted (closed)

## Date
2026-08-24 .. 2026-08-26 (git log: ROP-B residuals through `aea5592`, ROP-C Z1..Z3 `d883535`..`35e3aab`, ROP-D tail `ec04960`..`82edb0a`)

## Context
docs/CODE_PRINCIPLES_AUDIT.md listed critical ROP findings (§ROP-001 masking errors as null, §ROP-002 unchecked `.Value`, manual catch→Failure ladders) repeated across storage, tools, config, and agent-loop code. Each conversion re-introduced subtle drift (e.g. OCE/cancellation masked as store failure).

## Decision
Migrate error handling to CSE-style `Result` rails as the single canon: `Result.Try(...)` + `ResultErrors.Message`; bind-rail chains (`Bind`/`Map`/`Ensure`) for prelude and guard ladders; cancellation propagates through exception filters instead of being converted to `Failure`. The interim `ResultGuard` helper was deleted once §4.5 had a single canon (`9e954a5`). Enforcement moved to BannedApi wiring for legacy `.GetResult()` sites (`be81e42`).

## Consequences
- No manual catch→Failure blocks remain in migrated zones; new violations are mechanically banned.
- Diagnostics improve: source-local errors instead of swallowed catches.
- Verification at close: Release build 0 errors; per-project TUnit runs green except the historically known Avalonia-headless/IPC flakes (`bdac0d4`).

# ADR-003: ConsoleEx — second in-process terminal renderer (CE-0..CE-5)

## Status
Accepted (MVP complete)

## Date
2026-08-25 .. 2026-08-27 (design bible `1c6455b` → scaffold `069640f` → MVP marks `14e87ab` → PTY/research wave `8fa93d5`, `8f3d93b`)

## Context
The default interactive shell compiled from contrib (`Spectre.Tui`/`Fullscreen`) depends on third-party rendering stacks with allocation-heavy redraws and limited input control. A codex-style inline REPL needs exact raw-mode input handling (kitty keyboard protocol, SGR mouse, bracketed paste) and zero-allocation steady-state rendering budgets that those stacks cannot guarantee.

## Decision
Ship `Harbor.Tui.ConsoleEx` as a **second** render path inside the existing CLI process: own input pipeline (escape-sequence state machine, kitty/mouse/paste decode), cell-grid screen buffer with fused full-scan diff engine, virtualized chat timeline with streaming markdown + unified-diff blocks, event-driven frame loop (wake channel + 80 ms spinner tick). Opt-in only: `HARBOR_TUI=consoleex`, config `"tui": "consoleex"` or `cli.json defaultTuiRenderer`; kill-switch `ui.consoleEx.enabled` rolls back to the legacy renderer. Verified by golden grid-dump suites (CE-2/CE-3), perf-budget tests (0 allocations steady-state), a live-REPL E2E smoke with golden frame (`0148ceb`), and a real-PTY harness with 8 scenarios (CE-5, incl. termios struct-size crash fix `1749841`).

## Consequences
- Legacy renderers unchanged; fallback path keeps consoleex non-breaking.
- Raw-mode platform differences (termios, VMIN=1, Ctrl+C windows, lifetime bootstrap DI) are covered by PTY e2e rather than unit mocks.
- Remaining gaps documented in `src/Harbor.Tui.ConsoleEx/README.md` (MVP limitations) before graduation to default.

# ADR-004: Sub-agent execution behind the `task` tool

## Status
Accepted

## Date
2026-08-20 .. 2026-08-25 (`TaskTool` rework `7b01045`; honest-failure fix `10f6857`; rail conversion `e0aebe0`)

## Context
Complex prompts need delegation to focused child agents. Earlier iterations of `TaskTool` fabricated queued success even when nothing ran.

## Decision
Sub-agent execution lives behind the builtin `task` tool which resolves an agent by name from `IAgentRegistry` (`Bind(name => _agents.GetAgent(name))`). Registry ships builtin agents `code`, `plan`, `explore` (`src/Harbor.Registries/Agents/AgentRegistry.cs`), each with its own permission ruleset. Unsupported paths report honest failure instead of fake success.

## Consequences
- Permission boundaries of the parent do not leak: child agent permissions come from its own ruleset.
- Tool-level Result rails match ADR-002 conventions.

# ADR-005: Plugin hosting split into layered projects

## Status
Accepted

## Date
2026-07-18 (`3415002` split Plugins.Runtime into layered sub-projects; `ce522a9` adds Plugins.Storage) .. 2026-08-22 (`0c86704` out-of-process MCP/plugin host → `Harbor.Plugins.Host`)

## Context
A single monolithic loader (`Plugins.Runtime`) mixed discovery, compilation (Roslyn CS-source), registration sink semantics, instantiation, hosting lifecycle, and persistence. Collectible `AssemblyLoadContext` is not viable under NativeAOT, so lifecycle concerns needed seams suitable for both in-process Roslyn loading and out-of-process hosts.

## Decision
Split plugin support into `Harbor.Plugins.{Abstractions, Compilation, Instantiation, Registration, Hosting, Runtime, Host, Storage}`: Abstractions define contracts (e.g. `IPluginLoadHost` registration sink), Compilation does Roslyn CS-source compilation, Registration collects contributions, Instantiation constructs plugins, Hosting owns lifecycle, Runtime preserves the public discovery/CS-loader API, Host covers the separate-process scenario, Storage persists plugin state.

## Consequences
- Each layer is independently testable (`tests/Harbor.Plugins.Runtime.Tests`).
- Architecture-test matrix covers all main-solution assemblies including the split (`5d2df19`).
- The old narrative "plugins == Roslyn runtime only" is outdated; DLL-based sample plugins and CS-source plugins coexist.

# ADR-006: MCP adopted as builtin tool adapter over out-of-process servers

## Status
Accepted

## Date
2026-07-18 (core registry `76646ad`) .. 2026-08-16 (tool integration `05bef5f`) .. 2026-08-22 (out-of-process host `0c86704`; argv hardening `6f18b65`) .. 2026-08-25 (instructions aggregation `64cbc0e`)

## Context
Model Context Protocol servers provide portable tool ecosystems, but running them in-process would couple the AOT-bound core to third-party runtimes and complicate crashes/timeouts.

## Decision
Model Context Protocol servers are consumed out-of-process over stdio JSON-RPC: `Harbor.Tools.Builtin/Tools/Mcp/` provides `McpRegistry`, `McpProcessClient`, `McpToolAdapter`, argv parser, source-generated serialization context (AOT-safe), and `mcp.json` config loading. One synthetic tool named `mcp` exposes discovered server tools to the LLM instead of spawning a separate runtime in-proc; server instructions are aggregated into the system prompt via `IMcpRegistry.GetInstructions`. Sample servers under `samples/mcp/`.

## Consequences
- Crashing/slow MCP servers cannot take down the agent loop.
- AOT constraints satisfied without reflection-based serialization.
- Single-tool surface keeps prompt/tool-catalog size bounded regardless of server count.

# ADR-007: Single ProviderPresets catalog for both wizards (PROD-UI-0)

## Status
Accepted (closed)

## Date
2026-08-26 (`e47def0`..`bada559`)

## Context
CLI onboarding and the Avalonia wizard drifted: duplicated provider preset tables produced inconsistencies (qwen drift) and divergent auth-field behavior.

## Decision
Both wizards read one catalog — `ProviderPresets` — aligned field-by-field with `providers/*.json` ids and auth env vars (preset↔json consistency test `bada559`). Supporting UX upgrades shipped alongside: `IProviderHealthCheck` "Test connection" button in both wizards (`051e369`), `/model` rebinding the active session without restarting the REPL (`4003bf1`), and a live model list with explicit degradation when the endpoint fails (`0d96ab2`).

## Consequences
- Adding/changing a provider means editing one preset table plus `providers/<name>.json`; the consistency test fails on drift.
- Users get connection feedback during setup instead of first-prompt failures.

# ADR-008: Reverse the Domain split — Harbor.Abstractions.Contracts (F1 decoupling)

## Status
Accepted (closed)

## Date
2026-08-24 (`fa8d3ae` full decoupling; follow-up R30 fix `e9abaaa` docs pass)

## Context
The v0.3 decision put value objects, entities, events and permission models into a separate `Harbor.Domain.dll`, leaving `Harbor.Abstractions` as pure interfaces. In practice nearly every consumer needed both assemblies (an interface and its DTO types), so the split bought no isolation but doubled the surface: two csproj files to touch for any contract change, plugin-compilation had to reference the domain assembly explicitly via `typeof(Session).Assembly`, and the "Abstractions = interfaces only" rule was enforced only by convention while the practical boundary sat elsewhere.

## Decision
Reverse the split: rename `Harbor.Domain.dll` to `Harbor.Abstractions.Contracts`. Contract types (models, events, ValueObjects, `PermissionRuleset`) live there with a deliberately small dep set (BCL + CSharpFunctionalExtensions + MemoryPack); `Harbor.Abstractions` keeps the pure interface layer and takes a ProjectReference on Contracts instead of owning the types. Namespaces stay `Harbor.Abstractions.Models.*` — only the assembly name and project move, so source-level references are unaffected. See the high-level Decision Log in [docs/ROADMAP.md](docs/ROADMAP.md).

## Consequences
- One project to edit for any contract change; consumers that need only DTOs can take just `Harbor.Abstractions.Contracts` without the full interface stack (useful for plugin compilation, which previously needed an explicit `typeof(Session).Assembly` reference).
- Dependency direction is fixed by architecture tests: Contracts must not grow heavier deps; Abstractions may depend on Contracts, never the reverse.
- Historical note in ROADMAP Decision Log updated accordingly; per-project docs reconciled during DOCS-ZERO (2026-08-27).
