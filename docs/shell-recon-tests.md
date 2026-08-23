# Shell Recon Tests — UI Shell Coverage Report

> Generated: 2026-08-12
> Scope: tests/ directory analysis for UiReducer, UiState, ViewModels, reducers, panels, overlays, projectors, and E2E shell tests.

> **Note:** snapshot generated 2026-08-12, before sprint-2. `Harbor.Tui.{Spectre,Spectre.Fullscreen,SpectreTui,TerminalGui,Termina,RazorConsole,Sixel}` now live in `contrib/tui/`, `Harbor.App.{Wpf,Maui,Blazor}` in `contrib/apps/`, their tests in `contrib/tests/`. Paths below are not updated.

---

## Test projects

| Test project | Purpose |
|---|---|
| `Harbor.Abstractions.Tests` | Unit tests for Harbor.Abstractions (ValueObjects, identifiers, events) |
| `Harbor.Architecture.Tests` | Reflection + NetArchTest layering invariants (Domain → Application → Infrastructure → Presentation) |
| `Harbor.Benchmarks` | BenchmarkDotNet perf suite |
| `Harbor.Config.Tests` | Config-store tests |
| `Harbor.Core.Tests` | Core agent-loop, event-bus, registries, compaction, permissions |
| `Harbor.Plugins.Runtime.Tests` | Roslyn CS-source plugin loader, hosting layer |
| `Harbor.Providers.Tests` | LLM provider config / client tests |
| `Harbor.Scripting.Tests` | Scripting engine / bridge tests |
| `Harbor.Storage.Jsonl.Tests` | JSONL session-store tests |
| `Harbor.Storage.Tests` | Storage-abstraction tests |
| `Harbor.Tools.Builtin.Tests` | 14 builtin tool tests (read/write/edit/bash/glob/grep/ls/task/webfetch/patch/notebook/ripgrep/tree/mcp) |
| `Harbor.Tui.E2E.Tests` | E2E framework helpers (StateTestRunner, StateTestBase) |
| `Harbor.Tui.Tests` | **Primary TUI unit-test project**: UiReducer, UiStore, InputModel, PanelRegistry, SpectreTui renderer, ChatViewProjector, DefaultUiProjector, TEA bridges, diagnostics panel, F12 keymap |
| `Harbor.App.Avalonia.Tests` | Avalonia desktop app: DI, view inflation, killer features, locator convention |
| `Harbor.App.Blazor.Tests` | Blazor app DI tests |
| `Harbor.App.Cli.Tests` | CLI HostBuilder DI surface tests |
| `Harbor.App.Maui.Tests` | MAUI app tests |
| `Harbor.App.Wpf.Tests` | WPF app DI tests |
| `Harbor.E2E.App.Avalonia` | **Avalonia UI E2E**: MainViewModel-driven component tests (settings, command palette, toasts, sidebar, diff, history) |
| `Harbor.E2E.App.Blazor` | Blazor E2E |
| `Harbor.E2E.Cli` | CLI E2E (mock LLM server round-trip) |
| `Harbor.E2E.Framework` | E2E test framework (StateTestRunner, mock LLM server, PTY driver) |
| `Harbor.E2E.Tui.RazorConsole` | RazorConsole E2E |
| `Harbor.E2E.Tui.SpectreTui` | **SpectreTUI PTY E2E**: streaming, tool-call, error, compaction, panels, scroll, input history, autocomplete |
| `Harbor.E2E.Tui.Termina` | Termina E2E |
| `Harbor.E2E.Tui.TerminalGui` | Terminal.Gui E2E |
| `Harbor.Ipc.Tests` | IPC transport tests |

---

## UiReducer tests

**File:** `tests/Harbor.Tui.Tests/SpectreTuiRendererTests.cs` — class `UiReducerTests`

