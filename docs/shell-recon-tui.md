# TUI Shell Architecture Reconnaissance — Spectre.Console Renderer

## 1. UiState properties

| Property | Type | Purpose |
|----------|------|---------|
| `Lines` | `ImmutableArray<ChatLine>` | Full transcript history, oldest first. Append-only. |
| `Active` | `ActiveMessage` | Live streaming message buffers (`TextBuffer` + `ThinkBuffer`). |
| `IsStreaming` | `bool` | Whether a message is actively streaming. |
| `Status` | `string` | Human-readable status: `idle` / `running` / `compacting` / `error`. |
| `Cost` | `CostSnapshot` | Cumulative token/cost accounting (`TokensIn`, `TokensOut`, `CostUsd`). |
| `Model` | `string` | Active model id (header/status). |
| `Provider` | `string` | Active provider id (header/status). |
| `AgentName` | `string` | Active agent name (header/status). |
| `IsAgentRunning` | `bool` | Whether the agent is currently running a prompt. |
| `WasRunning` | `bool` | Snapshot of `IsAgentRunning` from previous agent event (rising-edge detection). |
| `ShouldQuit` | `bool` | User requested quit. |
| `Input` | `InputModel` | Editable prompt state (text + history navigation). |
| `Focus` | `FocusMode` | Which region owns keyboard: `Input`, `Chat`, or `Panel`. |
| `ScrollOffset` | `int` | History scroll-back offset (0 = pinned to newest). |
| `ViewportLines` | `int` | Number of history rows currently visible (reported by renderer). |
| `TotalLines` | `int` | Total number of wrapped history rows (reported by renderer). |
| `ScrollPercent` | `int` (computed) | How far scrolled as percentage (0 = bottom, 100 = top). |
| `PanelStates` | `ImmutableDictionary<string, TuiPanelState>` | Per-panel runtime visibility state. |
| `PanelSizes` | `ImmutableDictionary<string, int>` | Per-panel size override (rows/cols, 0 = provider default). |
| `FocusedPanelId` | `string?` | Id of panel currently owning keyboard focus, or `null` for chat/input. |
| `RegisteredPanelIds` | `ImmutableArray<string>` | Registered panel ids in registration order. |

**Mutating methods on `UiState`:**
- `AddLine(ChatRole, string, string?)` — append transcript line.
- `SetLine(int, ChatRole, string)` — replace line at index.
- `SetInput(InputModel)` — replace input model.
- `SetFocus(FocusMode)` — replace focus mode.
- `ClearTranscript()` — clear transcript/scroll/input, preserve chrome.
- `SetScroll(int)` — clamp and set scroll offset.

---

## 2. Panel-related state

### PanelStates
`ImmutableDictionary<string, TuiPanelState>` keyed by panel id. Values are the `TuiPanelState` enum:
- `Hidden` — not rendered.
- `Visible` — rendered but no focus.
- `Focused` — rendered AND owns keyboard focus.
- `Pinned` — rendered, kept open when cycling focus away.

### PanelSizes
`ImmutableDictionary<string, int>` keyed by panel id. Value is rows (Top/Bottom) or columns (Left/Right). `0` means "use provider's `DefaultSize`".

### FocusedPanelId
`string?` — the panel id currently owning keyboard focus. `null` means chat/input owns focus. Driven by `FocusPanel` / `CyclePanelFocus` messages.

### RegisteredPanelIds
`ImmutableArray<string>` — panel ids in registration order. Maintained by `PanelRegistry` via `UiStore.Transition` / `SeedPanels`. Used by `CycleFocus` so the reducer stays pure (no `IPanelRegistry` dependency).

---

## 3. UiMsg types

| DU Case | Category | Description |
|---------|----------|-------------|
| `Agent(AgentEvent)` | agent | Wrap an agent event into the UI pipeline. |
| `KeyInput(ChatAction, UiKey)` | key | Resolved UI action with originating key. |
| `Viewport(int)` | viewport | Renderer reports visible history height. |
| `HistoryMeasured(int)` | viewport | Renderer reports total wrapped transcript rows. |
| `TogglePanel(string)` | panel | Toggle panel Hidden ↔ Visible. |
| `FocusPanel(string?)` | panel | Set focus to panel, or `null` for chat. |
| `CyclePanelFocus` | panel | Cycle focus to next visible panel; last → chat. |
| `ResizePanel(string, int)` | panel | Grow/shrink panel by delta rows/cols. |
| `ScrollResetToTail` | viewport | Reset scroll offset to 0 (pin to live tail). |
| `ScrollClamp(int)` | viewport | Clamp scroll offset to max. |
| `SeedPanels(ImmutableArray<string>, ImmutableDictionary<string,TuiPanelState>, ImmutableDictionary<string,int>)` | panel | Host-side seeding of registered panels at startup. |

