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
checks в ruleset. Из 9 691 строки в 17 workflow-файлах невыразимое в C# — блоки
`on:`/`permissions:`/`runs-on:` и заголовки джобов (931 строка, 9,6 %) плюс
`needs:` (19 строк); шелл-код в `run:`-блоках — 2 728 строк (28,1 %) — это то, что
имеет смысл унести в C#, но в объём переезда оно не входит. Генерация
workflow-файлов средствами Fallout реальна (`[GitHubActions]` пишет `on:`,
`permissions:`, `runs-on:`, шаги), но `needs:` он не пишет, поэтому вопрос вынесен в
отдельную задачу.

- Эпик переезда: [#2178](https://github.com/refusedguy/Harbor-Harness/issues/2178);
  подзадачи по шагам: #2179 (пилот), #2180 (`build/_build/`), #2181 (CI и
  bootstrap), #2182 (документация), #2183 (удаление NUKE), #2184 (гейт).
- Полный текст, план по шагам, смета в числах, риск-реестр и граница YAML:
  [ADR-023](ADR-023-build-orchestrator-moves-to-fallout.md) и
  [BUILDSYSTEM_STUDY.md](../BUILDSYSTEM_STUDY.md).