### Covered scenarios
| Scenario | Assertion |
|---|---|
| `AgentStartEvent` seeds user lines only when empty | `Lines.Length == 1`, role == User, text == "hi" |
| Text deltas accumulate then flush on `MessageEndEvent` | `Active.TextBuffer == "Hello world"`, then `Lines[0].Text == "Hello world"`, `IsStreaming == false` |
| Thinking delta flushes to thinking line | `Lines[0].Role == Thinking`, text == "hmm" |
| `StepFinishEvent` accumulates cost | `Cost.TokensIn`, `TokensOut`, `CostUsd` |
| `AgentEndEvent` resets running state | `IsAgentRunning == false`, `IsStreaming == false`, `Status == "idle"` |
| Tool events append tool lines | 2 lines: `ChatRole.Tool` + `ChatRole.ToolResult` |

### Panel-related messages (in `PanelRegistryTests.cs`)
| Scenario | Assertion |
|---|---|
| `TogglePanel` flips Hidden ↔ Visible | `PanelStates["alpha"]` transitions correctly |
| Focused → Hidden clears `FocusedPanelId` | `FocusedPanelId == null` |
| `FocusPanel` sets Focused state | `PanelStates["alpha"] == Focused`, `FocusedPanelId == "alpha"` |
| `CycleFocus` walks visible panels in registration order | Focus wraps p1 → p2 → chat → p1 |
| `ResizePanel` clamps to [MinSize..MaxSize] | Grows, max-clamp, min-clamp, unknown-id no-op |

### Key-related messages
| Scenario | Assertion |
|---|---|
| `ScrollResetToTail` resets scroll + sets `WasRunning` | `ScrollOffset == 0`, `WasRunning == true` |
| `ScrollClamp` clamps `ScrollOffset` to [0..MaxScroll] | Correct clamping for over/under/negative |
| All `ChatAction.Scroll*` actions flow through `Update` | ScrollUpLine, ScrollDownLine, ScrollUpPage, ScrollDownPage, ScrollTop, ScrollBottom update `ScrollOffset` correctly |

### Overlay-related messages
**No overlay-specific UiReducer tests found.**

### Missing
- No tests for `UiMsg.TogglePanel` via the full `UiReducer.Update` dispatch (panel tests call `UiReducer.TogglePanel` static helper directly, not through `Update`).
- No tests for `UiMsg.FocusPanel` via `Update`.
- No tests for `UiMsg.CyclePanelsFocus` via `Update`.
- No tests for `UiMsg.ResizePanel` via `Update`.
- No tests for overlay messages (e.g., `ShowOverlay`, `HideOverlay`, `OverlayResult`).

---

## UiState tests

**Files:**
- `tests/Harbor.Tui.Tests/PanelRegistryTests.cs` — `TeaComplianceTests`
- `tests/Harbor.E2E.Framework/StateTestRunner.cs` — factory helpers
- `tests/Harbor.E2E.Framework/StateTestBase.cs` — base class

### Covered scenarios
| Scenario | Assertion |
|---|---|
| `UiState` has `ScrollOffset`, `ViewportLines`, `TotalLines`, `WasRunning`, `IsAgentRunning` properties | Reflection-based existence checks |
| `UiState` has `PanelStates`, `PanelSizes`, `FocusedPanelId`, `RegisteredPanelIds` | Used implicitly in panel tests |
| E2E framework builds `UiState` snapshots for all states | `StreamingState`, `ThinkingState`, `ToolCallState`, `ToolResultState`, `ErrorState`, `CompactionState`, `AgentRunningState`, `AgentIdleState`, `PanelFocusedState`, `ScrolledState`, `HistoryNavigatedState`, `SlashAutocompleteState`, `UserMessageState`, `AssistantMessageState` |

### Missing
- No direct unit tests for `UiState` immutability / `with` expressions.
- No tests for `UiState.Cost`, `UiState.Status`, `UiState.Model`/`Provider`/`AgentName` setters.
- No tests for `UiState.Active` (streaming buffer) transitions.

---

## ContentHost tests

**No tests found.** `ContentHost` / `IContentHost` does not appear in any test file under `tests/`.

---

## MainViewModel tests

**Files:**
- `tests/Harbor.E2E.App.Avalonia/AvaloniaUiTests.cs` — extensive E2E component tests
- `tests/Harbor.App.Avalonia.Tests/AppHostDiTests.cs` — DI registration
- `tests/Harbor.App.Wpf.Tests/AppDiTests.cs` — DI registration
- `tests/Harbor.App.Avalonia.Tests/KillerFeatureTests.cs` — killer features