**Categories:**
- **Panel-related:** `TogglePanel`, `FocusPanel`, `CyclePanelFocus`, `ResizePanel`, `SeedPanels`
- **Key-related:** `KeyInput`
- **Agent-related:** `Agent`
- **Viewport-related:** `Viewport`, `HistoryMeasured`, `ScrollResetToTail`, `ScrollClamp`

---

## 4. UiReducer panel handlers

### `TogglePanel(UiState state, string id)`
- **Signature:** `public static UiState TogglePanel(UiState state, string id)`
- **Behavior:** If id is empty or unknown, return state unchanged. Toggle between `Hidden` and `Visible`. If toggling to `Hidden` and the panel was focused, clear `FocusedPanelId`.
- **Invariants:** Panel id must exist in `PanelStates`.

### `FocusPanel(UiState state, string? id)`
- **Signature:** `public static UiState FocusPanel(UiState state, string? id)`
- **Behavior:** If `id` is `null`, demote any previously focused panel back to `Visible` and clear `FocusedPanelId`. If `id` is provided, demote previous focus, ensure target is at least `Visible`, set to `Focused`.
- **Invariants:** Panel id must exist in `PanelStates`.

### `CycleFocus(UiState state)`
- **Signature:** `public static UiState CycleFocus(UiState state)`
- **Behavior:** Get visible panels in registration order. If none visible, return focus to chat if not already there. Otherwise, advance focus to next visible panel; if last panel was focused, wrap to chat (`null`).
- **Invariants:** Uses `RegisteredPanelIds` for order; filters by `PanelStates` != `Hidden`.

### `ResizePanel(UiState state, string id, int delta)`
- **Signature:** `public static UiState ResizePanel(UiState state, string id, int delta)`
- **Behavior:** Clamp new size to `[PanelRegistry.MinSize (2) .. PanelRegistry.MaxSize (200)]`. `0` means use provider default.
- **Invariants:** Panel must exist; delta can be positive or negative.

### `SeedPanels` (in `Update`)
- **Signature:** handled in `UiReducer.Update` as `UiMsg.SeedPanels`
- **Behavior:** Replace `RegisteredPanelIds`, `PanelStates`, and `PanelSizes` with the supplied snapshots. Used at startup and on plugin reload.

---

## 5. TuiEffect types

| Effect | Trigger | What it does |
|--------|---------|--------------|
| `None` | No-op | Identity effect. |
| `PromptAgent(string Text)` | Submit input | Run user prompt through agent. |
| `RunSlash(string Command)` | Slash command | Invoke slash command handler. |
| `AbortAgent` | Abort / Ctrl+C | Cancel running agent. |
| `QuitApp` | Quit / exit words | Leave interactive loop. |

**Missing effects (gap):**
- No `OpenOverlay(string id)` / `CloseOverlay()` effect for modals/palettes.
- No `ShowShell(string mode)` effect for shell-mode switching (e.g., right-drawer tabs).
- No `NavigateToSession(SessionId)` effect for session rail clicks.
- No `ResizePanelGrow/Shrink` effect (resize is handled purely in reducer via `UiMsg.ResizePanel`, which is fine).

---

## 6. UiStore API

### Dispatch(AgentEvent)
```csharp
public void Dispatch(AgentEvent @event)
```
- Applies `UiReducer.Reduce` through a **lock-free CAS loop** on `volatile UiState _state`.
- Short-circuits if state is unchanged (`ReferenceEquals`).
- Raises `Changed` event on success.

### Dispatch(UiMsg)
```csharp
public TuiEffect Dispatch(UiMsg msg)
```
- Applies `UiReducer.Update` through the same **CAS loop**.
- Returns the `TuiEffect` for the host to run.
- Raises `Changed` event on success.

### Transition(Func<UiState, UiState>)
```csharp
internal void Transition(Func<UiState, UiState> reducer)
```
- Internal escape hatch used by `TuiEffectHost` to fold follow-up state.
- Same CAS loop pattern.
- Marked `internal` so external renderers cannot bypass `Dispatch(UiMsg)` (§FP-007 acknowledged).

### Changed event
```csharp
public event EventHandler<UiStateChangedEventArgs>? Changed;
```
- Raised after every successful state transition with the new immutable snapshot.

### State storage
- `private volatile UiState _state;`
- Lock-free `Interlocked.CompareExchange` CAS retry loop.
- Replaced previous `lock(_gate)` bottleneck (§PERF-007 resolved).

