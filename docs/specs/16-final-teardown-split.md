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
| ~~`Harbor.Abstractions`~~ **done in #435** | **nothing** — the `Used by` guess below was wrong | Measured: zero `using Harbor.Abstractions*` in the project and zero types bound from it; the only two mentions of the name in the directory were the `ProjectReference` itself and a README row. A non-empty csproj over a zero real dependency (the #980 shape). The edge was only *legal* because `Harbor.Ui.Framework.State` dragged `Abstractions` into the layer matrix's expansion, so the two removals could not be split. |
| ~~`Harbor.Ui.Framework.State`~~ **done in #435** | `MouseRouter` (`AppMsg`, `ChatAction`, `UiKey`, `UiKeyCode`), `ComposerController` (`EnterKeyPolicy`, `ChatAction`, `InputMsg`) | Ported onto the BCL-only vocabulary rather than relocated: `MouseRouter.WheelToMessage` became `WheelToKey` returning `UiKeyDto` (#33/T1's type), and `ComposerController` asks `EnterPolicy`/`EnterDecision`, a new BCL-only mirror of the Enter decision that `State.EnterKeyPolicy` now delegates to — so #359's single source of truth survives instead of being duplicated. `InputMsg.HistoryUp/HistoryDown` turned out to be empty marker records and became a local direction enum. `KeyEventAdapter` remains the single Rendering→State crossing point. |
| `Harbor.Ui.Framework.Rendering` | 24 files, 15 types (cells, keys, markdown, widgets) | Split `Engine/Rendering` into `Core.Primitives` (buffer/diff/ANSI/writer — zero-dep) vs chat-owned markdown/prompt rendering (moves to Chat). Also now carries the two BCL-only vocabularies above, which is why #435 could remove State's edge while this one stayed. #436's decision. |
| `Harbor.DesignSystem` | `EscapeSequenceParser` (1 file, 1 call site) | Check what leaks; likely palette constants for OSC — invert via callback or move the file to Chat. NOTE measured for #435 and NOT acted on: the 5 types bound from this assembly (`CellStyle`, `PackedColor`, `StyleAttr`, `ChatPalette`, `TerminalBackgroundProbe`) are all reachable transitively through the `Harbor.Ui.Framework.Rendering` edge, which references DesignSystem — so the direct edge may be redundant rather than load-bearing. But 4 of the 5 are declared in `Harbor.DesignSystem` while NAMED `Harbor.Ui.Framework.*`, so a guard keyed on either namespace prefix alone is blind to them. Verify by deleting it, not by reading this paragraph. |

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

- [ ] ~~`ChatViewState`/`ChromeViewState`~~, `StreamingSync`, chat VMs
      (`ToolCall/TokenUsage/StatusMappers`), `Sessions/` → Chat-ext project.
      (The two records were deleted as producer-less in #597; `SessionInfo`
      stays — it is live, held by `ChatDomainState.Sessions`.)
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
- No product-behavior changes: mouse-grab vs paste-only decided in #36
  (full grab — scroll/clicks inside Harbor, Shift/palette for clipboard);
  auto-title hatch (`HARBOR_NO_AUTOTITLE`) stays.
- No new `.csproj` without updating `FullLayerMatrixTests.AllSrcAssemblies`
  + `CellForgeGraphRules` + `Harbor.slnx` in the same commit.

## 7. Open decisions for @refusedguy

- #36 decided (full mouse grab), #21 (two-process go/no-go for v1.0), #33 Q1–Q3.
- Public NuGet from day one vs internal until API stabilizes (#33 Q2).
