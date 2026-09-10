# 16. Final teardown split (CellForge + Ui.Framework + tests)

> Status: spec. Implements the remainder of #33 (standalone reusable layers)
> and the unfinished tail of #27 (full TEA integration). Written 2026-09-09
> after `feat/improving-cell-forge` (Engine/Chat project split, runner
> decomposition) and `chore/tests-max-perf` (TestKit/E2E expansion).
> Do not start before both branches merge to `dev` — order in §5.

## 0. Terminology

- **Engine** = `src/Harbor.Tui.CellForge.Engine` (input/parsing/capabilities/cell
  primitives; namespaces stay `Harbor.Tui.CellForge.*`).
- **Chat** = `src/Harbor.Tui.CellForge` (widgets/panels/screens only).
- **App** = generic TEA core (no Harbor domain); **Chat-ext** = Harbor adapter.

## 1. Engine → zero-dep Core (#33 Medium)

Engine still references (all must go for a true Core):

| Dep | Used by | Cut strategy |
|---|---|---|
| `Harbor.Abstractions` | `MouseRouter` (`UiMsg`? no — `Abstractions.Models`), `EscapeSequenceParser` | Move shared vocab down: `UiKey`/`ChatAction` already live in `Ui.Framework.Rendering.Input` + `Ui.Framework.State`; route `MouseRouter.WheelToMessage` through `KeyEventMapper` (already the shared contract) instead of constructing `UiMsg` directly |
| `Harbor.Ui.Framework.State` | `MouseRouter` (`UiMsg`, `UiKey`) | Same as above — Engine emits `KeyEvent`, Chat maps to `UiMsg` |
| `Harbor.Ui.Framework.Rendering` | global usings (blocks, markdown) | Split `Engine/Rendering` into `Core.Primitives` (buffer/diff/ANSI/writer — zero-dep) vs chat-owned markdown/prompt rendering (moves to Chat) |
| `Harbor.DesignSystem` | `EscapeSequenceParser` (1 file) | Check what leaks; likely palette constants for OSC — invert via callback or move the file to Chat |

Acceptance:
- [ ] `Harbor.Tui.CellForge.Engine` builds with ZERO `Harbor.*` ProjectReferences (BCL only).
- [ ] Golden-frame tests for Core in isolation (no `Harbor.Abstractions`).
- [ ] `CellForgeGraphRules`: Core referenceable by anyone; Chat restricted to `Harbor.App.Cli` / `Harbor.Hosting`.
- [ ] Arch suite green (`FullLayerMatrixTests` + NetArch rules updated).

## 2. UiState/UiMsg/UiReducer → App + Chat-ext (#33 Medium)

Done already: `{Ui: TerminalUiState, Chat: ChatDomainState}` composition,
`UiMsg.ConfigureRuntime`, dead panel actions in reducer. Remaining renames
(NO namespace renames of public types — arch tests pin them; new types only):

- [ ] `AppState` (generic: Lines/Active/Input/Scroll/Focus/PanelStates) +
      `ChatState` extension (AgentName/IsAgentRunning/Cost/Model/Provider).
- [ ] `AppMsg` (`KeyInput`, `Scroll`, `TogglePanel`, `InputText`) +
      `ChatAppMsg` (`AgentStarted`, `AgentEnded`, `StatusChanged`, `AppendLine`).
- [ ] `AppReducer` (panels/scroll/input/focus) + `ChatAppReducer`
      (`AgentEvent → AppState`). `UiReducer` becomes a thin facade or is deleted.
- [ ] Unit tests: `AppReducer` (no Harbor refs), `ChatAppReducer` (Harbor ext).
- [ ] Rejected (do NOT reopen): `ImmutableDictionary<string,object?>`
      extensions (boxing/AOT); single-`UiMsg` split into two enums is fine,
      single reducer file is not — split by message family.

## 3. #27 tail: store+projector flip

- [ ] Scroll/transcript/painters read from store+projector (today: local
      scroll in timeline, status-only reads). Under goldens — re-bless only
      with reviewed diffs (`ce3-chat-screen`, `ce4-consoleex-repl`, hash manifest).
- [ ] Input/resize/focus fully through `UiMsg`/`UiReducer`; remove
      `ShouldRenderPlacement` stub (verify gone).
- [ ] Re-verify #27 acceptance verbatim: 7 panels from `UiState`, E2E Kilocode
      `agent_start→…→agent_end` intact, 0 warnings, arch tests on ref changes.
- [ ] Palette nav → `PaletteModel<T>` delegation (model exists, zero consumers;
      ranking must stay byte-identical — pin with `CommandPaletteViewTests` first).

## 4. #33 Low/Suggested (only after §1–§2 green)

- [ ] `ChatViewState`/`ChromeViewState`, `StreamingSync`, chat VMs
      (`ToolCall/TokenUsage/StatusMappers`), `Sessions/` → Chat-ext project.
- [ ] `Harbor.Ui.Framework` + Engine packable (`IsPackable>true`), versioned.
- [ ] Theme via `IThemeService` (today: direct wiring — `Watch` swallows
      errors/names; fix the service first, then route).
- [ ] Answer the 3 discussion questions in #33 (SessionId/MessageId
      abstraction, public-NuGet-from-day-one, Chat-ext dependency direction).

## 5. Merge order (do not parallelize)

1. `chore/tests-max-perf` → `dev` (TestKit/E2E; resolves the Sep-6 merge state).
2. `feat/improving-cell-forge` → `dev` (Engine split, runner, bubbles v2, PTY fixes).
3. New `chore/final-teardown` branch from fresh `dev` implements §1→§4 in order.
4. Each step: full `dotnet build Harbor.slnx` (0 errors) + affected suites +
   `Architecture.Tests` on every csproj change + CI green before merge.

## 6. Explicit non-goals

- No namespace renames of existing public types (arch tests pin
  e.g. `Harbor.Tui.CellForge.Input.UnixTermiosModeController`).
- No `Spectre`/contrib changes (out of scope, owner differs).
- No product-behavior changes: mouse-grab vs paste-only is decided in #36;
  auto-title hatch (`HARBOR_NO_AUTOTITLE`) stays.
- No new `.csproj` without updating `FullLayerMatrixTests.AllSrcAssemblies`
  + `CellForgeGraphRules` + `Harbor.slnx` in the same commit.

## 7. Open decisions for @refusedguy

- #36 (mouse grab), #21 (two-process go/no-go for v1.0), #33 Q1–Q3.
- Public NuGet from day one vs internal until API stabilizes (#33 Q2).