---

## 7. PanelRegistry API

### Registration
```csharp
public Result Register(IPanelProvider panel)
```
- Thread-safe (`lock(_gate)`).
- Replaces in-place if id already exists (preserves registration order / hotkey slot).
- Adds to `_ordered` list and `_providers` dictionary.

### Unregistration
```csharp
public Result Unregister(string id)
```
- Thread-safe. Removes from both collections.

### Queries
```csharp
public IReadOnlyList<IPanelProvider> All { get; }
public IPanelProvider? Get(string id)
public PanelRegistryView View(UiState state)
```

### PanelRegistryView
Read-only snapshot over providers + `UiState`. Methods:
- `GetVisible()` — providers not `Hidden`.
- `GetVisibleByPlacement(TuiPanelPlacement)` — visible providers filtered by placement.
- `GetState(string id)` — current `TuiPanelState` (defaults to `Hidden`).
- `GetSize(string id)` — current size override (0 = provider default).

### IPanelProvider contract
```csharp
public interface IPanelProvider
{
    string Id { get; }
    string Title { get; }
    TuiPanelPlacement DefaultPlacement { get; }
    int DefaultSize { get; }
    object? Build(PanelContext ctx);
    bool OnKey(UiKey key, PanelContext ctx);
}
```
- `Build` is side-effect free, called every visible frame.
- `OnKey` may dispatch state transitions via `UiStore.Dispatch` (via `ctx.Services`), must NOT mutate `UiState` directly.
- Thread safety: `Build` called from render thread, `OnKey` from input thread concurrently.

---

## 8. TUI projectors

### ChatViewProjector (SpectreTui)
- **Purpose:** Facade that wires chrome + history + layout shell. Orchestration only (~100 lines).
- **Properties exposed:** `Status`, `Model`, `Provider`, `Agent`, `IsReadingInput`, `IsStreaming`, `TokensIn`, `TokensOut`, `Cost`, `InputText`, `Focus`, `FooterText`, `StreamBuffer`, `ThinkBuffer`, `ScrollOffset`, `SourceCount`, `TotalLines`, `ViewportLines`, `EffectiveScroll`, `MaxScroll`, `HistoryTopRow`, `Layout`.
- **Widgets built:** `Header`, `History`, `Input`, `Footer`, plus optional `StreamBar` when streaming.
- **State source:** Receives state via `SpectreUiViewport.Apply(UiScreenModel)` which projects `UiState` → `UiScreenModel` using `DefaultUiProjector`, then copies fields into the projector's mutable properties. History is synced via `SetLines(ImmutableArray<ChatLine>, ...)`.

### DefaultUiProjector
- **Purpose:** Projects `UiState` into `UiScreenModel` (renderer-agnostic).
- **Outputs:**
  - `UiHeaderModel` — model, provider, agent, running/streaming flags, cost, footer text.
  - `UiTranscriptModel` — blocks + rendered lines + streaming block id.
  - `UiStatusBarModel` — segments: provider/model, status glyph, agent name, tokens, cost, scroll %.
  - `UiInputModel` — text, caret, enabled state, placeholder.
- **How it maps state:** Reads `UiState` directly (pure function). `ProjectTranscript` iterates `Lines`, builds `UiBlock` + `UiRenderedLine` per line, appends streaming blocks if `IsStreaming`. `ProjectStatusBar` builds left/center/right aligned segments. `ProjectFooter` joins status bar segments into a single footer string.

### SpectreUiViewport
- **Purpose:** Adapter between `DefaultUiProjector` and `ChatViewProjector`.
- **Behavior:** Calls `DefaultUiProjector.Project(state)`, then copies `UiScreenModel` fields into `ChatViewProjector`'s mutable properties. Extracts lines from rendered spans.

---

## 9. TUI keyboard handling

### Key flow
1. Spectre.TUI `KeyMessage` arrives in `ChatScreen.OnMessage`.
2. `ToUiKey(key)` translates Spectre `Key` + `KeyModifiers` → `UiKey` (code + modifier set).
3. `ChatKeyMap.Resolve(uiKey)` maps `UiKey` → `ChatAction`.
4. Special cases handled before map:
   - `Ctrl+L` → `ChatAction.Clear`
   - `Ctrl+C` → `ChatAction.Abort`
   - `?` → `ChatAction.HelpPanel`
