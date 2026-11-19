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
Model Context Protocol servers are consumed out-of-process over stdio JSON-RPC: `Harbor.Tools.Builtin/Tools/Mcp/` provides `McpRegistry`, `McpProcessClient`, `McpToolAdapter`, argv parser, source-generated serialization context (AOT-safe), and `mcp.json` config loading. The generic `mcp` tool remains for arbitrary JSON-RPC methods. Since #2227 each server's `tools/list` answer is also registered as real `ITool` adapters before the tool registry is frozen, with an exact permission grant on each agent; server instructions are aggregated into the system prompt via `IMcpRegistry.GetInstructions`. Discovery follows `nextCursor` until the server exhausts its catalog (#2228), so a paginated answer registers every page rather than silently dropping all tools after the first. Sample servers under `samples/mcp/`.

## Consequences
- Crashing/slow MCP servers cannot take down the agent loop. A failed call returns `Result.Failure`; the next call respawns the process and the adapter stays registered.
- AOT constraints satisfied without reflection-based serialization.
- Server tools are ordinary `ITool` instances in `ToolRegistry`, so the prompt and dispatcher treat them like builtins. Prompt size grows with the tools the configured servers advertise.
- Pagination bounds discovery the same way it bounds the server: a fixed page cap plus a repeated-cursor check, so a hostile or broken server cannot stall composition in an endless cursor chain.

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