### Covered scenarios (E2E)
| Scenario | Assertion |
|---|---|
| `ActiveView` changes | Asserts view switches |
| `OpenDiffCommand` | Opens diff viewer modal |
| `IsSettingsOpen` | Settings dialog opens |
| `AddToast` | Toast appears |
| `IsSidebarVisible` | Sidebar toggles |
| `SwitchViewCommand` | View switching |
| Input history navigation | `InputText` reflects history |
| `IsCommandPaletteOpen` | Command palette visibility |

### Missing
- No pure unit tests for `MainViewModel` logic (all tests are E2E or DI smoke tests).
- No tests for `MainViewModel` event handling, store subscription, or property change propagation in isolation.

---

## StoreSubscriberViewModel tests

**No tests found.** `StoreSubscriberViewModel` does not appear in any test file.

---

## OverlayController tests

**No tests found.** `OverlayController` / `IOverlayController` does not appear in any test file.

---

## PanelRegistry tests

**File:** `tests/Harbor.Tui.Tests/PanelRegistryTests.cs`

### Covered scenarios
| Scenario | Assertion |
|---|---|
| `Register` adds panel and makes it retrievable | `All.Count == 1`, `Get("alpha") != null` |
| Registration order preserved | `All[0..2].Id` matches insertion order |
| Default placement preserved | `DefaultPlacement` per panel |
| Duplicate id replaces in place | Count stays same, title/size updated |
| Empty id returns failure | `IsFailure`, `All.Count == 0` |
| `PanelRegistryView.GetVisibleByPlacement` filters by state + placement | Hidden excluded, registration order preserved |
| `PanelRegistryView` is snapshot | Subsequent state mutations don't affect captured view |
| `GetState` / `GetSize` read from `UiState`, not registry | `view.GetState("left-2") == Focused`, `view.GetSize("left-1") == 0` |

### TEA compliance tests (same file)
| Scenario | Assertion |
|---|---|
| `IPanelRegistry` exposes only registration methods | No `SetState`, `SetSize`, `GetState`, `GetSize`, `ApplySnapshot`, etc. |
| `PanelRegistry` has no state mutation methods | Reflection checks for banned method names |
| `UiState` has scroll/viewport fields | `ScrollOffset`, `ViewportLines`, `TotalLines`, `WasRunning`, `IsAgentRunning` |
| `UiMsg` has scroll messages | `ScrollResetToTail`, `ScrollClamp`, `Viewport`, `HistoryMeasured` |
| `ChatScreen` has no local scroll fields | No `_scroll`, `_wasRunning` fields |
| `ChatScreen` has no `HandleLocalScroll` method | Reflection null check |
| `ChatScreen` has no `SyncRegistryFromState` method | Reflection null check |
| `SpectreTuiRenderer` has no `SyncPanelRegistryToState` | Replaced by `SeedPanelRegistryIntoState` |

### Missing
- No tests for `Unregister`.
- No tests for concurrent registration / thread-safety (despite `PanelRegistry` being documented as thread-safe).

---

## Architecture tests

**Files:**
- `tests/Harbor.Architecture.Tests/LayerDependencyTests.cs`
- `tests/Harbor.Architecture.Tests/NetArchLayerRules.cs`
- `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs`