5. `HandlePanelAction(action, uiKey)` handles panel-specific actions **before** the reducer:
   - `TogglePanelSlot` (Alt+1..9) → `UiMsg.TogglePanel(id)`
   - `CyclePanelFocus` (Ctrl+Tab) → `UiMsg.CyclePanelFocus()`
   - `ResizePanelGrow/Shrink` (Ctrl+Up/Down) → `UiMsg.ResizePanel(id, delta)`
   - `HelpPanel` (`?`) → `UiMsg.TogglePanel("help")`
   - `ToggleLogsPanel` (F12) → `UiMsg.TogglePanel("logs")`
6. If a panel owns focus (`FocusedPanelId`), route key to `panel.OnKey(uiKey, ctx)` first. Panel may consume or fall through.
7. Remaining actions dispatched via `_store.Dispatch(new UiMsg.KeyInput(action, uiKey))`.
8. If effect is not `None`, `_effects.Run(effect)` executes it.
9. If effect is `QuitApp`, `_app.Quit()` is called.

### While agent is running
- Only `Quit`, `Abort`, scroll actions, and `ToggleFocus` pass through.
- Input editing (`Char`, `Backspace`, history, autocomplete) is suppressed.

### ChatKeyMap bindings
| Action | Binding |
|--------|---------|
| `Quit` | Escape |
| `Abort` | Escape (same key, context-dependent) |
| `Submit` | Enter |
| `ToggleFocus` | F2 |
| `ScrollUpLine` | Up |
| `ScrollDownLine` | Down |
| `ScrollUpPage` | PageUp |
| `ScrollDownPage` | PageDown |
| `ScrollTop` | Home |
| `ScrollBottom` | End |
| `InputHistoryPrev` | Alt+Up |
| `InputHistoryNext` | Alt+Down |
| `Autocomplete` | Tab |
| `Backspace` | Backspace |
| `Clear` | Ctrl+L (character handler) |
| `TogglePanelSlot` | Alt+1..9 (Char code) |
| `CyclePanelFocus` | Ctrl+Tab |
| `ResizePanelGrow` | Ctrl+Up |
| `ResizePanelShrink` | Ctrl+Down |
| `HelpPanel` | `?` |
| `ToggleLogsPanel` | F12 |

---

## 10. TUI render loop

### SpectreTuiRenderer.RunInteractiveAsync
1. Creates `UiStore` and `TuiEffectHost`.
2. Binds session chrome (model/provider/agent) into state.
3. Registers builtin panels: `HelpPanel`, `TodoListPanel`, `DiffPreviewPanel`, `FileTreePanel`, `TokenBreakdownPanel`, `DiagnosticsPanel`, `LogsPanel`.
4. Calls `SeedPanelRegistryIntoState()` to push panel ids/states/sizes into `UiState` via `UiMsg.SeedPanels`.
5. Creates `ChatScreen` (Spectre.TUI `Screen`).
6. Runs `Application.Create(settings).RunAsync(_screen)`.

### ChatScreen.RenderCore (per frame)
1. `_panelShell.Ensure(state, state.IsStreaming)` — rebuild layout tree if signature changed.
2. Measure `History` area height → dispatch `UiMsg.Viewport(viewport)` if changed.
3. Rising-edge: if `IsAgentRunning && !WasRunning`, dispatch `UiMsg.ScrollResetToTail()`.
4. Project state: `_projector.Project(state)` → `UiScreenModel` → `_viewport.Apply(screen)`.
5. Build widgets: `_panels.BuildWidgets(viewport, state)` → chat widgets + panel widgets.
6. Measure `TotalLines` → dispatch `UiMsg.HistoryMeasured(total)` if changed.
7. Clamp scroll to `MaxScroll` → dispatch `UiMsg.ScrollClamp(max)` if needed.
8. Render each widget into its layout area via `context.Render(widget, area)`.

### Layout trees
**ChatLayoutShell** (legacy, still used by `ChatViewProjector.Layout`):
- No panels: `Header(1) → History → Input(3) → Footer(1)`
- Streaming: `Header(1) → History → StreamBar(1) → Input(3) → Footer(1)`

**PanelLayoutShell** (current, used by `ChatScreen.RenderCore`):
- No panels: identical to legacy.
- With panels: `Header(1) → Body(cols: Left + Centre + Right) → Input(3) → Footer(1)`
  - Centre: `TopPanels → Header → History → [StreamBar] → BottomPanels → Input → Footer`
  - Each placement stack: `Tabs(1) + PanelContent(size or flex)`

---

## 11. Panel switching in TUI