Amendment (#453): the two contracts are read/write versus read-only over one file, which is a split of capability and not a duplicated contract — so the fix was to make the difference legible in the names rather than to merge them. The narrow reader also now returns `Maybe<ModelRef>` instead of `(string? ProviderId, string? ModelId)?`, which had spelled four states while the domain has one; `CommonConfigContractRules` is the guard, and it fails on a second implementer of the narrow port or on a half-pair test reappearing in a file that names that port.

Amendment (#894): the sentence above claimed "a second producer" and "anywhere on the seam", and both were false — measured, not assumed. "Exactly one production implementer" (rule 5) and "one producer of a qualified reference" are different assertions: `ToolsCatalog.ResolveDefaultModelFromCommon` re-qualifies the pair by hand without implementing the port, and rule 5 was never going to see it. It is inventoried instead, with a stated reason, by `ModelRefSingleParserTests` rule 3. And the seam is the port's name, not the pair's reach — widening it to "names either contract" takes the perimeter from 6 files to 19 and catches nothing. The write side is a separate invariant, graded by `ConfigHalfPairWriteRules` against `CommonConfig.HasDefaultPair`.

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

# ADR-012: вендоренная пара `CollapseWhitespace` — правка в чужом репозитории, решение за владельцем

## Status
**Open — the owner decides.** The measurement in §2-§4 of the long-form document
is normative; the choice in §6 is not taken and nothing is implemented. No product
code changed in the commit that recorded this.

## Context
Issue #726 recorded three `CollapseWhitespace` behaviours under one name, the
third of them vendored. #878 checked that third claim: there are **two** vendored
copies, not one behaviour, and they diverge from **each other** — ASCII-six
whitespace vs `char.IsWhiteSpace`, and leading/trailing edges trimmed on one side
and kept on the other. Re-typed and compared on an exhaustive alphabet, the two
disagree on 758 of 781 strings (97.1 %), and the boundary is described exactly by
"has an edge whitespace or a non-ASCII-six whitespace inside".

The frame-changing fact is ownership: `external/ConsoleEx` is a **git submodule**
(160000) pointing at `github.com/nickprotop/ConsoleEx` — a third-party
repository, with no Harbor remote to send a fix to. It is also, contrary to the
claim in #726, **compiled**: `Harbor.Tui.NickConsoleEx` sits in `Harbor.slnx` with
an unconditional `ProjectReference` and CI checks out `submodules: recursive`. And
it is **unreachable**: no `.cs` under `src/` or `apps/` names `HtmlControl`,
`HtmlBuilder` or any other type in the `Html/` surface.

## Decision
Deferred to the owner, with the price of each option stated. **A** change nothing.
**B** fork the submodule and patch deliberately, accepting merge debt on every
upstream release. **C** replace the surface. Reachability reorders the price: no
Harbor user can see either output today, so A is the default and B/C buy
consistency in code that no user path executes.

## Consequences
- The long-form document is a **dated record, not `Status: normative`**, and says
  why: the `cites` job checks out without submodules, so a `file:line` fence on
  `external/ConsoleEx/` would read as `DOC-CITE-MISSING` on a tree where the file
  exists. A normative banner would claim a machine-checkability this file lacks.
- **No guard is proposed for the two copies.** Both are `private static` inside a
  submodule at a moving pin, so any test on them is reflection over a third party's
  private API that can only ever go red on a change we cannot make. The live
  perimeters are already guarded by #717 (`WhitespaceCollapseDivergenceTests`) on
  our own files. What *is* guarded instead is the premise this ADR rests on —
  that the HTML surface is unreachable — by `VendoredHtmlSurfaceRules`.
- This is **not** the #915/#923/#924 shape: `external/ConsoleEx` is not `contrib/`
  and does build. The two must not be conflated in either direction.
- Full text, measurements and the per-option costs:
  [ADR-012](ADR-012-vendored-html-collapse-whitespace-pair.md).

---

## ADR-013: the CellForge widget axis is an internal contract — both plugin seams, and the one that is not

Issue #564 asked whether the CellForge widget set is closed at compile time and
whether a plugin can add one. Measured on `dev` = `7aff0c87`, the widget axis is an
INTERNAL CONTRACT and the freeze is correct (#555 — lifted on 2026-10-20; the widget
axis is closed by that owner decision, not by the freeze), the view seam
(`ITuiPlugin`/`ITuiView`) is CLOSED and has been since #780/#966, and the panel
seam (`ITuiPanelPlugin`/`IPanelProvider`) is LIVE — with a real door and four
product readers — but was documented as returning a renderer-native widget that
no shipped renderer can accept.

So the residual finding was neither an open axis nor a closed one. It was three
plugin-facing documents teaching an API that does not compile or does not render,
which is the same defect #916 measured in prose and one document further out.

Decided: the widget axis stays closed (an owner decision, not this issue's), the
panel axis stays text-only, and the documents now say so — fenced by two new rules
in `tools/check-doc-cites.py` that are red on the pre-fix tree.

- Full text, the three axes with their implementation and reader counts, the
  measured cost, and the four known limits:
  [ADR-013](ADR-013-cellforge-widget-axis-internal-contract.md).

---

## ADR-014: `Harbor.Ui.Framework.Sessions` stays — the interface already exists, and the matrix forbids the move

Issue #439 (slice `33/T6`) asked for `Sessions/` → `Harbor.Application`, for
`IChatViewState`/`IToolCallViewModel`/`IDiffRenderer` to be extracted into
`Harbor.Ui.Framework.Abstractions`, and for #33's three discussion questions to be
answered. Measured on `dev` = `4a55260b`, two of the three ask for something the tree
does not permit and the third is already done.

The interface exists: `IPanelSessionGateway` is the extracted lower-layer session port,
added by #470 with a doc-comment stating the rule the issue re-derives, and
`CellForgeTuiRenderer` binds it that way. The three named interfaces have **0**
occurrences each in `src/`, `apps/` and `tests/` — writing them is an introduction,
which is a new axis (the #555 freeze that forbade it was lifted on 2026-10-20; an axis
is additive, but it has to be declared in `PluginAxisRegistry` and wired), not an
extraction.

The move is blocked twice by the same authority: `docs/ARCHITECTURE_LAYERS.md` §2 says
the Application layer "must not know about UI vocabulary", and
`FullLayerMatrixTests` fails the new edges from the IL *and* refuses to pre-declare
them. It is not cosmetic either — **11 of 14 files** use a `Layer.Presentation` type.
There is no cycle to justify it: `Ui.Framework.{State,Services,ViewModels}` never
reference `Harbor.Application`. `Sessions/` is not dead — 0 of 14 types, 204 external
code references — and the three `Sessions/` folders declare disjoint type names, so
there is nothing to merge and nothing to delete.

Decided: the move is declined on the measurement, no interface is introduced, and the
one real defect found in the blast radius is fixed — `CountDelegations` swallowed
`IOException`, so `FileNotFoundException` made a path-pinned `== 0` assertion report
"clean" for a file that was not there, the #591 shape.

- Full text, the per-type consumer counts, the per-file binding table, the
  eight path-pinned guards split by how each would react to a move, and why no new
  layer guard is proposed: [ADR-014](ADR-014-sessions-stays-in-ui-framework.md).

---

## ADR-015: i18n shared string catalogue lives in `Harbor.Ui.Framework.Abstractions`

Issue #434, slice 1 (inventory + catalogue home only). The shared catalogue is
`UiStrings.resx` (`en` = source of truth) with per-locale satellites plus a
missing-key → `en` accessor, placed in `Harbor.Ui.Framework.Abstractions`
because that project is already `Layer.Domain` in `FullLayerMatrixTests` (cf.
ADR-009) — reachable from TUI renderers, both apps and `contrib`-Blazor with
zero new matrix edges, while `Harbor.Abstractions` stays frozen. Avalonia
projects the same keys into per-locale `ResourceDictionary` XAML (rule 9),
Blazor adapts via `IStringLocalizer`, TUI/CLI look up directly with a
width/unicode fallback. Inventory (~1 370 occurrences pre-dedupe), the
`InvariantCulture` user-facing hit list, and the high-traffic group table:

- [inventory](../I18N_STRING_INVENTORY.md), full text:
  [ADR-015](ADR-015-i18n-string-catalogue-home.md).

Locales, fallback tests, extraction, and the culture-format fixes are later
slices.

---

## ADR-016: Roslyn plugin-host fate — AOT main plus JIT split host (option 2)

Issue #418 (slice S4 of epic #48) asked for the decision the S1–S3 evidence slices
support. Measured at `dev` = `0da7f2ba`: S1 JIT half landed (PR #1043, about
459 ms / 94 MB on the DI-heavy verb, AOT column blocked), S2 gate landed
(PR #1014) with the committed record that the AOT publish fails (IL3000, IL2070,
IL2072 as errors, one IL3000 site inside Roslyn itself), S3 audit closed, S5
skeleton plus parity table landed (PRs #1008, #1041).

Decided 2026-10-09: option 2 of the three in #48 — AOT main plus JIT plugin host
over IPC/MCP with `src/Harbor.Plugins.Host` as the split point. The AOT binary
must never load Roslyn in-process; the escape hatch is the opt-in MCP stdio host
(JIT, tools only — provider, agent, panel, store and backend unsupported with
reason, TUI view a closed seam in every option, boundary not a sandbox).
Per-option unsupported lists for config reflection, serializers, globalization,
error paths and plugin availability are in the full text. Delta against #21 (UI
half over UDS) and spec 17 sections 7 vs 8 are spelled out there too. Owner
sign-off lands as the closing comment on #418 before any gated perf work merges.

- Full text, evidence table, per-option verdicts, unblock/still-blocked lists:
  [ADR-016](ADR-016-roslyn-plugin-host-split.md).

## ADR-017: docs site stays DocFX — metadata lane wired, Pages behind an opt-in

Issue #431 asked for the DocFX-vs-MdDocs decision with a default to DocFX; slice D1
(PR #1049) had already landed the DocFX-shaped foundation (`docfx.json`,
`docs/toc.yml`, assembly lane), so this slice records the decision rather than
re-litigating it. Measured on `dev` = `21bdef4f`: markdown-only cannot produce the
required API surface for the two frozen contract assemblies, and
`GenerateDocumentationFile` is already global in `Directory.Build.props` (shared
once with the API-freeze slice), so D2 wires the consumer — the metadata lane for
`Harbor.Abstractions` + `Harbor.Abstractions.Contracts` (`dest` set to `api`) —
plus a `deploy` job that publishes to Pages only on `master` behind the
`HARBOR_PAGES_ENABLED` opt-in (Pages is not enabled yet, so an unconditional lane
would be red on a head nobody broke). Deliberately deferred: full `docfx build`
(`docs.yml` stays dotnet-free), the XML-doc coverage gate (no measurement yet),
and the README link (no live URL until the first green deploy).

- Full text, the three measured reasons, the wired-vs-deferred split:
  [ADR-017](ADR-017-docs-site-docfx.md).

## ADR-018: the `Harbor.Application` split, variant A (the maximal five-assembly plan)

Issue #1608 asked the owner to choose between three shapes for decomposing
`Harbor.Application` (A: five assemblies; B: three; C: one plus an optional
second), on research that had measured 14 areas / 19.5k lines with Sessions at
44% and Agents at 27%, and no real dependency cycles at all. The research
recommended the incremental `C → B` path; the owner chose **variant A** on
2026-10-16, and the issue stayed open with the decision living only in its
comment thread. This ADR is the durable record.

Variant A, as decided, is five assemblies: `Harbor.AgentRuntime` (Agents +
Pipeline + Resilience, ~5.9k), `Harbor.SessionContext` (compaction / prompt /
tracker, ~3.1k), `Harbor.PolicyConfig` (Permissions + Configuration +
Onboarding, ~3.4k), `Harbor.Intelligence` (Skills + Hooks + Diagnostics + Git +
Fs, ~3.5k), and `Harbor.ChangeTracking` (~5.6k). The four `[decomp-max]` slices
[#1623](https://github.com/refusedguy/Harbor-Harness/issues/1623)–[#1626](https://github.com/refusedguy/Harbor-Harness/issues/1626)
cover the first four; `ChangeTracking` landed separately under #1616. The owner's
reasons: faster parallel compilation, physical boundaries rather than boundaries
held in the head, and the assemblies being pullable into other projects later as
packages.

State of the slices when this was written: **#1623 merged** (PR #1666),
**#1624 merged** (PR #1714), **#1616 merged** (PR #1660), **#1625 / #1626 open**.
Two caveats are recorded rather than smoothed over. The cohort boundary has moved
(Permissions and Onboarding already left under #1658 and #1701, so #1625's remaining
scope is `Configuration/*` alone), and the "additive and registered" axis rule that
came out of the #555 freeze lift is still policy only until #1719 lands.

**Update 2026-10-21:** #1625 has landed — the configuration family
(`Configuration/*`, 11 files) is `src/Harbor.PolicyConfig`, an Application-layer
sibling with its own matrix row, `Harbor.slnx` row and README. #1626
(`Intelligence`) is the last slice still open.

- Full text, the rejected variants and what the issues do *not* record about
  them, the sequencing DAG, the standing rules and the assembled
  definition-of-done:
  [ADR-018](ADR-018-harbor-application-split-variant-a.md).

## ADR-019: the vendored SharpConsoleUI renderer leaves the product

Issue [#1921](https://github.com/refusedguy/Harbor-Harness/issues/1921) read the
compile timeline of a Release build and found that three of the 110 projects
were compiled in **Debug inside the Release build**, and that no Release output
of them exists. One of the three was `external/ConsoleEx/SharpConsoleUI`, the
third-party terminal UI kit Harbor vendored as a git submodule to back the
`Harbor.Tui.NickConsoleEx` renderer. That tree cost a 28.3 s window in every
Release compile, and its Debug output landed in the shipped CLI.

Decided 2026-10-20 by the owner while working on that issue: *"убрать полностью
в контриб, кто захочет - пусть и дрочится"* (move it out to `contrib/` entirely;
whoever wants it can wire it up themselves). The decision is not recorded in a
comment on the issue, so [ADR-019](ADR-019-vendored-sharpconsoleui-renderer-leaves-the-product.md)
is the durable record.

Removed from the product: the `HARBOR_WITH_NICK_CONSOLE_EX` build constant and
its code paths, the `nickconsoleex` value of the build-time `HarborWithRenderer`
selection, the `tui` value `nickconsoleex`, and the public enum member
`BackendKind.NickConsoleEx` (a breaking public API change). `HARBOR_TUI=nickconsoleex`
stops working and resolves to the fallback backend with a warning. The
`Harbor.Tui.NickConsoleEx` NuGet package is not published any more.

Moved to `contrib/`: `src/Harbor.Tui.NickConsoleEx/` to
`contrib/tui/Harbor.Tui.NickConsoleEx/`, the `external/ConsoleEx` submodule to
`contrib/external/ConsoleEx/`, and the three boundary files
(`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`)
to `contrib/external/`. The `contrib/` terms apply unchanged: unmaintained, not
compiled by CI, not in `Harbor.slnx`, no `ProjectReference` from `src/` or
`apps/`, not published.

A user who wants SharpConsoleUI-backed rendering now has to clone the vendored
tree, build the wrapper and wire it into a Harbor build by hand. The product
does not ship it, and no configuration value brings it back.

- Full text, the measurement, the removed surface and the alternatives:
  [ADR-019](ADR-019-vendored-sharpconsoleui-renderer-leaves-the-product.md).

## ADR-020: the per-turn change oracle is computed by the harness, outside the agent

Issue [#1745](https://github.com/refusedguy/Harbor-Harness/issues/1745) asked for a
per-turn «what changed» in the interactive scenario, where nothing existed: `harbor
run` had a frozen change set, the REPL and `harbor ask` had no change story at
all. The point of the issue was the distinction between two claims — «the agent
says it changed X» and «the world says X changed» — and only the second one is
evidence.

The decision: a workspace path whose bytes differ across the turn, as the HARNESS
fingerprinted them (a content hash taken before and after every mutating tool
call). The agent's prose is not an input to any verdict, a tool that writes
without declaring a path (`bash`, `task`, an MCP server, a plugin tool) is named
as *unobserved* rather than assumed away, and a path the harness cannot read is a
verdict (`Unverified`) rather than a silence. The oracle is a projection over the
event bus in the Application layer, outside the agent loop; the hosts (the REPL,
`harbor ask`) draw one line per turn from what it decided.

Not taken, and why: git snapshots of the working tree (the issue's own proposal —
a different product, and it needs a temporary index to leave the user's
repository untouched), a new `AgentEvent` subtype (the event union is a frozen
public contract, and two hosts read the report), and persisting the reports (the
part that survives a restart is the part that does not need to, and the rest
would cost the frozen `ISessionStore` contract).

- Full text, the four decisions, the rejected alternatives and where the code is:
  [ADR-020](ADR-020-turn-change-oracle.md).

## ADR-021: `Harbor.Desktop.Abstractions` is split, and the hub gets an honest name

Issue [#1899](https://github.com/refusedguy/Harbor-Harness/issues/1899) read a packable
assembly whose name promised a contract layer and found a hub wearing that name: 9
project references and an 18-assembly dependency closure under the description
"platform VMs derive from the abstract VMs here".

Re-measured on `dev` = `283a9dbe` (the issue's own numbers had moved: 30 `.cs` files
and 46 public types, not 39 and 55), the assembly divides by DEPENDENCE into two
halves: 12 contract/model/enum types that bind two Harbor types, both in the
innermost assembly, and 34 concrete view-models that bind the preset catalogue
(the `#188` `ProviderPresets` edge — `Harbor.Application` at the time of the
measurement, `Harbor.PolicyConfig` since #1625 moved the configuration family
out) and five `Harbor.Ui.Framework.*` modules. Two of the nine references bound
no type at all (`Harbor.Terminal.Abstractions`, `Harbor.Ui.Framework`), and both
were already recorded as vestigial edges with "remove the reference" written
next to them.

Decided: **split at that seam** rather than rename the whole assembly. The leaf keeps
the name `Harbor.Desktop.Abstractions` and drops to one Harbor reference
(`Harbor.Abstractions.Contracts`, where `SessionStatus` and `AgentName.Fallback`
actually live); the view-models move to `Harbor.Desktop.ViewModels`, and the `#188`
exception moves with them. A consumer that wants only `ICommonConfigStore` or
`ThemeKind` now pulls **2** assemblies instead of **18**, and no longer drags
the preset catalogue's assembly in.

Two cost claims in the issue were corrected by measurement: there is **no**
public-API baseline for this assembly to re-sign (the public-API freeze covers
only `Harbor.Abstractions` and `Harbor.Abstractions.Contracts`), and there are
**no external consumers** (nuget.org answers 404 for every Harbor package; the
release workflow publishes only the CLI).

The bonus the issue did not predict: `DesktopAbstractionsLeafTakesNoIoRules` already
called this assembly *"the config leaf"* and argued about its empty allowed set. The
split makes the guard's premise a description of the tree instead of an aspiration.

Three questions stay with the owner: the package identity and versioning of the two
packable assemblies, the deletion of two unreferenced public types the measurement
found (`ThemeKind`, and a `ToastKind` that duplicates the one in
`Harbor.Ui.Framework.Services`), and whether the leaf should be renamed
`Harbor.Desktop.Contracts`. The split does not touch `Harbor.Abstractions.Contracts`,
and does not move the three abstract bases down to `Harbor.Ui.Framework.ViewModels`
(#1875, which is what would take the preset catalogue out of the TestKit
closure).

- Full text, the per-option costs, the before/after table and the open questions:
  [ADR-021](ADR-021-desktop-abstractions-split.md).

## ADR-022: append-only журнал сессии: log, surface, rewind-событие и явный prune

Issue [#1784](https://github.com/refusedguy/Harbor-Harness/issues/1784) asked for the
plan behind #1752: a session log that is append-only, with a surface (what the model
and the UI see) folded out of it, shadowed records that stay in the log, a rewind that
writes an event instead of rewriting the file, and a prune that only happens when a
person asks for it.

The plan was written on 2026-10-20 and rewritten on 2026-10-21 against the tree as it
stands, because eight things changed in between. The contract, the shared fold and the
guard are already in the tree (PR-1 of the plan, #1806). The message edit no longer
moves a record, it rewrites it in place (#1798, #1907), so the stage that made the
edit append-only is gone and the invariant survives on the fingerprint, which does not
digest `Usage`. The export is one snapshot (#1785, #1811), the checkpoint axis is out
of the store (#1513), so the old "after #1513" block is gone. Compaction moved to
`Harbor.SessionContext`, the feedback records (#1746) are the precedent for a
record that is not a message, the torn tail is already reported (#1744), and the
query service (#1743) is already there, so no new search service is needed. The
session file is versioned too (#1749, #2066), so the rewind record is now a question
about the file's generation: by the catalog's own rule it stays in the current one,
and the price of that (an older build reads the file and shows the surface from
before the rewind) is named as an open question for the owner.

Decided and recorded: the rewind is a record of its own (`"type":"rewind"`), the edit
stays an in-place rewrite, prune is explicit only, `ISessionStore` grows default
members instead of a new extension axis, and the log is the message records plus the
rewind events. Six stages, each with a verifiable acceptance criterion and a
non-vacuity control that is red on the parent commit and aimed at the destructive path
that really exists in the repository.

- Full text, the six stages, the guard's scope and the open questions for the owner:
  [ADR-022](ADR-022-session-log-append-only.md).

## ADR-023: оркестратор сборки переезжает с NUKE на Fallout

Оркестратор Harbor (проект `build/_build.csproj`, 32 файла `.cs`, 3 718 строк, 13
таргетов, пакет `Nuke.Common 10.1.0`) переезжает на
[Fallout](https://github.com/Fallout-build/Fallout) — жёсткий форк NUKE с
сохранённой историей, MIT-лицензией и сохранённой поддержкой `.slnx`.

Основание — не слова, а даты и цифры, снятые 2026-10-22: последний коммит NUKE —
**2025-12-02** (324 дня назад), **0** коммитов за последние 6 месяцев, **0**
ответов мейнтейнера после той же даты при 65 открытых issue и 55 открытых PR, сайт
`nuke.build` не открывается, отчёт о CVE с 1 critical (issue #1591) без ответа с
2026-04-16, запрос поддержки `.slnx` (issue #1586) без ответа с 2026-01-09. Флаг
`archived` на GitHub при этом **не выставлен** (`archived: false`) — заброшенность
фактическая, а не формально помеченная.

Fallout: точка форка — NUKE `develop` = `27e8077b8` (2025-12-02), проверено
`git merge-base --is-ancestor`; **459** своих коммитов от 17 авторов, 21 за
последние 30 дней; `lib/net10.0/` — единственный TFM у `Fallout.Common`;
`Fallout.Persistence.Solution` несёт `.slnx`-читалку; путь миграции документирован
(`fallout-migrate`, `Fallout.Migrate.Analyzers`, переходный shim). Разрыв по
скачиваниям — 103 378 против 39 894 159 у NUKE, то есть проект молодой, и это
зафиксировано как риск, а не как аргумент «за».

Решено: пиним **`Fallout.Common 10.4.0`** (последняя листинговая на nuget.org;
`11.x` по ADR-0002 проекта fallout'а публикуется на nuget.org только по явному
флагу). Переезд идёт шестью шагами, и **первый шаг ничего не ломает**: пилот
(`Pack`, сравнение артефакта) идёт параллельно NUKE-пути, переключение — отдельный
шаг с одной точкой входа, удаление NUKE — последним. Пятый шаг — гейт
`tools/gates/check-no-nuke/`, который не даёт вернуться к NUKE, по образцу
`tools/check-no-test-sdk`.

Что остаётся в YAML: всё, что решает GitHub, а не клиент, — `on:`, `runs-on:`,
`permissions:`, `secrets`, публикация артефактов, `needs:` как топология, required
checks в ruleset. Из 9 691 строки в 17 workflow-файлах решётка — **617 строк (6,4 %)**,
из которых руками остаются **72** (19 `needs:` + 53 джобных `permissions:`); шелл-код
в `run:`-блоках — **1 700 строк (17,5 %)** — это то, что имеет смысл унести в C#, но
в объём переезда оно не входит. Генерация workflow-файлов средствами Fallout
существует (`[GitHubActions]` пишет `on:`, `permissions:`, `runs-on:`, шаги), но
нашу модель джобов не выражает: не пишет `needs:`, `strategy:`/`matrix:`, джобные
`if:`, `permissions:`, `outputs:`, `env:`, и id и имя джоба у него выведены из одной
строки — а у 7 из 8 обязательных проверок имя отличается от job id, и пробел в job id
GitHub не допускает. Решено: **генератор в объём переезда не входит**; если его
делать — то **в `tools/gates/`, на Go, без внешних зависимостей** (правило
`docs/CI_GATES.md` правило 6 запрещает гейтам внешние модули, а `vendor/` был бы
нарушением этого правила), по образцу `gen-seam-graph`. C#-генератор отпадает: он
потребовал бы restore и сборки оркестратора, а гейт обязан срабатывать там, где SDK
и сборки нет.

Отдельно зафиксированы **правила зависимостей по каталогам**: `src/`, `apps/`,
`tests/`, `tools/` — под корневым `Directory.Build.props`, CPM включён, версия
обязана лежать в `Directory.Packages.props`; **`build/` — вне**: свой
`build/Directory.Build.props` не импортирует родителя, CPM выключен в двух местах,
анализаторы off, версии ставятся прямо в `build/_build.csproj` (запись в
`Directory.Packages.props` для сборщика была бы мёртвым текстом — ровно то, что
убрал #1896 для `Nuke.Common`); **`tools/gates/` — Go, стандартная библиотека
только, `go.sum` нет**, потому что гейт должен срабатывать на раннере без сети.

- Эпик переезда: [#2178](https://github.com/refusedguy/Harbor-Harness/issues/2178);
  подзадачи по шагам: #2179 (пилот), #2180 (`build/_build/`), #2181 (CI и
  bootstrap), #2182 (документация), #2183 (удаление NUKE), #2184 (гейт).
- Полный текст, план по шагам, смета в числах, риск-реестр и граница YAML:
  [ADR-023](ADR-023-build-orchestrator-moves-to-fallout.md) и
  [BUILDSYSTEM_STUDY.md](../BUILDSYSTEM_STUDY.md).

## ADR-024: grapheme cluster в 16-байтовой Cell. Отдельное решение

Issue [#2223](https://github.com/refusedguy/Harbor-Harness/issues/2223) поднял
трещину между моделью данных и терминалом: `Cell.Rune` это `int` на один scalar
(`Cell.cs:22`), тогда как терминал рисует grapheme cluster; Ratatui держит
`Option<CompactString>` в клетке (`ratatui .../cell.rs:41-47`), но наш 16-байтовый
value-контракт и AOT-профиль такое расширение не принимают. `UnicodeWidth` уже
пометил VS16/ZWJ как zero-width no-ops (`UnicodeWidth.cs:25-34`), то есть проблема
известна и сознательно отложена.

Решение фиксируется **до изменения модели данных**. Три варианта с ценой:

1. **Grapheme side-table рядом с буфером (рекомендуемый).** `Cell` остаётся
   16-байтовой, zero-alloc `FrameDiff` и `CellSimd` не ломаются; платим вторым
   массивом (~8–16 байт на клетку), правкой row-hash и emission-пути
   (`DiffEngine`/`AnsiWriter`). Прецедентом служит `CellDiffOption` в `ScreenBuffer`.
2. **32-байтовая клетка (отклонён).** Ломает «16 байт = 2 ulong» в `CellSimd`,
   five-compare, codec и goldens; AOT ломается только при попытке положить
   ссылочную строку, то есть цена та же, что у варианта 1, без выигрыша.
3. **Оставить `int Rune` и документировать ограничение (текущее состояние).**
   Ноль затрат, ноль исправлений; ограничение перестаёт быть молчаливым.

Ни один продуктивный файл этим PR не тронут: ни код `Cell`, ни тесты, ни гейты.
Клетка не расширяется без явного решения. Этим ADR решение и зафиксировано.

- Полный текст, таблица доказательств, цена по осям и список не проверенного:
  [ADR-024](ADR-024-grapheme-cluster-16-byte-cell.md).

## ADR-025: `ci.yml` не делится на `workflow_call`

Issue [#2194](https://github.com/refusedguy/Harbor-Harness/issues/2194) вернул
в повестку четвёртый вариант из исследования workflow-файлов: разбить `ci.yml`
на reusable workflow'и. Замерили на `origin/dev` = `03a239ba`: 17 workflow-файлов,
0 триггеров `workflow_call`, каждый файл вызывается один раз. Механизм существует
ради переиспользования, а второго вызывающего нет. Единственное намеренное
дублирование, фильтр `changes` в `test-os.yml`, это 16 записей, все они есть в
списке `ci.yml`, и комментарий в файле уже объясняет выбор дубля.

Блокер в другом месте. Восемь required checks это `name:` под джобами, и все
восемь имен записаны в `.github/rulesets/dev.json` и
`.github/rulesets/master.json`. Джоб внутри reusable workflow сообщает проверку
как `<вызывающий> / <вызываемый>`, а сам reusable workflow нельзя выбрать
обязательной проверкой. Разбиение переименовало бы все восемь проверок разом и
потребовало бы менять оба ruleset'а в том же PR, что и CI. В `master` включена
строгая политика heads.

Решение: не разбивать `ci.yml`. Пересмотреть можно, когда появится второй
вызывающий с совместимым графом, гейт из #2191 будет сверять правила с именами
джобов, а переименование восьми проверок будет спланировано как одно изменение
с правкой ruleset'ов и защиты ветки. Ни один workflow-файл, ruleset или гейт этим
PR не тронут.

- Полное измерение, границы GitHub и условия пересмотра:
  [ADR-025](ADR-025-ci-yml-stays-one-workflow.md).

## ADR-026: батчинг вызовов git, а не замена git

Issue [#2133](https://github.com/refusedguy/Harbor-Harness/issues/2133) фиксирует
вердикты исследования «обязаны ли гейты использовать git», чтобы их не проверяли
заново. Историю читают три гейта: `check-doc-cites` (37 процессов, 23.4 с внутри
git), `check-local-merge-loss` (1546 процессов, 31.0 с) и
`check-public-api-resign` (35 процессов, 0.5 с). Остальные читают индекс
через `git ls-files` за 4–25 мс. Из ~55 с внутри git набора 53 с (96 %) лежат
в двух гейтах.

Решено **не** подключать go-git (blame в 5.4–31.3 раза медленнее и врёт на
8 из 285 строк переименования, бинарь +4.35 МБ, worktree не открывается),
**не** делать shallow (blame 15× быстрее, но 1 уникальный коммит вместо 5),
**не** писать свой blame, **не** брать gitoxide/libgit2 при `CGO_ENABLED=0`,
**не** начинать с параллелизма процессов (2.8–4.4× с потолком по ядрам против
25–30× у батчинга), **не** ждать выигрыша от Go на чтении объектов (уже парите:
25.4–27.0 против 24–29 МБ/с) и **не** расширять список гейтов «просто потому,
что git».

Вместо этого: `check-local-merge-loss` оставить на git и батчить вызовы
(21–28 с, 1–2 дня); `check-doc-cites` портировать в Go ради 45–55 с собственной
работы Python, git для истории не трогать (29 фолбэков blame и точечный blame
остаются отдельной работой); `check-public-api-resign` оставить на git (0.5 с),
Go нужен только ради единого бинарника; остальные гейты не трогать.

- Полный текст, таблицы измерений, вердикты и список не измеренного:
  [ADR-026](ADR-026-gates-and-git-verdicts.md).

## ADR-027: Canvas Shape/Context из ratatui остаётся DEFERRED

Issue [#2689](https://github.com/refusedguy/Harbor-Harness/issues/2689) — полный
отчёт `canvas-shape-decision.md` — вернул вердикт по
[#1168](https://github.com/refusedguy/Harbor-Harness/issues/1168): **оставить
DEFERRED**. Условие триггера не выполняется ни при одной формулировке: `#23`
никогда не содержал chart slice, а появившийся после триажа TUI-примитив
`CellChart` (`#2382`, коммит `071f64e9`) явно исключил 2D-геометрию. Машинная
часть дизайна (cell-diff, инвалидация, replay, кэш глифов, `SparklineBlock`,
`CellChart`, terminal graphics) в Harbor уже есть и ортогональна геометрии.
Остаётся один кластер — subcell-растеризация и векторные примитивы с
world-координатами — но у него нет ни одного потребителя, а владелец дважды
(`#1155`, `#2382`) прямо исключил его из объёма. Объём L (5+d), приоритет p3/low.

Решение: трекаем, не реализуем. `#1168` остаётся OPEN, код, тесты, гейты и
пороги не меняются; новый гейт был бы вакуумным. Условный минимальный срез из
отчёта предлагает `SubcellGrid`, `CanvasPainter`, затем `Line` и `Points` с
replay через `Scene`, но прямо помечен «НЕ рекомендация к реализации сейчас».
Пересмотр возможен тогда и только тогда, когда владелец снимет DEFERRED и
появится реальный потребитель.

- Полный текст, таблица покрытия, непокрытый кластер и условный срез:
  [ADR-027](ADR-027-canvas-shape-context-stays-deferred.md).

## ADR-028: Avalonia AXAML vs Harbor DSL — сравнение и вердикт

Issue [#2694](https://github.com/refusedguy/Harbor-Harness/issues/2694)
сравнил восемь приёмов декларативного XAML в Avalonia с DSL-платформой Harbor.
Все применимые пункты уже покрыты: компиляция разметки в C#, типобезопасные
биндинги без reflection, live-интерпретатор, Debug-only подмена `BuildRoot`,
реестр панелей по типу и ошибки с позицией в ответе на reload. Оконный previewer,
стили и SRE-компиляция не переносятся в терминал и остаются owner-decision.
Нового кода, гейтов и порогов этот вердикт не требует.

- Полное сравнение по пунктам и границы проверки:
  [ADR-028](ADR-028-avalonia-declarative-xaml-dsl-verdict.md).
## ADR-036: Blazor declarative UI и Hot Reload vs Harbor DSL. Сравнение и вердикт

Issue [#2695](https://github.com/refusedguy/Harbor-Harness/issues/2695) прислал
competitor research по декларативному UI и Hot Reload в Blazor с восемью идеями для
`.harbor-ui`. Проверка `gh pr list --search 2695` и дерева `origin/dev` (`09a673bc`)
показала: все применимые пункты уже влиты цепочкой #2424-#2441 и #2688. Это
двухфазный `MarkupGenerator` с кэшем по `(TargetPath, text)`, sequence numbers в
`BuildRoot`, правило `HasRemovedDirectParameters` в `BuildRootOverrideCoordinator`,
изолированный dev-reload с `ReloadResult.Rejected`, checksum и диагностики с
1-based позицией. Прямой `MetadataUpdateHandler` отклонён: он требует JIT и не
работает под AOT, а граница `HarborWithDslDev` жёстче AppContext-выключателя.

Decided: код не писать, гейты и пороги не менять, issue оставить OPEN как источник
сравнения. Пересмотр только по новому факту, например измеримой выгоде IL-дельты в
JIT-only dev-сборке.

- Полное сравнение по восьми пунктам и границы проверки:
  [ADR-036](ADR-036-blazor-declarative-hotreload-verdict.md).
## ADR-029: gpui-kit: переносимые паттерны уже в Harbor

Issue [#2701](https://github.com/refusedguy/Harbor-Harness/issues/2701) прислал
полный отчёт competitor research по gpui-kit (part 1/1). Проверка через
`gh pr list --search` показала, что все семь переносимых идей отчёта уже влиты
отдельными PR: приоритет стилей (#2385), типизированные part (#2393), delegate
для виртуализованных списков (#2387), ключевая идентичность (#2392), normalize
layout и reconciliation по id (#2388), один handle на viewport (#2391) и время
жизни подписок (#2390). `StylePriority.cs` прямо ссылается на gpui-kit коммит
`249a963`, который исследовал отчёт.

Решение: код не писать. Повторная реализация любой идеи создала бы второй
источник истины там, где отчёт требует одного владельца. Оставшаяся часть
отчёта описывает GPU-механику, hitboxes, DPI, анимации, AccessKit, IME и
pointer drag, которых в терминале нет. Отдельно отклонены `actions!` как второй
keymap, `Entity<State>` вместо `UiStore` и ещё один глобал темы. Issue #2701
остаётся источником сравнения, пересмотр возможен при новом факте.

- Полный текст, таблица соответствия идей влитым PR и список неприменимого:
  [ADR-029](ADR-029-gpui-kit-patterns-already-shipped.md).
## ADR-030: отчёт GPUI (#2707) — все применимые паттерны уже в дереве, нового slice нет

Issue #2707 — competitor research по движку Zed GPUI с шестью паттернами,
применимыми к CellForge. Проверка `gh pr list --search` и файлов на
`origin/dev` (`fc9aabde`) показала: все шесть уже влиты — #2396 (prepaint +
стабильные element id), #2384 (Scene display list), #2389 (адресная
инвалидация), #2395 (стек ключевых контекстов), #2386 (glyph cache
cell-паттернов), #2394 (measured leaves); паттерн 1 усилен PR #2442, #2523,
PR #2526 и #2722.

Decided: product-код не меняется, новый slice не открывается, research-отчёт
закрывается как уже реализованный. «Честно неприменимо» из отчёта остаётся
отвергнутым; отдельного общего per-frame line layout cache не добавляем —
per-frame reuse уже дают prepaint/ElementId и `TimelineLayoutCache`.

- Полный текст: [ADR-030](ADR-030-gpui-report-patterns-already-landed.md).
## ADR-031: отчёт steal-list 2026-11 (#2690) — нового slice нет

Issue #2690 — self-contained отчёт `steal-list-2026-11` с восемью идеями.
Проверка `gh pr list --search 2690` и `origin/dev` (`2392420b`): идеи 1-5 влиты
PR #2492-#2496 (follow-up #2539 и #2546), идея 6 — PR #2870; идея 7 зафиксирована
[ADR-024](ADR-024-grapheme-cluster-16-byte-cell.md) как side-table с отложенной
реализацией; идея 8 — широкий refactor трёх сборок и требует owner-решения.

Decided: код не писать. Отчёт остаётся открытым источником сравнения, гейты и
пороги не меняются. Пересмотр — по новому факту: снятие defer с ADR-024 или
выбор владельцем границ единого аниматора.

- Полный текст, таблица по восьми идеям и условия пересмотра:
  [ADR-031](ADR-031-steal-list-2026-11-no-new-slice.md).
## ADR-032: отчёт demo-gifs-docs-decision (#2692), нового slice нет

Issue #2692 — self-contained отчёт `demo-gifs-docs-decision` с вопросом, нужен
ли отдельный файл состояний demo GIF, и четырьмя минимальными slice. Проверка
`gh pr list --search 2692`, `gh pr list --search 2691` и `origin/dev`
(`f26ceb7e`): P1 влит PR #2511 (таблица покрытия в `DEMO_GIFS.md`), P2 влит
PR #2514 (`GIF_MIN_SCREENS_*` и `check-demo-screens.py` с self-test), P0 требует
owner-dispatch с пушем бинарников, а P3 описан неверно: `screen-*.png` живут во
временном каталоге, который `demo-repro normalize` удаляет. Смежные по #2691
PR #2787 и #2879 уже в дереве.

Decided: код не писать, отдельного `DEMO_GIF_STATES.md` не создавать, гейты,
пороги и GIF не трогать. Issue остаётся открытым источником сравнения.
Пересмотр по новому факту: владелец выполняет P0 или заказывает stills как
артефакт отдельным issue.

- Полный текст, таблица по четырём пунктам и условия пересмотра:
  [ADR-032](ADR-032-demo-gifs-docs-decision-no-new-slice.md).
## ADR-033: отчёт по кросс-языковому declarative UI (#2699) — гибрид уже в дереве

Issue #2699 — self-contained обзор declarative UI в семи фреймворках. Главный
вопрос, sourcegen или рантайм-интерпретатор, решён гибридом. Общий парсер
`.harbor-ui` строит `SpecArena` (#2489), Roslyn generator выпускает обычный C#
(PR #2443, #2477, #2531, #2534), а dev-интерпретатор использует тот же парсер
(#2727).
Стабильные id, file watcher и отдельный канал логов влиты #2722, #2442 и #2760;
headless-деревья — #2761 и #2524.

Decided: новый code slice не открывается. Subsecond/JIT-патчи, загрузка
разметки из плагинов с полным доверием в prod, DOM/GPU-абстракции и единый DSL
на все рендереры не вводятся. Issue остаётся OPEN как источник сравнения, гейты
и пороги не меняются. Пересмотр — по новому требованию preview с sample data,
снятию запрета на динамический код или owner-решению о едином DSL.

- Полный текст, таблица по семи фреймворкам и пять заимствований:
  [ADR-033](ADR-033-declarative-ui-crosslang-report-verdict.md).
## ADR-034: gpui declarative devtools (#2700), verdict без кода

Issue #2700 — self-contained отчёт по devtools для декларативного UI, прочитанный
по локальным клонам gpui-kit (`249a963`) и Zed (`6983685`). Из восьми идей,
которые отчёт называет заимствуемыми, семь уже влиты: IR описания (PR 2489),
ретенция дерева и patch (PR 2727, 2789), чистый генератор `BuildRoot`
(PR 2531, 2535), инструменты `dsl check` / `dsl types` (PR 2538), `debug_tree` и
`getTree` (PR 2528, 2761), prefix hashes и типизированное состояние элементов
(PR 2442). Восьмая идея, sentinel-поиск слотов, не имеет runtime-шаблона в
компилируемом C# DSL и отложена за владельцем. Dev-канал логов (PR 2760) и
automation (PR 2761, закрывает issue 2438) уже на дереве; watcher PR 2765
открыт другим агентом и не дублируется.

Decided: код, тесты, гейты и пороги не меняются. Issue остаётся OPEN как источник
сравнения. Пересмотр — по новому факту: runtime-слоты в компилируемом шаблоне или
новый devtool с конкретным терминальным аналогом.

- Полный текст, таблица по восьми идеям и список неприменимого:
  [ADR-034](ADR-034-gpui-declarative-devtools-no-new-slice.md).

## ADR-035: отчёт GPUIX (#2703), декларативный срез уже в дереве

Issue #2703 — competitor research по декларативному терминальному UI GPUIX.
Проверка `gh pr list --search 2703` и `origin/dev` (`f1dbd3a1`): все шесть
заимствований из итогового списка уже влиты. Mutation batch IR, трёхфазный
atomic apply, dirty/revision, event registry `(id, type)`, структурное сравнение
до enqueue и dev reload с разделением хоста и дерева это PR #2530, #2727 и #2789.
Automation-протокол с `getTree`, `testId`, clock и id-корреляцией это PR #2761.
probe-дисциплина уже живёт в `CapabilityProber`.

Decided: код не писать. Whole-surface kitty shm, inline RGBA, frame edits и
fences не принимаются: они заменяют cell-diff Harbor растровым кадром, сам
отчёт не рекомендует такую замену. `keystrokeId` не выделяется в отдельный
slice без измеримой цели и критериев приёмки. Issue остаётся OPEN как отчёт,
гейты и пороги не меняются.

- Полный текст, таблица покрытия шести пунктов и список неприменимого:
  [ADR-035](ADR-035-gpuix-declarative-terminal-no-new-slice.md).
## ADR-037: отчёт по Avalonia XAML (#2694) — финальный вердикт, нового slice нет

Issue #2694 — self-contained competitor research по AXAML, compiled bindings,
hot reload и реестру шаблонов данных. ADR-028 уже сравнил восемь приёмов с
DSL-платформой Harbor; единственной открытой позицией был dev-хук подмены
`BuildRoot`, описанный там как «in flight» по PR #2764. Проверка на
`origin/dev` (`872b6157`): #2764 закрыт как superseded, #2789 влит с
`BuildRootOverrideCoordinator` и Debug-only trampoline. Все восемь пунктов
имеют merged-след, новых фактов из upstream нет.

Decided: ADR-028 сохраняет силу, второй ADR-вердикт по тому же сравнению не
заводится, новый slice не открывается. Оконный previewer, стили, SRE и
runtime-движок `CompiledBinding` остаются owner-decision. issue #2694 не
закрывается, гейты и пороги не меняются.

- Полный текст, таблица по восьми пунктам и условия пересмотра:
  [ADR-037](ADR-037-avalonia-xaml-declarative-verdict.md).
## ADR-038: каталог компонентов gpui-kit (#2702) — нового slice нет

Issue #2702 — self-contained отчёт по ~80 компонентам gpui-kit и их
применимости к CellForge. Проверка `gh pr list --search 2702` и `origin/dev`
(`f1dbd3a1`) показала: топ-5 отчёта уже покрыты — таблицы с сортировкой и
фильтром (#2343, #2320), Tree (`GenericTreeView`, `TreeView`,
`PanelIndex` #2743), fuzzy-пикер (`CommandPaletteView`, `FuzzyMatcher`), ratio-сплиты
(`LayoutTree`, #2388) и ProgressEta (#2870). Пункт, названный «самой простой
реально отсутствующей частью», Slider, влит PR #2397 ещё после снимка
`c4c6b50d`, на который опирался отчёт. GPU-специфичные категории (SVG,
изображения, hover, 120 FPS, AccessKit, WASM, мышиный drag) отвергнуты как
несовместимые с BCL-only AOT-моделью и cell-diff рендером.

Decided: код не писать. Golden-файлы, AOT-поведение, гейты и пороги не
меняются; issue остаётся открытым источником сравнения. Пересмотр — по новому
факту: владелец называет потребителя и измеримую цель для отсутствующего
контракта, например сериализуемых сплитов.

- Полный текст, таблица по топ-5 и условия пересмотра:
  [ADR-038](ADR-038-gpui-kit-components-no-new-slice.md).
## ADR-039: отчёт TUI-композиции (#2688) — все конкретные пункты влиты, нового slice нет

Issue #2688 — self-contained отчёт `tui-composition-2026-11` с семью конкретными
идеями. Проверка `git merge-base --is-ancestor` по `origin/dev` (`192ed9a0`)
показала: все семь влиты отдельными PR. Идея 1 (INode/NodeFrame в хосте) — #2877,
идея 2 (ключевая идентичность DSL) — #2722, идея 3 (Row/Column/Overlay в
SpecKind) — #2527, идея 4 (ReadEntities в инвалидацию) — #2523, идея 5 (кэш
layout) — #2525, идея 6 (`.bind`) — #2768, идея 7 (CompositionPilot) — #2524.
Две идеи отмечены в issue как влитые на момент переоткрытия, остальные пять
влиты до даты снимка отчёта. Расхождение — в дате снимка, а не в пропущенной
работе.

Decided: код не писать. Ни один из семи пунктов не перевыводится, новый guard
не вводится (на дереве, где всё влито, он был бы вакуумным), гейты и пороги не
меняются. Issue остаётся OPEN как источник сравнения; комментарий содержит
только хеш коммита ADR. Пересмотр — по новому факту: владелец называет
потребителя и измеримую цель для действительно отсутствующего контракта.

- Полный текст, таблица по семи идеям с PR и следом в дереве:
  [ADR-039](ADR-039-tui-composition-report-all-slices-landed.md).

## ADR-040: отчёт cellforge-engine-gaps (#2696) — все исполнимые пункты закрыты

Issue #2696 — self-contained отчёт о разрыве CellForge с аналогами (15 позиций,
пять пунктов «что достраивать»). Проверка на `origin/dev` = `bc310e34` и
`git merge-base --is-ancestor` показала: Sixel влит PR 2254 (`62bb4f24`),
фокус-события — PR 2233 (`0f24186c`), тень — PR 2773 (`a6fdf1df`), спрайты и
тайлы — PR 2883 (`5dbc28f1`). Пятый пункт, графемные кластеры, закрыт
owner-decision в ADR-024 по issue 2223: 16-байтовая `Cell` сохраняется,
side-table отложена до санкции владельца. Измерение p50/p95 input latency
помечено `NOT MEASURED` в BENCHMARKS.md и требует отдельного бенчмарка,
а счётчик FPS и tween-менеджер отчёт называет опциональными.

Decided: код не писать. Ни один пункт не перевыводится, новый guard не вводится
(на дереве, где всё влито или owner-decided, он был бы вакуумным), гейты,
пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как источник
сравнения. Пересмотр — если владелец санкционирует вариант 1 из ADR-024 или
назовёт потребителя и измеримую цель для latency-бенчмарка.

- Полный текст, таблица по пяти пунктам и условия пересмотра:
  [ADR-040](ADR-040-cellforge-engine-gaps-report-verdict.md).
## ADR-041: отчёт.cellforge-примитивов (#2698) — все конкретные пункты влиты, нового slice нет

Issue #2698 — self-contained отчёт `cellforge-tui-primitive-gaps` с 15
примитивами и топ-8 по формуле польза/стоимость. Проверка
`git merge-base --is-ancestor` по `origin/dev` (`bc310e34`) показала: все
исполняемые пункты влиты отдельными PR, большая часть ещё до снимка отчёта.
Пункты 1, 3, 6–9, 11, 12 и 15 закрыты PR #2320–#2412; п.10 — PR #2334 и
slice #2698 PR #2769; п.13 (ETA) — PR #2870; п.2 («НЕТ» в отчёте) — PR #2884;
п.7 (история и алиасы) — PR #2329 и #2682. Пункты 4, 5 и 14 помечены «ЕСТЬ»
в самом отчёте. Расхождение с формулировками отчёта — в дате снимка, не в
пропущенной работе.

Decided: код не писать. Все 15 пунктов не перевыводятся, новый guard не
вводится (на дереве, где всё влито, он был бы вакуумным), гейты и пороги не
меняются. Issue остаётся OPEN как источник сравнения; комментарий содержит
только хеш коммита ADR. Пересмотр — по новому факту: владелец называет
потребителя и измеримую цель для действительно отсутствующего контракта.

- Полный текст, таблица по 15 пунктам с PR и следом в дереве:
  [ADR-041](ADR-041-cellforge-tui-primitives-report-all-slices-landed.md).

## ADR-042: отчёт CellForge как нагрузочный тест для игр (#2697) — вердикт по 15 примитивам

Issue #2697 — self-contained отчёт `cellforge-roguelike-demands` с 15
примитивами и топ-10 по формуле польза/стоимость. Проверка
`git merge-base --is-ancestor` по `origin/dev` (`3cefd7cd`) показала: четыре
исполняемых пункта уже влиты — `VisibilityMap` (#2779, `a5cf953e`), numpad в
`KeyCode` и декодерах (#2885, `eb3797d3`), `TileCamera` (#2910, `a1b2e954`) и
`TileAtlas` с `TileMapNode` (#2883, `a436fa9a`). Пункты 8, 10, 12 и 13 помечены
«ЕСТЬ» в самом отчёте. `DirtyRect` и `ViewDamageTracker` в дереве есть
(#2354), но трекер не подключён к продуктовому кадровому циклу. Остаются
отсутствующими игроспецифичные `MinimapView`, `LightingFx`, `TileLayerStack`,
`PaletteTable` и сериализуемый `ScreenSnapshot`.

Decided: код не писать. Влитые пункты не перевыводятся, игроспецифичные не
открываются без названного потребителя и решения владельца. Общие пункты
(`DamageTiles` и подключение `ViewDamageTracker`, continuous mode в REPL
runner) остаются кандидатами на отдельные issue, этот ADR их не закрывает.
Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр —
по новому факту: владелец называет потребителя и измеримую цель для
действительно отсутствующего контракта.

- Полный текст, таблица по 15 пунктам с PR и следом в дереве:
  [ADR-042](ADR-042-cellforge-roguelike-report-verdict.md).

## ADR-043: отчёт DX/docs-пробелов (#2687) — одного большого slice нет

Issue #2687 — self-contained отчёт `dx-docs-gaps` с 60 независимыми идеями про
установку, документацию, CLI, диагностику, контрибутинг и release notes.
Проверка `origin/dev` (`8561cd87`) и истории PR показала: пять пунктов уже
влиты — FAQ по точным строкам ошибок (#2896), symptom → check (#2766),
getting help (#2881), llms.txt и agent skill (#2770, issue #2670) и справочник
переменных окружения (#2858). Остальные идеи (`harbor doctor`, `harbor
completions`, structured output, `docs/CLI_REFERENCE.md`, PR-шаблоны) не имеют
владельца и критерия приёмки.

Decided: код не писать. Влитые пункты не перевыводятся, оставшиеся не
объединяются в meta-PR и открываются отдельными issue с владельцем и измеримой
целью. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR.

- Полный текст, таблица по влитым пунктам и список открытых идей:
  [ADR-043](ADR-043-dx-docs-gaps-report-verdict.md).

## ADR-044: каталог UI-примитивов CellForge (#2706) — нового общего slice нет

Issue #2706 — self-contained отчёт `tui-ui-primitives-catalog` с 15
доменно-независимыми примитивами P01-P15 из 16 README терминальных приложений.
Сам отчёт выделяет три контракта, которые требовали работы: P05 `ChangeBlock`,
P13 faceted record и P04 numeric series. Инвентаризация `origin/dev`
(`f700d122`) и `gh pr list --search 2706` показала: P02 влит как `TwoPaneState`
(#2884), P13 влит как `FacetedHistoryState` (#2903), а PR #2906 держит открытый
контракт P05. Остальные примитивы помечены shipped в
`docs/CELLFORGE_UI_PRIMITIVES.md`.

Decided: код не писать. Влитые пункты не перевыводятся, P05 не дублируется
открытому PR, остальные не открываются без названного потребителя и измеримой
цели. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр —
по новому факту: владелец называет потребителя и цель для ещё не shipped
примитива.

- Полный текст и таблица по трём контрактам:
  [ADR-044](ADR-044-tui-ui-primitives-catalog-verdict.md).

## ADR-045: отчёт product-feature-gaps part 2/3 (#2685) — одного общего slice нет

Issue #2685 — self-contained отчёт `product-feature-gaps` part 2/3 с пятнадцатью
разделами (L/W/D/G/I/E/N/T/R/B/P/O/Q/J/K) и более чем ста независимые concrete
item. Инвентаризация `origin/dev` (`bbd559c2`) и `gh pr list --search 2685`
показала три влитых item: D09 per-file scroll в diff viewer (#2784, `68a22232`),
N07 OSC 777 notification backend (#2876, `e96c2af2`) и T03 property-test на
секреты в telemetry exports (#2875, `2fdc608d`).

Decided: код не писать. Влитые item не перевыводятся, оставшиеся не
объединяются в meta-PR и открываются отдельными issue с владельцем и измеримой
целью. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр —
по новому факту: владелец называет конкретный item, потребителя и цель.

- Полный текст, таблица по влитым item и список открытых разделов:
  [ADR-045](ADR-045-product-feature-gaps-part2-report-verdict.md).

## ADR-046: отчёт product-feature-gaps part 1/3 (#2684) — одного общего slice нет

Issue #2684 — self-contained отчёт `product-feature-gaps` part 1/3 с восемью
разделами (S/U/X/M/V/A/H/C) и более чем ста независимые concrete item.
Инвентаризация `origin/dev` (`608327e0`) и `gh pr list --search 2684` показала
три влитых item: H01 lifecycle events для subagent (#2772, `fc9aabde`), S06
перегенерация заголовка сессии (#2900, `0c30e456`) и S14 cost и usage на уровне
сессии (#2859, `6f222f95`).

Decided: код не писать. Влитые item не перевыводятся, оставшиеся не
объединяются в meta-PR и открываются отдельными issue с владельцем и измеримой
целью. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр —
по новому факту: владелец называет конкретный item, потребителя и цель.

- Полный текст, таблица по влитым item и список открытых разделов:
  [ADR-046](ADR-046-product-feature-gaps-part1-report-verdict.md).

## ADR-047: отчёт gpui-kit-architecture (#2701) — вердикт после переоткрытия

Issue #2701 — self-contained отчёт по архитектурным паттернам gpui-kit и их
применимости к CellForge. ADR-029 зафиксировал вердикт до переоткрытия issue,
после чего владелец переоткрыл отчёт для остальных пунктов. Свежая проверка
`origin/dev` (`96f02608`) через `git merge-base --is-ancestor` подтвердила, что
все семь переносимых идей влиты отдельными PR: приоритет стилей (#2385,
`dcbe063a`), типизированные part (#2393, `1cab27dd`), delegate для
виртуализованных списков (#2387, `7601c667`), ключевая идентичность (#2392,
`3bf57616`), normalize layout и reconciliation по id (#2388, `0ff75cc5`), один
handle на viewport (#2391, `30cc3452`) и время жизни подписок (#2390,
`606ba3b0`).

Decided: код не писать. Влитые пункты не перевыводятся, неприменимые
GPU-механические идеи не портируются, новый guard не вводится (на дереве, где
всё влито, он был бы вакуумным), гейты, пороги, golden и AOT-профиль не
меняются. Issue остаётся OPEN как источник сравнения; комментарий содержит
только хеш коммита ADR. Пересмотр — по новому факту: владелец называет
конкретный отсутствующий контракт, потребителя и измеримую цель.

- Полный текст, таблица по семи идеям и условия пересмотра:
  [ADR-047](ADR-047-gpui-kit-architecture-post-reopen-verdict.md).

## ADR-048: отчёт ampcode-features part 2/2 (#2676) — нового slice нет

Issue #2676 — self-contained отчёт `ampcode-features` part 2/2 с восемью concrete
item (128-135). Инвентаризация `origin/dev` (`96f02608`) и `gh pr list --search
2676` показала два влитых item: 128 именованные промпты (#2794, `d30a894e`) и
133 runner flags (#2897, `38bc284a`).

Decided: код не писать. Влитые item не перевыводятся, оставшиеся не объединяются
в meta-PR. Item 129, 131 и 135 не применимы к терминальному агенту; 130, 132 и
134 требуют владельца и web/VM-поверхности. Гейты, пороги, golden и AOT-профиль
не меняются. Issue остаётся OPEN как источник сравнения; комментарий содержит
только хеш коммита ADR. Пересмотр — по новому факту: владелец называет конкретный
item, потребителя и цель.

- Полный текст, таблица по влитым item и список нереализованных:
  [ADR-048](ADR-048-ampcode-features-part2-report-verdict.md).

## ADR-049: отчёт kilocode-features part 2/2 (#2680) — одного общего slice нет

Issue #2680 — self-contained отчёт `kilocode-features` part 2/2 со 180 concrete
item F001-F180 в тринадцати группах A-M. Инвентаризация `origin/dev`
(`8db8393f`) и `gh pr list --search 2680` показала три влитых item: F157 shell
integration (#2866, `36443d63`), F159 классификатор мутирующих git-команд
(#2792, `947412df`) и F179 детектор базовой ветки (#2907, `7509231f`). Все три
merge commit подтверждены как предки `origin/dev`.

Decided: код не писать. Влитые item не перевыводятся, оставшиеся 177 не
объединяются в meta-PR. Часть item прямо неприменима к терминальному агенту
(F143, F144, F180), часть требует только сверки с уже существующим аналогом
(F117, F129, F135, F147, F150), остальные меняют публичный контракт или новую
поверхность (F094, F102, F108/F109, F134, F153, F158, F169) и требуют
отдельного владельца. Гейты, пороги, golden и AOT-профиль не меняются. Issue
остаётся OPEN как источник сравнения; комментарий содержит только хеш коммита
ADR. Пересмотр — по новому факту: владелец называет конкретный item,
потребителя и измеримую цель.

- Полный текст, таблица по влитым item и список нереализованных:
  [ADR-049](ADR-049-kilocode-features-part2-report-verdict.md).

## ADR-050: отчёт t3code-features part 1/2 (#2673) — одного общего slice нет

Issue #2673 — self-contained отчёт `t3code-features` part 1/2 с 90 concrete
item A1-F11 в шести группах. Инвентаризация `origin/dev` (`82b915c2`) и
`gh pr list --search 2673` показала три влитых item: A17 drainable worker
(#2780, `822c2312`), A3 execution graph (#2863, `66046b72`) и A9 activity feed
(#2871, slice `1006894c`, merge `9f9e5b02`). Все три merge commit подтверждены
как предки `origin/dev`.

Decided: код не писать. Влитые item не перевыводятся, оставшиеся 87 не
объединяются в meta-PR. Часть item неприменима к терминальному агенту (E16,
E17, E18, F6), часть требует только сверки с уже существующим аналогом (B12,
E10, E14, E15, F8, F11), остальные — target design `[spec]` или меняют
публичный контракт (A1, A2, B1, C6, D1, D3, F1) и требуют отдельного владельца.
Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр — по
новому факту: владелец называет конкретный item, потребителя и измеримую цель.

- Полный текст, таблица по влитым item и список нереализованных:
  [ADR-050](ADR-050-t3code-features-part1-report-verdict.md).

## ADR-051: отчёт demo-gifs-metrics (#2691) — нового slice нет

Issue #2691 — self-contained отчёт `demo-gifs-metrics`: аудит четырёх demo GIF,
метрик записи и процедуры из `docs/DEMO_GIFS.md`, `demo.yml`, `demo/*.tape` и
`demo/toolchain.lock`. Инвентаризация `origin/dev` (`46414368`), комментарии
issue и `git grep` показали, что обе правки влиты: базовый slice (#2787, merge
`b186262c`) и замена устаревших путей `tools/demo_repro.py` на
`tools/gates/demo-repro/` в tapes и toolchain.lock (#2879, merge `70409353`).
`git grep -n 'demo_repro' origin/dev -- docs/DEMO_GIFS.md demo/` возвращает
пусто.

Decided: код не писать. FPS ровно 12 и размер 0,919 MiB укладываются в лимиты
гейта; аудит констатирует отсутствие дефекта. Покрытие всех 4 слотов и трёх
сценариев полное; ANSI, `--scene all` и состояния вне демо-команды в корпусе не
предусмотрены. Stale manifest и красный drift gate лечатся действием владельца
`gh workflow run demo.yml -f record_baseline=true`, а не кодом. Гейты, пороги,
golden и AOT-профиль не меняются. Issue остаётся OPEN как источник сравнения;
комментарий содержит только хеш коммита ADR. Пересмотр — по новому факту:
владелец называет конкретный отсутствующий контракт, потребителя и измеримую
цель.

- Полный текст, таблица по двум влитым правкам и выводы аудита:
  [ADR-051](ADR-051-demo-gifs-metrics-report-verdict.md).

## ADR-052: отчёт kilocode-features part 1/2 (#2679) — одного общего slice нет

Issue #2679 — self-contained отчёт `kilocode-features` part 1/2 с 89 concrete
item F001-F089 в семи группах A-G (агенты, разрешения, контекст, чекпоинты,
браузер и изображения, MCP, хуки). Инвентаризация `origin/dev` (`1992dc8f`),
комментарии issue и `git log --all --grep=2679` показали три влитых item: F019
dotenv path guard (#2874, merge `970d7f83`), F037 context section в
`harbor_inspect` (#2862, merge `0eb8a39f`) и F041 env-override для компакции и
прунинга (#2785, merge `2c4964db`). Все три merge commit подтверждены как
предки `origin/dev`.

Decided: код не писать. Три влитых item не перевыводятся, остальные 86 не
объединяются в meta-PR. Item с прямым отказом требуют браузер, сервер или
image-capable провайдер; item «только сверить» уже имеют аналог в Harbor;
item, меняющие публичный контракт, требуют собственного владельца и критерия
приёмки. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN
как источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр
— по новому факту: владелец называет конкретный item, потребителя и измеримую
цель. Соседний part 2/2 (#2680) покрыт ADR-049 и этим решением не перевыводится.

- Полный текст, таблица по трём влитым item и список нереализованных:
  [ADR-052](ADR-052-kilocode-features-part1-report-verdict.md).

## ADR-053: отчёт ampcode-features part 1/2 (#2675) — одного общего slice нет

Issue #2675 — self-contained отчёт `ampcode-features` part 1/2 со 127 concrete
item 001-127 в семи группах A-H. Проверка `origin/dev` (`32fe88b9`), комментарии
issue, `gh pr list --search 2675` и `git log --all --grep=2675` показали десять
влитых item: 002 (#2952, `90aa35a0`), 010 (#2938, `b7763523`), 022 (#2941,
`1faa3561`), 030 (#2918, `8a58d1ed`), 041 (#2951, `69e2270a`), 049 (#2778,
`acff568d`), 070 (#2946, `11d4cfc2`), 071 (#2861, `05e3c7ad`), 103 (#2944,
`ad7b8b81`) и 121 (#2911, `01a76c09`). Все merge commit подтверждены как предки
`origin/dev`.

Decided: код не писать. Десять влитых item не перевыводятся, остальные не
объединяются в meta-PR. Item с прямым отказом требуют image-модель, удалённый
сервер или desktop GUI; item «только сверить» уже имеют аналог в Harbor; item,
меняющие публичный контракт, требуют собственного владельца и критерия приёмки.
Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр — по
новому факту: владелец называет конкретный item, потребителя и измеримую цель.
Соседний part 2/2 (#2676) покрыт ADR-048 и этим решением не перевыводится.

- Полный текст, таблица по десяти влитым item и список нереализованных:
  [ADR-053](ADR-053-ampcode-features-part1-report-verdict.md).

## ADR-054: отчёт product-feature-gaps part 3/3 (#2686) — одного общего slice нет

Issue #2686 — self-contained отчёт `product-feature-gaps` part 3/3 с concrete
item Q05-Q08, J01-J08 и K01-K06. Проверка `origin/dev` (`788e3713`), комментарии
issue, `gh pr list --search 2686` и `docs/adr/DECISIONS.md` показали девять
влитых item: Q06 (#2795, `e620a41f`), J01 (#2925, `ff39057e`), J02 (#2919,
`22dd6e9a`), J03 (#2935, `b537183d`), J04 (#2869, `bd98e8aa`), J05 (#2949,
`d0d38cf6`), J06 (#2916, `a30bf9d`), K05 (#2943, `540c0032`) и Q07 (#2931,
`295ae923`). Все merge commit подтверждены как предки `origin/dev`.

Decided: код не писать. Девять влитых item не перевыводятся, остальные не
объединяются в meta-PR. J08 и K02 прямо помечены как неприменимые; J07, K03, K04
и K06 перенаправлены в соседние slice и гейты; K01 реализуется отдельным
PR #2957 по #2674 G11; Q05, Q08 и J07/W07 требуют владельца и измеримой цели.
Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр — по
новому факту: владелец называет конкретный item, потребителя и измеримую цель.
Соседние part 1/3 (#2684) и part 2/3 (#2685) покрыты ADR-046 и ADR-045 и этим
решением не перевыводятся.

- Полный текст, таблица по девяти влитым item и список нереализованных:
  [ADR-054](ADR-054-product-feature-gaps-part3-report-verdict.md).

## ADR-055: отчёт desktop-ux-gaps part 2/2 (#2683) — одного общего slice нет

Issue #2683 — self-contained отчёт `desktop-ux-gaps` part 2/2 с concrete item
K13–K41 и десятью formulation в «Итоге». Проверка `origin/dev` (`e5656566`),
комментарии issue, `gh pr list --search 2683`, `git log --all --grep=2683` и
`docs/adr/DECISIONS.md` показали пять влитых item: K29 (#2788, `5c09f4fb`),
K13 (#2891, `b08e1228`), K24 (#2905, `e109d344`), K25 (#2917, `5a17b417`) и
K17 (#2929, `6c055f1c`). Все merge commit подтверждены как предки
`origin/dev`.

Decided: код не писать. Пять влитых item не перевыводятся, остальные не
объединяются в meta-PR. K14, K16, K23, K26, K38 и K15 требуют точечного порта
существующего аналога; K18–K22, K27–K28 и K30–K40 требуют владельца и
измеримой цели; K39 прямо обусловлен появлением voice input; K41 — правило
состояний, а не runtime-контракт. Десять formulation в «Итоге» смешивают
K-пункты с Z/G-пунктами part 1/2 (#2682), который этим решением не
перевыводится. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся
OPEN как источник сравнения; комментарий содержит только хеш коммита ADR.
Пересмотр — по новому факту: владелец называет конкретный item, потребителя и
цель.

- Полный текст, таблица по пяти влитым item и список нереализованных:
  [ADR-055](ADR-055-desktop-ux-gaps-part2-report-verdict.md).

## ADR-056: отчёт testing-qa-gaps part 2/2 (#2705) — одного общего slice нет

Issue #2705 — self-contained отчёт `testing-qa-gaps` part 2/2 с пунктами X01 и
X02, десятью «формулировками issue» и итоговой таблицей на 57 идей. Проверка
`origin/dev` (`e5656566`), комментарии issue, `gh pr list --search 2705` и
`docs/adr/DECISIONS.md` показали три влитых item с прямой меткой `#2705`: T03
единый seed (#2776, `6633f102`), T04 детерминированный event drain (#2912,
`3cefd7cd`) и T08 fault injection в `MockLlmServer` (#2904, `8ae547fb`). Все
merge commit подтверждены как предки `origin/dev`.

Decided: код не писать. Три влитых item не перевыводятся, остальные не
объединяются в meta-PR. P02 и X01 прямо помечены в отчёте как `ЕСТЬ`; соседние
срезы part 1/2 (#2704) закрывают fake time, seeded JSONL fuzz, культуру и
event-transcript отдельными PR. Quarantine lane, flake accounting из TRX,
coverage изменённых строк, отказ от MCP-fallback, авто-pin ChatPalette и X02
(категории `fast`/`ui`/`flaky`) требуют владельца, CI-поверхности и измеримой
цели. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как
источник сравнения; комментарий содержит только хеш коммита ADR. Пересмотр — по
новому факту: владелец называет конкретный item, потребителя и измеримую цель.
Соседний part 1/2 (#2704) покрыт своими slices и этим решением не перевыводится.

- Полный текст, таблица по трём влитым item и список нереализованных:
  [ADR-056](ADR-056-testing-qa-part2-report-verdict.md).

## ADR-057: отчёт goose-features part 2/2 (#2678) — нового slice нет

Issue #2678 — self-contained отчёт `goose-features` part 2/2 с concrete item
87–117 и безымянным первым блоком (item 86). Проверка `origin/dev`
(`d8e4856c`), комментарии issue, `gh pr list --search 2678`,
`git log --all --grep=2678` и `docs/adr/DECISIONS.md` показали шесть влитых
item: 92 (#2894, `d08f09bd`), 93 (#2928, `e6793a36`), 99 (#2864, `6355f2e1`),
100 (#2796, `57f55b03`), 113 (#2939, `515c961e`) и 114 (#2921, `bbd559c2`).
Все merge commit подтверждены как предки `origin/dev`. Номер 057 выбран,
потому что 055 занят ADR-055 за #2683, а 056 влит PR #2969 за #2705.

Decided: код не писать. Шесть влитых item не перевыводятся, остальные не
объединяются в meta-PR. Пункты 88, 95, 97 и 105 имеют готовый аналог в Harbor;
86, 87, 89, 96, 101, 102, 104, 106, 107, 108, 109, 110, 115, 116 и 117
требуют владельца и измеримой цели; 90, 91, 94, 98, 103, 111 и 112 отчёт
помечает как неприменимые или слишком крупные. Приоритетные кластеры 1–5
итоговой сводки относятся к part 1/2 (#2677) и здесь не перевыводятся;
кластер 6 (99–105) частично закрыт влитыми 99 и 100. Гейты, пороги, golden и
AOT-профиль не меняются. Issue остаётся OPEN как источник сравнения;
комментарий содержит только хеш коммита ADR. Пересмотр — по новому факту:
владелец называет конкретный item, потребителя и цель.

- Полный текст, таблица по шести влитым item и список нереализованных:
  [ADR-057](ADR-057-goose-features-part2-report-verdict.md).

## ADR-058: отчёт desktop-ux-gaps part 1/2 (#2682) — одного общего slice нет

Issue #2682 — self-contained отчёт `desktop-ux-gaps` part 1/2 со 100 concrete
item (Z01–Z68, G01–G20, K01–K12). Инвентаризация `origin/dev` (`0068e8e9`),
комментарии issue, `gh pr list --search 2682`, `git log --all --grep=2682` и
`docs/adr/DECISIONS.md` показали семь влитых item: Z01 (#2873, `8de31c8e`),
Z02 (#2783, `07ae68e0`), Z03 (#2872, `09a673bc`), Z06 (#2948, `ff148bb8`),
Z09 (#2954, `8db8393f`), Z15 (#2959, `7752e267`) и Z20 (#2965, `383a7139`).
Все merge commit подтверждены как предки `origin/dev`; Z10 влит внутри Z01 как
`QueryHistory.ResetNavigation()`.

Decided: код не писать. Влитые item не перевыводятся, остальные не объединяются
в meta-PR. Палитра, дерево файлов, редактор/diff, collaboration, GPUIX и
gpui-kit требуют собственного владельца, поверхности и измеримой цели; часть
пунктов отчёт прямо относит к не-MVP или к multi-user среде. Гейты, пороги,
golden и AOT-профиль не меняются. Issue остаётся OPEN как источник сравнения;
комментарий содержит только хеш коммита ADR. Пересмотр — по новому факту:
владелец называет конкретный item, потребителя и цель. Part 2/2 (#2683) уже
зафиксирован ADR-055 и этим решением не перевыводится.

- Полный текст, таблица по семи влитым item и список нереализованных:
  [ADR-058](ADR-058-desktop-ux-part1-report-verdict.md).

## ADR-059: отчёт goose-features part 1/2 (#2677) — нового общего slice нет

Issue #2677 — self-contained отчёт `goose-features` part 1/2 с concrete item
1–86 в семи разделах (A Extensions/MCP, B Hooks, C Права и безопасность,
D Agent loop, E Контекст, F Recipe, G Сессии). Инвентаризация `origin/dev`
(`b84a80d1`), комментарии issue, `gh pr list --search 2677`,
`git log --all --grep=2677` и `docs/adr/DECISIONS.md` показали четыре влитых
item: 2 (#2914, `722684ec`), 7 (#2774, `eb33bc33`), 59 (#2908, `94e17e0e`) и
60 (#2860, `7c4cea70`). Все merge commit подтверждены как предки
`origin/dev` через `git merge-base --is-ancestor`.

Decided: код не писать. Влитые item не перевыводятся, остальные не объединяются
в meta-PR. Extensions, hooks, права, конвейер агента, контекст, рецепты и
сессии требуют собственного владельца, публичного контракта и измеримой цели;
часть пунктов имеет готовый аналог в Harbor или прямо помечена как
неприменимая. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся
OPEN как источник сравнения; комментарий содержит только хеш коммита ADR.
Пересмотр — по новому факту: владелец называет конкретный item, потребителя и
цель. Part 2/2 (#2678) уже зафиксирован ADR-057 и этим решением не
перевыводится.

- Полный текст, таблица по четырём влитым item и список нереализованных:
  [ADR-059](ADR-059-goose-features-part1-report-verdict.md).

## ADR-060: отчёт t3code-features part 2/2 (#2674) — одного общего slice нет

Issue #2674 — self-contained отчёт `t3code-features` part 2/2 с concrete item
F12–F14, G1–G14, H1–H14 и таблицей I1–I15 «честно неприменимо»; весь отчёт
насчитывает 121 item в обеих частях. Инвентаризация `origin/dev` (`5406294e`),
комментарии issue, `gh pr list --search 2674`, `git log --all --grep=2674`,
`git merge-base --is-ancestor` и `docs/adr/DECISIONS.md` показали семь влитых
item: F13 (#2923, `608327e0`), F14 (#2880, `71893b36`), G2 (#2868,
`ac436e30`), G3 (#2793, `e797a2b7`), G11 (#2957, `0068e8e9`), H8 (#2913,
`6814c594`) и H12 (#2933, `5dc29584`). Все merge commit подтверждены как
предки `origin/dev`.

Decided: код не писать. Влитые item не перевыводятся, остальные не объединяются
в meta-PR. G8, G12, G14, H6, H7, H9, H10, H13, H14 и вся таблица I1–I15 не
применимы к терминальному агенту или требуют облака; G6, G7, G9, G10 и G5 в
части log-ротации имеют готовый аналог. Остальные item DX, телеметрии и
source-control требуют собственного владельца и измеримой цели. Гейты, пороги,
golden и AOT-профиль не меняются. Issue остаётся OPEN как источник сравнения;
комментарий содержит только хеш коммита ADR. Пересмотр — по новому факту:
владелец называет конкретный item, потребителя и цель. Part 1/2 (#2673) уже
зафиксирован ADR-050 и этим решением не перевыводится.

- Полный текст, таблица по семи влитым item и список нереализованных:
  [ADR-060](ADR-060-t3code-features-part2-report-verdict.md).

## ADR-061: отчёт testing-qa-gaps part 1/2 (#2704) — одного общего slice нет

Issue #2704 — self-contained отчёт `testing-qa-gaps` part 1/2 с идеями в 15
разделах (T, S, H, E, F, P, M, C, B, V, I, D, R, Q, X); тело обрывается на X01.
Инвентаризация `origin/dev` (`5406294e`), комментарии issue,
`gh pr list --search 2704`, `git log --all --grep=2704` и
`docs/adr/DECISIONS.md` показали девять влитых item: T04 (#2767, `d1f16eb7`),
E03 (#2909, `8561cd87`), E02 (#2922, `966c8609`), P03 (#2926, `774b8132`),
D02 (#2932, `5a1ebb7a`), E04 (#2937, `44098751`), T05 (#2942, `0a214da8`),
H05 (#2947, `55dff1e5`) и H03 (#2953, `94a7fb06`). Все merge commit
подтверждены как предки `origin/dev` через `git merge-base --is-ancestor`.

Decided: код не писать. Влитые item не перевыводятся, остальные не объединяются
в meta-PR. Часть идей помечена отчётом как `ЕСТЬ`; quarantine lane, TRX-accounting
и coverage diff требуют CI-поверхности; пилот, event-barrier, планировщик и
hit-testing требуют нового публичного контракта. Гейты, пороги, golden и
AOT-профиль не меняются. Issue остаётся OPEN как источник сравнения;
комментарий содержит только хеш коммита ADR. Пересмотр — по новому факту:
владелец называет конкретный item, потребителя и цель. Part 2/2 (#2705) уже
зафиксирован ADR-056 и этим решением не перевыводится.

- Полный текст, таблица по девяти влитым item и список нереализованных:
  [ADR-061](ADR-061-testing-qa-part1-report-verdict.md).

## ADR-062: отчёт orca-features part 1/2 (#2671) — одного общего slice нет

Issue #2671 — self-contained отчёт `orca-features` part 1/2, объединяющий два
проекта (`echoVic/orca-agent` `[A]` и `stablyai/orca` `[D]`); concrete item
A01–A20, B01–B22, C01–C15, D01–D13, E01–E27 и F01–F02, тело обрывается на F02.
Инвентаризация `origin/dev` (`01e2f3f7`), комментарии issue,
`gh pr list --search 2671`, `git log --all --grep=2671` и
`docs/adr/DECISIONS.md` показали три влитых item: E27 schema version на
`AgentEvent` (#2775, `cbadef6d`), D07 `ServiceUsage` (#2857, `cf9acbe5`) и D04
`CacheReuseMetrics` (#2893, `0010475f`). Все merge commit подтверждены как
предки `origin/dev` через `git merge-base --is-ancestor`.

Decided: код не писать. Влитые item не перевыводятся, остальные не объединяются
в meta-PR. Часть имеет готовый аналог в Harbor (`PermissionRuleset`,
`SessionForkService`, `SkillTool`, `SplitPane` и др.), часть привязана к
графической/десктопной поверхности (inline image, clipboard, drag-and-drop,
fleet worktree), часть требует своего владельца и публичного контракта
(`ApprovalMode`, workflow script, `IVisionRouter`, `IDiffRenderer`). Гейты,
пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как источник
сравнения; комментарий содержит только хеш коммита ADR. Пересмотр — по новому
факту: владелец называет конкретный item, потребителя и цель. Part 2/2 (#2672)
этим решением не перевыводится.

- Полный текст, таблица по трём влитым item и список нереализованных:
  [ADR-062](ADR-062-orca-features-part1-report-verdict.md).

## ADR-063: отчёт avalonia-product-gaps (#2681) — одного общего slice нет

Issue #2681 — self-contained аудит `Harbor.App.Avalonia`: разделы 0–20, 100
Concrete item G01–G100 плюс таблица «какие контролы Avalonia нельзя взять как
есть». Инвентаризация `origin/dev` (`204f4f44`), комментарии issue,
`gh pr list --search 2681`, `git log --all --grep=2681` и
`docs/adr/DECISIONS.md` показали одиннадцать влитых item: G08 (#2777,
`4fce521c`), G65 (#2867, `aa94aa30`), G25 (#2899, `54a34573`), G48 (#2940,
`3163dfbd`), G77 (#2945, `286a24d6`), G02 (#2955, `5125da50`), G85 (#2950,
`3d6d39a7`), G27 (#2963, `1e02befc`), G18 (#2967, `e87e93df`), G74 (#2973,
`ea8acf10`) и G81 (#2974, `b21f2f78`). Все merge commit подтверждены как предки
`origin/dev` через `git merge-base --is-ancestor`. Item G24 находится в открытом
PR #2980.

Decided: код не писать. Влитые item и открытый PR не перевыводятся, остальные не
объединяются в meta-PR. Human-in-the-loop (G05/G06, G38–G44) требует публичного
контракта и composition root; a11y E2E, perf budget и golden (G71–G73, G79,
G84, G88, G98–G100) требуют CI-поверхности; OS-уведомления, tray, background
runs и packaging (G45/G46, G50, G93/G94) требуют владельца платформы; часть
пунктов выведена отчётом из scope (G07, G31/G56, G61, G80). Гейты, пороги,
golden и AOT-профиль не меняются. Issue остаётся OPEN как источник сравнения;
комментарий содержит только хеш коммита ADR. Пересмотр — по новому факту:
владелец называет конкретный item, потребителя и цель.

- Полный текст, таблица по одиннадцати влитым item и список нереализованных:
  [ADR-063](ADR-063-avalonia-product-gaps-report-verdict.md).

## ADR-064: отчёт orca-features part 2/2 (#2672) — одного общего slice нет

Issue #2672 — self-contained отчёт `orca-features` part 2/2, продолжающий
part 1/2 (#2671). Concrete item F03–F13 и G01–G13, раздел 8 «честно не
применимо». Инвентаризация `origin/dev` (`50011c23`), комментарии issue,
`gh pr list --search 2672`, `git log --all --grep=2672` и
`docs/adr/DECISIONS.md` показали три влитых item: F06 `RetainedOutput`
(#2781, merge `d193931e`), F13 `FirstRunState` (#2865, merge `a4220347`) и
G03 `DiffReviewState` (#2898, merge `ed9b457a`). Все merge commit подтверждены
как предки `origin/dev` через `git merge-base --is-ancestor`.

Decided: код не писать. Влитые item не перевыводятся, остальные не
объединяются в meta-PR. F04 и F10 имеют готовый аналог (`IToolRegistry`,
`Harbor.Plugins.*`); G06, G11, G12 и G13 не применимы к терминальному агенту;
остальные требуют своего владельца и публичного контракта (`IExtensionData`,
`IConfigurationResolver`, `ISandboxProbe`, `INetworkPolicy`, `IAutomationStore`,
`IExecutionHost` и др.). Гейты, пороги, golden и AOT-профиль не меняются. Issue
остаётся OPEN как источник сравнения; комментарий содержит только хеш коммита
ADR. Пересмотр — по новому факту: владелец называет конкретный item,
потребителя и цель. Part 1/2 (#2671) этим решением не перевыводится.

- Полный текст, таблица по трём влитым item и список нереализованных:
  [ADR-064](ADR-064-orca-features-part2-report-verdict.md).

## ADR-065: отчёт каталога UI-примитивов (#2706), P05 влит

Issue #2706 — self-contained отчёт с 15 примитивами CellForge. ADR-044
зафиксировал P02 и P13 влитыми, а P05 как открытый PR #2906. Проверка
`origin/dev` (`74060665`) и `gh pr view 2906` подтвердила merge `650f4b38`:
`NavigableChangeBlocks` и его тесты в дереве. Все три примитива, требовавшие
тонкого контракта, закрыты.

Decided: код не писать. P02, P13 и P05 не перевыводятся, P04 и остальные
shipped-примитивы не открываются заново без владельца и измеримой цели. Гейты,
пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN как источник
сравнения; комментарий содержит только хеш коммита ADR. Пересмотр — по новому
факту: владелец называет потребителя и цель для ещё не закрытого примитива.

- Полный текст, таблица по трём контрактам и условия пересмотра:
  [ADR-065](ADR-065-tui-ui-primitives-catalog-post-verdict.md).

## ADR-066: issue #2667 (scheduler и webhooks в daemon) без общего slice

Issue #2667 выведен из `product-feature-gaps.md` разделами K01 и N01-N03, N07.
Он просит `harbor task schedule --cron --prompt`, `harbor daemon hook` с HMAC,
dedupe и rate-limit, а также `INotificationPolicy` с snooze и OSC 777.
Проверка `origin/dev` (`74060665`) и `gh pr list --search 2667` (ноль PR)
показала, что отправительная половина уже влита через #2674 G11
(PR [#2957](https://github.com/refusedguy/Harbor-Harness/pull/2957)), вместе
с five-field cron, `tasks.json`, HMAC-SHA256 и fixed-time сравнением.
ADR-060 уже отнёс HTTP-эндпоинт `harbor daemon hook` и fire-диспетчер к тому
же item G11.

Decided: код не писать. Принимающий эндпоинт, dedupe, rate-limit 60 req/min и
политика уведомлений с snooze остаются без владельца и требуют отдельной
issue и PR. Существующая отправка не перевыводится. Гейты, пороги, golden и
AOT-профиль не меняются. Issue остаётся OPEN; комментарий содержит только
хеш коммита ADR. Пересмотр по новому факту: владелец называет конкретную
часть, потребителя и измеримую цель.

- Полный текст, таблица по четырём готовым частям и список недостающих:
  [ADR-066](ADR-066-scheduler-webhooks-daemon-verdict.md).

## ADR-070: отчёт dx-docs-gaps (#2687) после вердикта, влиты item 1, 4 и 14

> Запись вставлена после ADR-066, а не в конец файла: смежные verdict-ADR
> добавляются параллельно, и конец файла сдвигается быстрее, чем этот PR
> проходит CI. Порядок номеров в файле уже не строго восходящий (ADR-036
> стоит перед ADR-029).

Issue #2687 — self-contained отчёт `dx-docs-gaps` с 60 идеями. ADR-043
зафиксировал вердикт на базе `8561cd87` и нашёл пять влитых идей. Проверка
`origin/dev` (`6efb6df6`, повторена на `56608667`) и `gh pr list --search 2687`
показала три slice, влитых после вердикта: item 1, маршрут онбординга с
mental model в `docs/GETTING_STARTED.md` (PR #2989, merge `ff7f369a`), item 4,
страница `docs/DAILY_BUILDS.md` с записью в `docs/toc.yml` (PR #2986, merge
`d667014f`), и item 14, `docs/UPGRADE.md` (PR #2991, merge `6e39897b`). Все
merge commit подтверждены как предки текущей базы через
`git merge-base --is-ancestor`.

Decided: код не писать. Вердикт ADR-043 сохраняется, общего slice по отчёту
нет. Три влитых slice не перевыводятся. Одновременно ADR-043 называл
`harbor completions` и `harbor doctor` открытыми идеями, хотя оба уже были в
дереве на момент вердикта (PR #2763, `a45bc7d6`, issue #2669, и `a3ba0fbe`,
issue #2668); это устаревшее наблюдение, а не новая работа. Оставшиеся идеи,
включая `docs/CLI_REFERENCE.md`, `docs/MIGRATION.md` и таблицу путей установки,
требуют отдельного владельца и измеримой цели. Гейты, пороги, golden и
AOT-профиль не меняются. Issue остаётся OPEN; комментарий содержит только хеш
коммита ADR. Пересмотр по новому факту: владелец называет конкретную идею,
потребителя и цель.

- Полный текст, таблица по трём новым slice и список устаревших наблюдений:
  [ADR-070](ADR-070-dx-docs-gaps-post-verdict.md).

## ADR-081: отчёт dx-docs-gaps (#2687) после вердикта, влита идея 3

> Запись вставлена после ADR-070, а не в конец файла: смежные verdict-ADR по
> #2687 добавляются параллельно. Номер 081 появился после перенумерации:
> 079 влит по issue #1435 (PR #3010), 078 закреплён за открытым PR #3008.

Issue #2687 — self-contained отчёт `dx-docs-gaps` с 60 идеями. ADR-043
зафиксировал вердикт на базе `8561cd87`, ADR-070 дополнил его на базе
`56608667` и назвал таблицу путей установки (идея 3) отсутствующей. Проверка
`origin/dev` (`36fcd2a1`) показывает, что идея 3 влита: раздел `Install paths`
в `docs/GETTING_STARTED.md:138`, slice `8cdb0082`, merge `8f38b89c` (PR #3006,
подтверждён как предок базы через `git merge-base --is-ancestor`).

Decided: вердикты ADR-043 и ADR-070 сохраняются, одного общего slice по
отчёту нет. Идея 3 не перевыводится. Идея 34 (`docs/MIGRATION.md`) остаётся в
открытом PR #3007 и сюда не включается; `docs/CLI_REFERENCE.md` не имеет
владельца. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся
OPEN; комментарий содержит только хеш коммита ADR. Пересмотр по новому факту:
владелец называет конкретную идею, потребителя и цель.

- Полный текст, таблица по идее 3 и условия пересмотра:
  [ADR-081](ADR-081-dx-docs-gaps-idea3-post-verdict.md).

## ADR-074: Thread lineage + merge-back из форка дельтой (#2660) — вердикт

> Запись вставлена после ADR-070, а не в конец файла: см. объяснение в самом
> ADR-070 — смежные verdict-ADR добавляются параллельно, и конец файла сдвигается
> быстрее, чем этот PR проходит CI.

Issue #2660 (gap `product-feature-gaps` S01/S03/G04) просит `ThreadRelation` и
`session_merge_back` с `--strategy 3way` и базой на точке форка. Проверка
`origin/dev` и `gh pr list --search 2660`: PR нет, ADR-вердикта по #2660 нет.
Первая половина уже влита — `ParentSessionId`, `SessionForkService` (slice
`ac33aec5`), `harbor sessions fork` (slice `a025edbf`); ADR-062 уже зафиксировал
форк как готовый аналог. Вторая половина (`ThreadRelation`, `session_merge_back`,
`3way`, тест на конфликт) в дереве отсутствует.

Decided: код не писать. Линия форка не перевыводится. Merge-back не открывается
заодно: нужны контракт хранения `ThreadRelation`, правило идентичности
сообщений между fork и merge, форма результата конфликта `3way`, поверхность
инструмента и запись в `PermissionRuleset.Default`. Гейты, пороги, golden и
AOT-профиль не меняются. Issue остаётся OPEN; комментарий содержит только хеш
коммита ADR. Пересмотр по новому факту: владелец называет эти контракты и
потребителя инструмента. Номер 074 выбран, потому что пока PR был открыт, 071 и
072 закрепили за другими issue, а 073 занят открытым PR.

- Полный текст и ссылки на код:
  [ADR-074](ADR-074-thread-lineage-merge-back-verdict.md).

## ADR-067: issue #2663 (tool search / deferred tools) без общего slice

Issue #2663 предлагает builtin `tool_search(query)` с top-10 по name/description
и догрузку выбранных инструментов в `ToolTableBuilder` на следующий turn.
Критерий приёмки: токены промпта падают при том же наборе инструментов, плюс
тест поиска. Проверка `origin/dev` (`279ac626`) показала, что `tool_search` и
ранжирования нет, `ToolTableBuilder` остаётся чистым построителем
`ToolDefinition[]`, `IToolRegistry` не имеет поискового API, а измерителя
токенов tool table в дереве нет. Ограничение issue про freeze #555 устарело:
владелец снял freeze 2026-10-20.

Decided: код не писать. Ранжирующий индекс, отложенная сборка tool table и
измерение токенов не объединяются в один PR: у каждого свой интерфейс и свой
критерий приёмки. Новый builtin этим PR не открывается: без отложенной table
он только добавляет запись в тот же промпт. Гейты, пороги, golden и
AOT-профиль не меняются. Issue остаётся OPEN; комментарий содержит только
хеш коммита ADR. Пересмотр по новому факту: владелец называет потребителя,
измеритель токенов tool table и цель.

- Полный текст, таблица по четырём частям и условия пересмотра:
  [ADR-067](ADR-067-tool-search-deferred-tools-verdict.md).

## ADR-068: issue #2666 (PR watch / минимальный IForgeClient) — нового slice нет

Issue #2666 выведен из `product-feature-gaps.md` J01-J03. Он просит builtin
`watch_pull_request(n)` с polling gh и `SessionSteerEvent` при failed check,
конфликте или комментарии, первый адаптер GitHub через gh, связи PR ↔ сессия
в метаданных и hermetic-тест с mock gh. Проверка `origin/dev` (`bb507484`),
комментариев issue, `gh pr list --search 2666`, `git log --all --grep=2666` и
`docs/adr/DECISIONS.md` показала, что три из четырёх частей влиты по #2686
J01-J03: `IForgeClient` с `GitHubForgeClient` (#2919, merge `22dd6e9a0`),
wake-политика `PullRequestWatchProjection` с лимитами и чистыми тестами
(#2925, merge `ff39057e`) и связи PR ↔ сессия с обратным поиском (#2935,
merge `b537183d`). approval-gated `session_steer` тоже есть в дереве.

Decided: код не писать. Влитые J01-J03 не перевыводятся. Builtin
`watch_pull_request` не открывается заодно с CLI-verb `harbor pr watch`: ему
нужны владелец жизненного цикла, реестр долгоживущих watch, отмена по
завершении сессии и контракт на `SessionSteerEvent`. Триггер по комментарию
меняет сигнатуру dedupe влитой проекции и требует своего владельца.
Acceptance «агент просыпается и чинит check» нечем проверить без E2E-поверхности
с mock gh внутри agent loop. Гейты, пороги, golden и AOT-профиль не меняются.
Issue остаётся OPEN; комментарий содержит только хеш коммита ADR. Пересмотр по
новому факту: владелец называет контракт для долгоживущего watch, потребителя
`SessionSteerEvent` и измеримую цель.

- Полный текст, таблица по четырём частям и условия пересмотра:
  [ADR-068](ADR-068-pr-watch-iforge-client-verdict.md).

## ADR-069: отчёт gpui declarative devtools (#2700), post-verdict по влитому #2765

Issue #2700 — self-contained отчёт по devtools для декларативного UI. ADR-034
зафиксировал вердикт на базе `09a673bc` и оставил PR #2765 единственным
in-flight slice, не предрешая его исход. Проверка через `gh pr view` и
`git merge-base --is-ancestor` на базе `origin/dev` (`56608667`) подтвердила
merge `192ed9a0` в `dev`: debounce, burst coalescing и atomic publication
watcher'а в дереве. Остальные идеи отчёта влиты или owner-decided.

Decided: код не писать. Влитый #2765 не перевыводится, остальные идеи не
открываются заново, sentinel slot discovery остаётся за ADR-034. Гейты,
пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN; комментарий
содержит только хеш коммита ADR. Пересмотр по новому факту: владелец
представляет compiled-template дизайн, которому нужен runtime slot discovery.

- Полный текст и ссылка на влитый slice:
  [ADR-069](ADR-069-gpui-declarative-devtools-post-verdict.md).

## ADR-071: issue #2662 (execution graph и command receipts) без общего slice

Issue #2662 просит `ExecutionNode { nodeId, runId, kind }`,
`ICommandReceiptStore`, запись receipt и события в одной sqlite-транзакции,
`RunAttempt` с причиной и `CompletionBarrier` до `AgentEndEvent`. Критерий
приёмки: повтор `commandId` не создаёт побочных эффектов, порядок событий
зафиксирован golden-тестом. Инвентаризация `origin/dev` (`27c312f7`) и
`gh pr list --search 2662` показала, что граф исполнения уже влит отдельными
item #2673 A3 (PR #2863, `a72c5786`) и A9 (PR #2871, `1006894c`), а
`ICommandReceiptStore`, `RunAttempt` и `CompletionBarrier` в дереве отсутствуют.

Decided: код не писать. Влитый граф не перевыводится. Receipt store и барьер
имеют разные контракты и разные критерии приёмки, поэтому не объединяются в
один PR. Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN;
комментарий содержит только хеш коммита ADR. Пересмотр — по новому факту:
владелец называет конкретную часть, потребителя контракта и измеримую цель.
Номер 071 выбран, потому что пока PR был открыт, 069 и 070 закрепили за другими
issue.

- Полный текст, таблица по трём частям и условия пересмотра:
  [ADR-071](ADR-071-execution-graph-command-receipts-verdict.md).

## ADR-072: file-level rewind с pre-image (#2659) — проект решения

Issue #2659 из `product-feature-gaps.md` (U01-U06) просит content-addressed blob
store вместо git-зависимого отката: pre-image перед первым write в turn, tombstone
для созданных файлов, `touchedPaths` вместо полного snapshot и трёхуровневый guard
«оригинал изменился» перед restore. Проверка `origin/dev` (`27c312f7`),
комментариев issue, `gh pr list --search 2659` и `git log --all --grep=2659`
показала, что влитого PR и предыдущего ADR-вердикта нет; ни один из U01-U06 в
дереве не представлен. Чекпойнт-стоуры хранят сообщения, а не содержимое файлов,
а [ADR-022](ADR-022-session-log-append-only.md) требует append-only журнала.

Decided: зафиксировать проект решения и dependency order будущего slice, код не
писать. Реализация затрагивает contracts, storage, CLI и DI и не принимается без
локальной сборки: публичные контракты подпадают под freeze, новый storage-тип
требует обновления seam graph. Гейты, пороги, golden и AOT-профиль не меняются.
Issue остаётся OPEN; комментарий содержит только хеш коммита ADR. Пересмотр по
новому факту: владелец называет потребителя `IFileHistoryTracker`, измеримую цель
для `Conflict` и владельца реестра `touchedPaths`. Номер 072 выбран, потому что
071 закрепил за собой PR #2998 (issue #2662), пока этот PR был открыт.

- Полный текст, таблица U01-U06 и dependency order:
  [ADR-072](ADR-072-file-rewind-preimage-design.md).

## ADR-073: issue #2661 (MCP elicitation и roots как client capabilities)

Issue #2661 просит `roots/list`, `notifications/roots/list_changed` при смене
workingDirectory и `ElicitationRequestedEvent` с CLI-формой (обязательные поля,
defaults, таймаут 300 с, Ctrl+C). Base `origin/dev` (`11db4c63`): клиентского
`initialize` нет, `McpJsonRpcTransport` не различает ответ и входящий запрос
сервера, готового события и responder нет. `IUserQuestionService` уже даёт форму
с таймаутом до 10 минут и отменой через `ct`, поэтому окно 300 с не требует
нового механизма ввода.

Decided: код не писать. Issue делится на три slice с разными контрактами:
handshake и roots внутри `Harbor.Tools.Builtin/Tools/Mcp` без изменения
`IMcpRegistry`; событие в `Harbor.Abstractions.Contracts` и responder через
`IUserQuestionService`; mock-тест с scripted потоками, где `elicitation/create`
приходит посреди `tools/list` и не роняет turn. Гейты, пороги, golden и
AOT-профиль не меняются. Issue остаётся OPEN; комментарий содержит только хеш
коммита ADR. Пересмотр по новому факту: владелец называет потребителя и
mock-тест одной из частей.

- Полный текст, инвентаризация и условия пересмотра:
  [ADR-073](ADR-073-mcp-elicitation-roots-client-capabilities.md).

## ADR-074: issue #2665 (permission modes и session-wide Allow all edits) — одного общего slice нет

Issue #2665 — product-gap из отчётов product-feature-gaps (A01-A04/M17), Orca
(A01-A04) и T3code (B1-B11) с четырьмя пунктами: enum из четырёх режимов,
in-memory `SessionPermissionOverlay`, approval record с `WaitingApproval` и
`DenyWithFeedback`, а также критерий приёмки auto-accept только для write/edit
и `not_resumable` после рестарта. Инвентаризация `origin/dev` (`72308740`),
`gh issue view 2665 --comments` (0), `gh pr list --search 2665` и
`git log --all --grep=2665` показала, что не влит ни один пункт: `CommonConfig.cs:249`
держит режим строкой, `PermissionRuleset` не знает режима, `ApprovalCoordinator`
держит in-memory gates без durable record, `SessionStatus` не знает
`WaitingApproval`.

Decided: код не писать. Четыре пункта не объединяются в meta-PR: у каждого свой
публичный контракт, AOT-профиль и владелец по безопасности. Acceptance про
auto-accept и текстовый отказ меняет `PlainPermissionAsker`,
`CellForgePermissionAsker` и форму `ToolResult`, поэтому это отдельный slice с
потребителем в TUI. Гейты, пороги, golden и AOT-профиль не меняются. Issue
остаётся OPEN как источник сравнения; комментарий содержит только хеш коммита
ADR. Пересмотр — по новому факту: владелец называет конкретный пункт,
потребителя и измеримую цель. Номер 074 выбран после проверки DECISIONS.md и
открытых PR: 073 и 075 уже заняты параллельными ADR (#3002 и #2999).

- Полный текст, таблица по четырём пунктам и условия пересмотра:
  [ADR-074](ADR-074-permission-modes-session-overlay-verdict.md).

## ADR-077: отчёт kilocode-features part 2/2 (#2680) после вердикта

Issue #2680 (180 concrete item F001-F180) уже имеет вердикт ADR-049 на базе
`8db8393f` с тремя влитыми item: F157, F159 и F179. Проверка `origin/dev`
(`abc83115`), комментариев issue и `gh pr view` показывает два slice, влитых
после вердикта: F170, таблица CLI/Avalonia settings parity в
`docs/CLIENT_PARITY.md` (PR #2990, `6efb6df6`), и F115, парсер git branch name
в kebab-case slug `BranchNameGenerator` с тестами (PR #2995, `a3881de7`). Оба
merge commit подтверждены как предки базы.

Decided: вердикт ADR-049 сохраняется, одного общего slice по #2680 нет. Два
новых slice не перевыводятся; F115 остаётся парсером без проводки в продукт,
`IBranchNameGenerator` и `harbor branch suggest` требуют владельца и
потребителя. Остальные 175 item не объединяются в meta-PR: у каждого свой
контракт и критерий приёмки. Гейты, пороги, golden и AOT-профиль не меняются.
Issue остаётся OPEN; комментарий содержит только хеш коммита ADR. Пересмотр по
новому факту: владелец называет конкретный item, потребителя и измеримую цель.
Номер 077 выбран потому, что 074 влит в `dev`, а 075 и 076 заняты открытыми PR.

- Полный текст, таблица F170/F115 и условия пересмотра:
  [ADR-077](ADR-077-kilocode-part2-post-verdict.md).

## ADR-075: issue #2664 (изолированные worktree для subagent) без общего slice

Issue #2664 просит `delegate_task(isolated: true)` через
`WorkspaceMaterializer`, `worktree_status/handoff` с владельцем и запретом
кражи, а также `IWorkspaceLease`, блокирующий rewind. Проверка `origin/dev`
(`56608667`, slice перебазирован на `8f38b89c`) показала: материализатор
detached worktree уже есть в `Harbor.ChangeTracking`, `harbor handoff` пакетит
PR через `IForgeClient`, но инструмента `delegate_task`, владельца worktree и
контракта аренды в дереве нет. PR по issue и комментариев до slice нет.

Decided: код не писать. Три части имеют разные интерфейсы и критерии
приёмки, поэтому общий slice не открывается. Существующий `harbor handoff` не
перегружается семантикой владения, новый guard не вводится (пока нет аренды,
проверка была бы вакуумной). Гейты, пороги, golden и AOT-профиль не меняются.
Issue остаётся OPEN; комментарий содержит только хеш коммита ADR. Пересмотр
по новому факту: владелец называет одну часть, потребителя и измеримую цель.
Номер 075 закреплён за этим slice в открытом PR #2999, пока шла параллельная
нумерация ADR по смежным issue.

- Полный текст, таблица по трём частям и условия пересмотра:
  [ADR-075](ADR-075-isolated-worktrees-subagent-verdict.md).

## ADR-078: отчёт t3code-features part 1/2 (#2673) после вердикта

Issue #2673 (90 concrete item A1-F11) уже имеет вердикт ADR-050 на базе
`82b915c2` с тремя влитыми item: A17, A3 и A9. D1 он прямо отнёс к item с
новым публичным контрактом, требующим отдельного владельца. Проверка
`origin/dev` (`8f38b89c`), комментариев issue и `gh pr view` показывает slice,
влитый после вердикта: D1, immutable `ProviderCapabilities` value с `Intersect`
в `Harbor.Abstractions.Contracts` (PR #2994, merge `72308740`, slice `87458fef`,
test-fix `808d069b`). Merge commit подтверждён как предок базы.

Decided: вердикт ADR-050 сохраняется, одного общего slice по #2673 нет. D1 не
перевыводится; чтение capabilities из `providers/*.json` и проводка в
`AgentLoop` остаются отдельными slice без владельца и потребителя. Остальные
86 item не объединяются в meta-PR: у каждого свой контракт и критерий приёмки.
Гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN;
комментарий содержит только хеш коммита ADR. Пересмотр по новому факту:
владелец называет конкретный item, потребителя и измеримую цель. Номер 078
выбран потому, что 074 влит двумя файлами, 075 влит PR #2999, 076 занят
открытым PR #3004, а 077 влит PR #3005.

- Полный текст, таблица D1 и условия пересмотра:
  [ADR-078](ADR-078-t3code-part1-d1-post-verdict.md).

## ADR-079: issue #1435 (daemon remote access) — одного общего slice нет

Issue #1435 просит реальные кросс-хост прогоны daemon через tailscale и LAN под
`HARBOR_E2E`, а также пользовательские пути Blazor P1-P3 с отсечением того, что
покрывается TUI поверх ssh. Проверка `origin/dev` (`36fcd2a1`), комментария
issue, `gh pr list --search 1435`, `git log --all --grep=1435` и DECISIONS.md
показала один влитый slice: PR #2300 (`eefa0cb9`, merge `77a0382d`) добавил
hermetic-тест non-loopback LAN dial с PSK и RPC. Программ прямого прогона между
хостами в issue нет; P2 покрыт `docs/REMOTE-TAILSCALE.md`; Blazor живёт в
`contrib/` вне сборки и без auth (PLAN.md держит Auth + HTTPS в P0).

Decided: код не писать. Реальные прогоны требуют двух машин и ручного
протокола, P1 требует аутентифицированного Blazor-поверхности в продукте, P3
отложен владельцем, IDE bridge выведен из скоупа. Общий slice не открывается;
гейты, пороги, golden и AOT-профиль не меняются. Issue остаётся OPEN;
комментарий содержит только хеш коммита ADR. Пересмотр по новому факту:
владелец публикует протокол прогона или называет потребителя и критерий приёмки
для P1. Номер 079 выбран потому, что 078 занят открытым PR #3008.

- Полный текст, таблица по частям и условия пересмотра:
  [ADR-079](ADR-079-daemon-remote-access-verdict.md).

## ADR-080: отчёт zed-gpui-engine (#2707) после переоткрытия — ADR-030 в силе

Issue #2707 — self-contained отчёт по движку Zed GPUI с шестью паттернами,
применимыми к CellForge. Проверка `origin/dev` (`1757a343`), полного текста
issue, обоих комментариев владельца и `gh pr list --search 2707 --state all`
показала, что все шесть PR (#2396, #2384, #2389, #2395, #2386, #2394) влиты
2026-11-17, а merge commit `d52f4eda`, `aea253f2`, `e5c17b2a`, `a53f8e39`,
`5fad6d0a`, `666a5aac` подтверждены как предки базы.

Decided: вердикт ADR-030 сохраняется, код не пишется. Переоткрытие issue
18.11 в 15:48 UTC опирается на неверную предпосылку «влит только один slice»:
комментарий владельца учитывает лишь ADR-PR #2790 (`3dd84f3f`) и не видит
шести code-PR предыдущего дня. Остаток отчёта не объединяется в общий
slice; раздел «что честно неприменимо» остаётся отвергнутым. Гейты, пороги,
golden и AOT-профиль не меняются, новый guard не вводится (был бы вакуумным).
Issue остаётся OPEN; комментарий содержит только хеш коммита ADR. Пересмотр по
новому факту: владелец называет конкретный непокрытый механизм GPUI, его
терминальный аналог и измеримую цель — тогда отдельный issue и slice, а не
второе переоткрытие #2707. Номер 080 — следующий свободный после 079.

- Полный текст, таблица по шести паттернам и условия пересмотра:
  [ADR-080](ADR-080-zed-gpui-post-reopen-verdict.md).

## ADR-082: отчёт t3code-features part 2/2 (#2674) после вердикта — влит G1

Issue #2674 — self-contained отчёт со 121 concrete item. ADR-060 зафиксировал
вердикт на базе `5406294e` и отнёс G1 (метрики оркестрации) к item, которым
нужны собственный контракт и владелец. После коммита ADR `12abcef4` в `dev`
влит PR #2977 (`f6ce1d03`, merge `c39a25d6`): четыре счётчика с парными
duration-гистограммами, проводка в drained-процессный seam и one-shot
`harbor ask`. Slice `f6ce1d03` и merge commit `c39a25d6` подтверждены как
предки `origin/dev` (`19a99db2`).

Decided: вердикт ADR-060 сохраняется, одного общего slice по #2674 нет. G1 не
перевыводится: у item влитый PR и файл в дереве. Follow-up G1 по HTTP-эндпоинту
и fire-диспетчеру остаётся внутри item и требует своего владельца. Оставшиеся
item не объединяются в meta-PR. Гейты, пороги, golden, self-test и AOT-профиль
не меняются; новый guard не вводится (был бы вакуумным). Issue остаётся OPEN;
комментарий содержит только хеш коммита ADR. Номер 082 — следующий свободный
после 081.

- Полный текст, таблица по G1 и условия пересмотра:
  [ADR-082](ADR-082-t3code-part2-g1-post-verdict.md).

## ADR-083: отчёт canvas-shape-decision (#2689) после переоткрытия — ADR-027 в силе

Issue #2689 — self-contained отчёт по дизайну Canvas/Shape/Context из ratatui.
ADR-027 зафиксировал «оставить DEFERRED» для #1168; единственное изменение по
отчёту — PR #2782 (`2f55d7ea`), merge commit которого подтверждён как предок
`origin/dev` (`6691dd96`). Владелец закрыл issue и переоткрыл его с
предпосылкой «влит только один slice», но PR #2782 был самим ADR-вердиктом, а
не кодом. Новых PR и новых фактов после переоткрытия нет.

Decided: вердикт ADR-027 сохраняется, код не пишется. Гипотетический срез
`SubcellGrid`/`CanvasPainter`/`Line`/`Points` не открывается без потребителя;
issue #1168 не снимается с DEFERRED. Гейты, пороги, golden, self-test и
AOT-профиль не
меняются, новый guard не вводится (был бы вакуумным). Issue остаётся OPEN;
комментарий содержит только хеш коммита ADR. Номер 083 — следующий свободный
после 082, закреплённого PR #3014.

- Полный текст, условия пересмотра и ссылки:
  [ADR-083](ADR-083-canvas-shape-post-reopen-verdict.md).

## ADR-084: отчёт dx-docs-gaps (#2687) после вердикта — влиты идеи 8, 27, 29 и 34

Issue #2687 — self-contained отчёт `dx-docs-gaps` с 60 идеями. ADR-043
зафиксировал вердикт на базе `8561cd87`, ADR-070 дополнил его на базе
`56608667`, ADR-081 закрыл идею 3 на базе `36fcd2a1`. Проверка `origin/dev`
(`654b1777`) и `gh pr view` показали четыре slice после ADR-081: идея 34
`docs/MIGRATION.md` (PR #3007, slice `4cf82090`, merge `47901ee0`), идея 27
индекс рецептов плагинов (PR #3018, slice `6c3dd7e9`, merge `e6a7e1d2`), идея 29
`docs/DESIGN_FAQ.md` (PR #3020, slice `75c39749`, merge `06f2f0e9`) и идея 8
`examples/README.md` (PR #3023, slice `08a8de53`, merge `8a4aa2a0`). Все merge
commit подтверждены как предки базы. ADR-081 называл идею 34 открытой в PR #3007;
слияние — новый факт.

Decided: вердикты ADR-043, ADR-070 и ADR-081 сохраняются, одного общего slice
по #2687 нет. Четыре влитые идеи не перевыводятся. `docs/CLI_REFERENCE.md` не
открывается без владельца и потребителя, остальные идеи требуют отдельных
issue и PR. Гейты, пороги, golden, self-test и AOT-профиль не меняются; новый
guard не вводится (был бы вакуумным). Issue остаётся OPEN; комментарий содержит
только хеш коммита ADR. Номер 084 — следующий свободный после 083,
закреплённого PR #3015.

- Полный текст, таблица по четырём идеям и условия пересмотра:
  [ADR-084](ADR-084-dx-docs-gaps-four-ideas-post-verdict.md).