### Relevant rules
| Rule | What it enforces |
|---|---|
| `Abstractions_HasNoHarborProjectReferences` | `Harbor.Abstractions` (Domain) references ZERO other Harbor assemblies |
| `TuiAbstractions_ReferencesOnlyAbstractions` | `Harbor.Terminal.Abstractions` may reference `Harbor.Abstractions` only |
| `Application_ReferencesOnlyAbstractions` | `Harbor.Application` may reference `Harbor.Abstractions` only |
| `Registries_ReferencesOnlyAbstractions` | `Harbor.Registries` may reference `Harbor.Abstractions` only |
| `Core_ReferencesOnlyApplicationAndRegistriesAndAbstractions` | `Harbor.Core` facade must not reach Presentation or Infrastructure |
| `PluginsRuntime_ReferencesOnlyAbstractions` | `Harbor.Plugins.Runtime` may reference `Harbor.Abstractions` + `Harbor.Terminal.Abstractions` |
| `Scripting_ReferencesOnlyAbstractions` | `Harbor.Scripting` may reference `Harbor.Abstractions` only |
| `Providers_ReferencesOnlyAbstractions` | Each provider assembly references `Harbor.Abstractions` only |
| `Storage_ReferencesOnlyAbstractions` | Each storage assembly references `Harbor.Abstractions` only |
| `ToolsBuiltin_ReferencesOnlyAbstractions` | `Harbor.Tools.Builtin` references `Harbor.Abstractions` only |
| `TuiRenderers_ReferencesOnlyAbstractionsAndTuiAbstractions` | Each TUI renderer may reference `Harbor.Abstractions` + `Harbor.Terminal.Abstractions` only |
| `Domain_HasZeroHarborProjectReferences` | `Harbor.Domain` pure domain layer — zero Harbor refs |
| `Extensions_ReferencesOnlyDomain` | `Harbor.Extensions` may reference `Harbor.Domain` only |
| `Abstractions_ReferencesOnlyDomainAndExtensions` | `Harbor.Abstractions` facade may reference `Harbor.Domain` + `Harbor.Extensions` only |

### Missing
- No architecture tests specifically for `MainViewModel` or `ViewModelLocator` layering (Avalonia/WPF/MAUI are Presentation layer but not probed).
- No tests forbidding `Harbor.Tui.SpectreTui` from referencing `Harbor.Core` or `Harbor.Application` directly (covered by generic TuiRenderers rule, but not explicitly named).

---

## KeyboardShortcutService tests

**No tests found.** `KeyboardShortcutService` / `IKeyboardShortcutService` does not appear in any test file.

---

## CommandPaletteViewModel tests

**Files:**
- `tests/Harbor.E2E.App.Avalonia/ComponentTests/CommandPaletteTests.cs` — full E2E component tests
- `tests/Harbor.App.Wpf.Tests/AppDiTests.cs` — DI registration
- `tests/Harbor.App.Blazor.Tests/ProgramDiTests.cs` — DI registration
- `tests/Harbor.App.Avalonia.Tests/ViewInflationTests.cs` — view inflation

### Covered scenarios (E2E)
| Scenario | Assertion |
|---|---|
| Open shows search input + all commands | `Results.Count > 5` |
| Search filters results | "New session" visible, "Switch to chat" not visible |
| Arrow-down moves selection | `SelectedIndex` 0 → 1 |
| Enter executes selected command | `ActiveView == "code"` after invoking "Switch to code" |
| Esc / closed hides palette | `IsCommandPaletteOpen == false` |
| No matches shows empty list | `Results.Count == 0` |

### Missing
- No pure unit tests for `CommandPaletteViewModel` filtering logic, command registration, or keyboard handling in isolation.

---

## TUI projector tests

**Files:**
- `tests/Harbor.Tui.Tests/DefaultUiProjectorTests.cs`
- `tests/Harbor.Tui.Tests/SpectreTuiRendererTests.cs` — `SpectreTuiChatViewProjectorTests`
- `tests/Harbor.Tui.Tests/TeaBridgeTests.cs` — bridge projections