### Key bindings
| Action | Key | Effect |
|--------|-----|--------|
| Toggle panel | Alt+1..Alt+9 | `UiMsg.TogglePanel(id)` |
| Cycle focus | Ctrl+Tab | `UiMsg.CyclePanelFocus()` |
| Close panel | Esc / `q` (when panel focused) | `UiMsg.FocusPanel(null)` |
| Grow panel | Ctrl+Up / Ctrl+Right | `UiMsg.ResizePanel(id, +1)` |
| Shrink panel | Ctrl+Down / Ctrl+Left | `UiMsg.ResizePanel(id, -1)` |
| Help panel | `?` | `UiMsg.TogglePanel("help")` |
| Logs panel | F12 | `UiMsg.TogglePanel("logs")` |

### Focus routing
- When `FocusedPanelId` is set, keystrokes are routed to `panel.OnKey(uiKey, ctx)` first.
- Panel may consume (`return true`) or fall through.
- Esc / `q` while focused always returns focus to chat.

### Tab strip
- `PanelViewProjector.BuildTabStrip` renders `1:Title 2:Title ...` for visible panels in a placement.
- Focused panel is bold/aqua; others are grey.

---

## 12. TUI status bar

The SpectreTui does **not** use the `StatusBarView` from `Harbor.Terminal.Abstractions`. Instead:

- `DefaultUiProjector.ProjectStatusBar(state)` builds a `UiStatusBarModel` with segments (provider/model, status glyph, agent, tokens, cost, scroll %).
- `DefaultUiProjector.ProjectFooter(state)` joins those segments into a single centered footer string.
- `ChatChromeView.BuildFooter(maxScroll, effectiveScroll)` renders the footer string as a centered `Paragraph` widget in the `Footer` layout area.
- The header (`ChatChromeView.BuildHeader`) shows `Provider/Model · Agent · Tokens↑ ↓ · $Cost · StatusPill`.

So status bar information is split across:
- **Header** — route, agent, tokens, cost, status pill.
- **Footer** — full status bar segments (left/center/right) + scroll % + mode indicator.

---

## 13. Gap analysis — missing desktop chrome features

The `UiState` / `UiReducer` / `UiStore` / `PanelRegistry` layer supports a rich chrome model. The SpectreTui currently uses only a subset. Missing from TUI today:

| Feature | Desktop (Avalonia/WPF) | SpectreTui | Gap |
|---------|------------------------|------------|-----|
| **Overlay / modal stack** | `OverlayController` + `IOverlayStack` in `Harbor.Ui.Framework.Overlays` | ❌ Not used | No modals, flyouts, pickers. `TuiEffect` has no `OpenOverlay`/`CloseOverlay`. `UiState` has no overlay field. |
| **Shell state / mode toggles** | `ShellState` (right-drawer tab, layout dimensions) | ❌ Not used | No right-drawer, no shell-mode switching. |
| **Session rail / sidebar** | `SessionRowViewModel` + shell rail | ❌ Not present | No session list, no session switching UI. |
| **Command palette** | Mentioned in README (`CommandPalette` — Cmd+K) | ❌ Not implemented | No command palette overlay exists in code. |
| **Settings / config overlay** | `IThemeService`, `OverlayController` | ❌ Not present | No settings modal. |
| **Theme picker** | HDS palettes via `IThemeService` | ❌ Not present | PLAN.md lists as TODO. |
| **Permission prompts** | Interactive permission flow | ❌ Not present | PLAN.md lists as TODO. |
| **Mouse support** | Click to scroll, drag dividers | ❌ Not present | PLAN.md lists as TODO. |
| **Inline image rendering** | Where supported | ❌ Not present | PLAN.md lists as TODO. |
| **Configurable keybindings** | | ❌ Not present | PLAN.md lists as TODO. |
| **Center / FloatingTab panel placements** | `TuiPanelPlacement.Center`, `FloatingTab` in enum | ❌ Not used by `PanelLayoutShell` | `PanelLayoutShell` only handles `Top`, `Bottom`, `Left`, `Right`. Center overlay panels and floating tabs are defined but not laid out. |
| **Panel persistence (Pinned)** | `TuiPanelState.Pinned` exists | ❌ Not used in reducer | `Pinned` state is defined but `UiReducer` never transitions to it (only `Hidden`/`Visible`/`Focused`). No "pin" action exists in `ChatAction`. |
| **Token-usage footer** | Present in `DefaultUiProjector` | ✅ Rendered in header/footer | Already present. |

### Summary
The SpectreTui has a solid **panel system** (dockable Top/Bottom/Left/Right panels with tabs, toggle, focus, resize) and a clean **TEA state loop**, but lacks all **overlay/shell chrome** features: no modal stack, no session rail, no command palette, no settings, no center/floating panels, and no pinned panels. The infrastructure (`OverlayController`, `IOverlayStack`, `ShellState`) exists in `Harbor.Ui.Framework` but is not wired into the SpectreTui renderer.
