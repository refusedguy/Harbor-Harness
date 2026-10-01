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
- Harbor.Core → Harbor.Application / Harbor.Registries split (done; the empty facade itself deleted in #451)
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
- Remaining gaps documented in `src/Harbor.Tui.CellForge/README.md` (project renamed from `Harbor.Tui.ConsoleEx`; the `consoleex` id remains a backend alias) — MVP limitations before graduation to default.

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
Accepted (closed) — superseded in part by #580 (2026-09-29): the catalog is now
*derived* from `providers/*.json` rather than kept aligned with it by a test.

## Date
2026-08-26 (`e47def0`..`bada559`)

## Context
CLI onboarding and the Avalonia wizard drifted: duplicated provider preset tables produced inconsistencies (qwen drift) and divergent auth-field behavior.

## Decision
Both wizards read one catalog — `ProviderPresets` — aligned field-by-field with `providers/*.json` ids and auth env vars (preset↔json consistency test `bada559`). Supporting UX upgrades shipped alongside: `IProviderHealthCheck` "Test connection" button in both wizards (`051e369`), `/model` rebinding the active session without restarting the REPL (`4003bf1`), and a live model list with explicit degradation when the endpoint fails (`0d96ab2`).

## Consequences
- Adding/changing a provider means editing one preset table plus `providers/<name>.json`; the consistency test fails on drift.
- Users get connection feedback during setup instead of first-prompt failures.

### Amendment (#580)
The duplication this ADR merged was re-introduced by its own guard: the preset↔json
consistency test turned the documented "add a provider = drop a `.json`" path
(docs/EXAMPLES.md §9) into a red build, which is the loudest possible contradiction
of the axis' main promise. `ProviderPresets.All` is now computed by
`ProviderPresetCatalog` from the same configs, over the same directory walk the
registration path uses; the table and its three policing assertions are gone, and
`providers/<name>.json` — now also carrying `defaultModel` / `setupHint` / `priority`
— is again the whole change. The "one catalog for both wizards" intent is unchanged,
and now holds by construction rather than by test.

# ADR-008: Reverse the Domain split — Harbor.Abstractions.Contracts (F1 decoupling)

## Status
Accepted (closed)

## Date
2026-08-24 (`fa8d3ae` full decoupling; follow-up R30 fix `e9abaaa` docs pass)

## Context
The v0.3 decision put value objects, entities, events and permission models into a separate `Harbor.Domain.dll`, leaving `Harbor.Abstractions` as pure interfaces. In practice nearly every consumer needed both assemblies (an interface and its DTO types), so the split bought no isolation but doubled the surface: two csproj files to touch for any contract change, plugin-compilation had to reference the domain assembly explicitly via `typeof(Session).Assembly`, and the "Abstractions = interfaces only" rule was enforced only by convention while the practical boundary sat elsewhere.

## Decision
Reverse the split: rename `Harbor.Domain.dll` to `Harbor.Abstractions.Contracts`. Contract types (models, events, ValueObjects, `PermissionRuleset`) live there with a deliberately small dep set (BCL + CSharpFunctionalExtensions + MemoryPack); `Harbor.Abstractions` keeps the pure interface layer and takes a ProjectReference on Contracts instead of owning the types. Namespaces stay `Harbor.Abstractions.Models.*` — only the assembly name and project move, so source-level references are unaffected. See the high-level Decision Log in [docs/ROADMAP.md](../ROADMAP.md).

## Consequences
- One project to edit for any contract change; consumers that need only DTOs can take just `Harbor.Abstractions.Contracts` without the full interface stack (useful for plugin compilation, which previously needed an explicit `typeof(Session).Assembly` reference).
- Dependency direction is fixed by architecture tests: Contracts must not grow heavier deps; Abstractions may depend on Contracts, never the reverse.
- Historical note in ROADMAP Decision Log updated accordingly; per-project docs reconciled during DOCS-ZERO (2026-08-27).

---

# ADR-009: ICommonConfigModelRefReader lives in Ui.Framework.Abstractions (circular-dependency resolution)

## Status
Accepted (implemented by 793c998 Ui.Framework split; recorded 2026-09-04)
Amended by #453 (2026-09-30): the interface was renamed and its carrier changed; the decision to
keep two contracts is unchanged and now has a guard. The "Context" below also misstated the edge
that causes the cycle — corrected here.

## Context
Issue #20: SessionFactory (Ui.Framework) needs the persisted provider/model choice, but the full config store (JsonCommonConfigStore) lives in Harbor.Desktop.Abstractions, so Ui.Framework could not reference it back. Options were: merge Desktop.Abstractions into Ui.Framework, split Terminal.Abstractions, or keep a narrow interface with a documented boundary.

The edge is a DIRECT `ProjectReference` from Harbor.Desktop.Abstractions to Harbor.Ui.Framework, plus one to each of `Ui.Framework.ViewModels`, `.State`, `.Services`, `.Sessions` and `.Rendering`. An earlier version of this ADR named `Desktop.Abstractions -> Terminal.Abstractions -> Ui.Framework` as the cause; Terminal.Abstractions is one more edge in the same direction, not the one that closes the loop (#453). The cycle is real either way, which is why the decision below holds.

## Decision
Keep the narrow interface, placed at the bottom of the layer stack: `ICommonConfigModelRefReader` (named `ICommonConfigReader` until #453) lives in Harbor.Ui.Framework.Abstractions/Configuration (same assembly family as its consumer, zero new edges) and exposes only what SessionFactory needs (`ReadModelRefAsync`, named `TryReadProviderModelAsync` until #453). Harbor.Desktop.Abstractions.JsonCommonConfigStore implements the full `ICommonConfigStore`; a per-platform adapter (`CommonConfigReaderAdapter`) projects it onto the narrow reader, and each platform registers both in DI. Merging Desktop.Abstractions into Ui.Framework was rejected (wrong direction - desktop concepts would leak into the shared framework); splitting Terminal.Abstractions was rejected (large churn, no additional isolation).

Amendment (#453): the two contracts are read/write versus read-only over one file, which is a split of capability and not a duplicated contract — so the fix was to make the difference legible in the names rather than to merge them. The narrow reader also now returns `Maybe<ModelRef>` instead of `(string? ProviderId, string? ModelId)?`, which had spelled four states while the domain has one; `CommonConfigContractRules` is the guard, and it fails on a second producer or on a half-pair test reappearing anywhere on the seam.

## Consequences
- No circular reference; layering enforced by Harbor.Architecture.Tests.
- Ui.Framework.Sessions takes the narrow reader as a DECLARED optional constructor parameter (since #470) - hosts without config (tests, minimal) behave as before.
- Full config surface and all writes stay on the Desktop.Abstractions type; the narrow reader must not grow beyond session-bootstrap needs, and `CommonConfigContractRules` fails it if a write member or a second implementer appears.

# ADR-010: one token-cell notation — measurement normative, choice open

## Status
**Open — the owner decides.** The measurement in §2-§4 of the long-form document
is normative and gate-checked; the choice in §6 is not taken and nothing is
implemented. No product code changed in the commit that recorded this.

## Context
Issue #788 (follow-up from #682) counted a session's cumulative token spend
rendered five ways in two notations, and declined to unify them because the
choice is a rendering decision with golden-frame blast radius. Re-measured on
`dev` = `2c55def5`, the inventory is six writers and three conventions, not five
and two: a third convention writes the cell raw with no suffix on the plain/ANSI
status line, and `M` is uppercase everywhere, so only `K` vs `k` actually
diverges. The golden frames that would move are text, cover only the status
widget, and number two files plus one hash manifest — zero binaries.

## Decision
Deferred to the owner, with the price of each option stated:
**A** `1.2K↑ 5.7K↓` (align to the status bar) costs 2 text goldens + 1 hash
manifest + a rewrite of the hand-rolled span token path, and widens cells by two
columns at round numbers. **B** `1.2k↑ 5.7k↓` (align to the status widget and
sidebar) costs zero goldens and ~12 test string literals, and never widens a
cell. **C** raw, no suffix, exists in the tree and costs the most for no
identified reader. The measurement also records that `goldens.yml` already
regenerates baselines on a GitHub runner and commits them back to the PR branch,
so "cannot be verified locally" was not the blocker it was taken to be.

## Consequences
- The measurement is re-checked against the tree by `tools/check-doc-cites.py`,
  so the inventory cannot rot into a stale claim.
- `MoneyCellSingleHomeRules` stays as it is: it freezes the writer COUNT and does
  not require the undecided choice. Point 4 of #788 (a rule on scale + suffix in
  one expression) must NOT be written until this ADR is decided — it would be red
  on all writers at once, which is the standing-red build its own header warns
  against.
- A token-side ratchet, if one is ever added, must declare the two model-facing
  raw token reports as homes from the start or it is red on day one.
- Full text, citations and the per-option file lists:
  [ADR-010](ADR-010-token-notation-one-cell.md).

---

# ADR-011: a gate for documentation examples that only LOOK compilable — measured, not built

## Status
Accepted as a record. **No gate was added.**

## Date
2026-09-30 (issue #853; follows the #849 defect fixed by PR #855)

## Context
`docs/TEST_PATTERNS.md`, titled "Copy-Paste Ready", shipped two untrue rows in its
attribute table and was caught only when another PR copied the text: a CS1729 arity
error (`[NotInParallel("a","b")]` — TUnit has no `params` overload) and a CS0246
non-existent type (`[SkipWhenNotLinux]` is `internal` to one test project). The
question was whether the stdlib-only, dotnet-free docs gate can catch either class.

## Decision
Build nothing. Six candidates were implemented far enough to be counted, and every one
is unreachable from `docs.yml` or red on day one. The decisive measurements on
`dev` = `538d9f1c`:

| candidate | verdict | number |
|---|---|---|
| A — name from TUnit's shipped XML doc | unreachable **and** incomplete | `docs.yml` runs 6 `python3` lines, no restore, no lockfile; and 21 of 47 attribute types carry no `#ctor` row, incl. `NotInParallelAttribute` |
| A′ — name from this repository | zero work, 23/62 false positives | 0 of 62 spellings are "declared only `internal`"; the false positives include the real TUnit `RunOn` and `ExcludeOn` |
| B — examples that quote code must cite | red on the corpus | 131 of 1028 copy-paste blocks cite (12.7%); 0 provenance markers of any kind exist |
| C — ban unprovenanced examples | would outlaw the docs | 897 blocks in 105 documents; 103 elide bodies, 175 are ASCII/shell fences |
| D — fence every citation already written | red on day one | 2662 citations in non-normative docs, 818 fail across 26 documents |
| E — a path on a fence's first line must resolve | the convention is a template | 116 fences use it, 34 resolve, 82 name files that do not exist |

## Consequences
- No code changed and no rule was added. A check that cannot fail is worse than none:
  it is indistinguishable from a check that works.
- `docs/TEST_PATTERNS.md` still declares neither `Status: normative` nor a dated
  banner, so the densest copy-paste document in the repo is outside every fence.
  Marking it normative is a one-line edit and a judgement call about that document's
  claims; it is left to the owner.
- Nothing conflicts with the merged work: `#826`'s `DOC-CITED-TABLE-UNDECLARED` is
  untouched, and `#811`'s ADR-010 is normative, cites code lines that the prose rule
  already fences, and stays green.
- Re-open conditions are recorded in the ADR so the decision is revisited on evidence
  rather than re-argued.

## References
Full text, the per-candidate reasoning and the re-open conditions:
[ADR-011](ADR-011-doc-example-compile-gate.md).