### Covered scenarios
| Projector | Scenario | Assertion |
|---|---|---|
| `DefaultUiProjector` | Boot state header | `Model`, `Provider`, `AgentName` empty; `IsAgentRunning == false` |
| `DefaultUiProjector` | Boot state transcript | `Blocks` empty, `StreamingBlockId == null` |
| `DefaultUiProjector` | FocusMode.Input | `screen.Focus == FocusMode.Input` |
| `DefaultUiProjector` | FocusMode.Panel | `screen.Focus == FocusMode.Panel` |
| `DefaultUiProjector` | Streaming state | `StreamingBlockId != null`, streaming block present |
| `DefaultUiProjector` | Tool call + result | 2 blocks: `ChatRole.Tool` + `ChatRole.ToolResult` |
| `DefaultUiProjector` | Status bar | Segment contains provider text |
| `DefaultUiProjector` | Input enabled/disabled | `IsEnabled` true when idle, false when running |
| `DefaultUiProjector` | State revision | `StateRevision` non-empty |
| `DefaultUiProjector` | User message span | `Role == User`, `Spans.Count == 1` |
| `ChatViewProjector` | Unbalanced brackets | No throw on `[` / `]` in markdown |
| `ChatViewProjector` | Streaming appends active buffers | 5 widgets when streaming |
| `ChatViewProjector` | History slices by display rows | `TotalLines - HistoryTopRow <= viewport` |
| `ChatViewProjector` | Scroll offset is display rows from bottom | `HistoryTopRow` shifts correctly |
| `ChatViewProjector` | Stream only appended when pinned | `TotalLines` differs between pinned/scrolled |
| `TerminaTeaBridge` | Constructs + dispatches text delta | `Lines[0].Text == "Hello"` |
| `TerminaTeaBridge` | Key handler dispatches chars | `Input.Text == "hi"` |
| `TerminalGuiTeaBridge` | Constructs + dispatches text delta | `Lines[0].Text == "Test"` |
| `TerminalGuiTeaBridge` | StatusBarView projects state | Contains model/provider/agent |
| `RazorConsoleTeaBridge` | Constructs + dispatches text delta | `Lines[0].Text == "Razor"` |
| `RazorConsoleTeaBridge` | ChatView projects transcript with markup | Contains `[white]` markup |
| `RazorConsoleTeaBridge` | Toast queue roundtrips | Enqueue + dequeue order |

### Missing
- No tests for `ChatViewProjector` with panel slots (`BuildWidgets` with panels).
- No tests for `ChatViewProjector` with overlay slots.
- No tests for `DefaultUiProjector` projecting panel state.

---

## E2E tests

**Files:**
- `tests/Harbor.E2E.Tui.SpectreTui/SpectreTuiE2ETests.cs`
- `tests/Harbor.E2E.App.Avalonia/AvaloniaUiTests.cs`
- `tests/Harbor.E2E.Cli/CliE2ETests.cs`
- `tests/Harbor.E2E.Framework/` — framework base classes

### SpectreTUI E2E (shell-related)
| Test | What it covers |
|---|---|
| `Start_ShowsWelcomeBanner` | Boot, welcome banner, `/exit` |
| `SlashHelp_ShowsCommandList` | `/help` renders command list |
| `CtrlC_AbortsTui` | Ctrl-C exits |
| `F12_TogglesLogsPanel` | Logs panel toggle |
| `QuestionMark_TogglesHelpPanel` | Help panel toggle (`?`) |
| `TypedText_IsEchoedToScreen` | Input echo |
| `Screenshot_CapturesCoreStates` | PNG capture of boot/help/logs/input |
| `Streaming_ShowsResponse` | Mock LLM streaming |
| `ToolCall_RendersToolCard` | Tool-call card rendering |
| `ErrorState_ShowsError` | Error message rendering |
| `Compaction_ShowsCompactionStatus` | Compaction status pill |
| `AgentRunning_ShowsRunningBanner` | Running status banner |
| `Alt1_TogglesPanel` | Panel toggle via Alt+1 |
| `CtrlTab_CyclesPanelFocus` | Panel focus cycling |
| `ScrollUp_ScrollsHistory` | PageUp scroll |
| `AltUp_NavigatesInputHistory` | Alt+Up history |
| `Tab_AutocompleteSlashCommand` | Tab autocomplete |

### Avalonia E2E (shell-related)
| Test | What it covers |
|---|---|
| `CommandPalette_OpensWithCtrlP` | Ctrl+P opens palette |
| `CommandPalette_Search_FiltersResults` | Search filtering |
| `CommandPalette_ArrowDown_MovesSelection` | Arrow navigation |
| `CommandPalette_Enter_ExecutesSelected` | Enter executes |
| `CommandPalette_Closed_NotVisible` | Esc closes |
| `CommandPalette_NoMatches_EmptyResultsList` | Empty results |
| Settings dialog opens | `IsSettingsOpen` |
| Toast via `AddToast` | Toast visibility |
| Sidebar visibility | `IsSidebarVisible` |
| Diff viewer modal | `OpenDiffCommand` |
| Input history | Previous command navigation |

### CLI E2E
| Test | What it covers |
|---|---|
| Mock LLM round-trip | HostBuilder → AgentLoop → OpenAiCompatibleLlmClient → MockLlmServer |

### Missing
- No E2E tests for overlay open/close/interaction in SpectreTUI.
- No E2E tests for panel resize via mouse/keys in SpectreTUI.
- No E2E tests for `ChatViewProjector` rendering with panels in SpectreTUI.
- No E2E tests for WPF/MAUI/Blazor shell (only Avalonia and SpectreTUI have shell E2E).

---

## Coverage gaps

| Component | What should be tested but isn't |
|---|---|
| `UiReducer` | Overlay messages (`ShowOverlay`, `HideOverlay`, `OverlayResult`) via `Update` dispatch |
| `UiReducer` | Panel messages (`TogglePanel`, `FocusPanel`, `CyclePanelsFocus`, `ResizePanel`) via full `Update` dispatch (not just static helpers) |
| `UiState` | Immutability / `with` expression correctness for nested collections |
| `UiState` | `Cost`, `Status`, `Model`/`Provider`/`AgentName` transitions |
| `ContentHost` / `IContentHost` | **Zero tests** — entire interface untested |
| `MainViewModel` | Pure unit tests for property logic, store subscription, command invalidation |
| `StoreSubscriberViewModel` | **Zero tests** — base class untested |
| `OverlayController` / `IOverlayController` | **Zero tests** — overlay stack, open/close/result routing untested |
| `KeyboardShortcutService` / `IKeyboardShortcutService` | **Zero tests** — key mapping, chord detection, conflict resolution untested |
| `PanelRegistry` | `Unregister` behavior |
| `PanelRegistry` | Concurrent registration thread-safety |
| `CommandPaletteViewModel` | Pure unit tests for filtering, command registration, keyboard handling |
| `ChatViewProjector` | Rendering with panel slots and overlay slots |
| `DefaultUiProjector` | Projecting panel state |
| `SpectreTUI E2E` | Overlay open/close/interaction |
| `SpectreTUI E2E` | Panel resize via keys |
| `SpectreTUI E2E` | `ChatViewProjector` rendering with panels |
| `Architecture` | Explicit `MainViewModel` / `ViewModelLocator` layering rules (Avalonia/WPF/MAUI Presentation layer) |
| `Architecture` | Explicit `Harbor.Tui.SpectreTui` dependency rule (currently covered by generic TuiRenderers rule) |

---

## Summary

- **UiReducer**: Well-covered for event-reduction and scroll/panel static helpers. **Gap: overlay messages, full dispatch path for panel messages.**
- **UiState**: Covered via E2E framework factories and TEA compliance reflection tests. **Gap: no direct unit tests for state transitions.**
- **ContentHost**: **Zero tests.**
- **MainViewModel**: Covered via Avalonia E2E + DI smoke tests. **Gap: no isolated unit tests.**
- **StoreSubscriberViewModel**: **Zero tests.**
- **OverlayController**: **Zero tests.**
- **PanelRegistry**: Well-covered for registration, view, TEA compliance. **Gap: Unregister, concurrency.**
- **Architecture**: Strong reflection + NetArchTest coverage for Clean/Hexagonal layers. **Gap: no Presentation-layer (Avalonia/WPF) ViewModel locator rules.**
- **KeyboardShortcutService**: **Zero tests.**
- **CommandPaletteViewModel**: Covered via Avalonia E2E + DI. **Gap: no isolated unit tests.**
- **TUI projectors**: Good coverage for `DefaultUiProjector` and `ChatViewProjector` core. **Gap: panel/overlay slots.**
- **E2E**: SpectreTUI and Avalonia have solid shell E2E. **Gap: overlay interaction, panel resize, WPF/MAUI/Blazor shell E2E.**
