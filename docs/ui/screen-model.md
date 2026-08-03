# UiScreenModel — Screen Model Specification

## Overview

`UiScreenModel` is the framework-agnostic projection of `UiState` into a form that
each renderer can consume. It is produced by `IUiProjector.Project(UiState)` — a pure
function with no I/O, no dispatcher, no framework references.

## Assembly

`Harbor.Ui.Framework.Projection` (in `src/Harbor.Ui.Framework/Projection/`)

## Threading contract

1. `IUiProjector.Project` — pure, thread-safe, no dispatcher.
2. `UiStore` notifications — as today; subscribers read `store.State` at the moment
   of execution, never capture it in a delayed closure.
3. `IUiViewport.Apply` — either called already on the UI/main loop thread, or
   marshals itself and reads `store.State` inside the callback (latest-wins).

## Identity contract

- Each `UiBlock.Id` is stable across stream updates.
- Message: `msg:{index}`; tool: `tool:{lineIndex}`; panel: `panel:{name}`.
- Adapters reconcile by Id (GUI) or full-repaint (TUI).

## Streaming contract

- On delta: same block Id, `Phase=Streaming`, updated Spans.
- Coalescing: if apply does not keep up, intermediate screens drop (latest wins).
- `Transcript.StreamingBlockId` points to the block that is currently streaming.

## Panel contract

- Visibility, pinned, size, body — all in `UiSidePanelModel`.
- Invariant: `IsVisible=true` ⇒ adapter MUST mount panel in layout tree.
- Invariant: `IsVisible=false` ⇒ MUST unmount or set Collapsed (uniform policy).

## Tool card contract

- In state: explicit `ToolCallId` on call and result lines.
- In model: one `ToolCallCardModel` with `Status = Pending | Running | Done | Error`.
- Forbidden: view looks for "next line as result" by index.

## Fields

### UiHeaderModel
- `Model`, `Provider`, `AgentName` — session chrome
- `IsAgentRunning`, `IsStreaming` — activity flags
- `Cost` — token/cost accounting

### UiTranscriptModel
- `Blocks` — list of `UiBlock` (message, separator, system notice)
- `StickToBottomVersion` — scroll policy hint (null = no auto-scroll)
- `StreamingBlockId` — id of the block currently receiving delta text

### UiBlock (abstract)
- `Id` — stable identifier

### UiMessageBlock
- `Id`, `Role`, `Spans`, `Phase` (Complete/Streaming/Thinking), `ToolCall`

### UiSeparatorBlock
- `Id`

### UiSystemNoticeBlock
- `Id`, `Text`, `Style`

### StyledSpan
- `Text`, `Foreground`, `Background`, `Bold`, `Italic`, `Underline`, `Dim`
- No framework-specific markup strings — only structured data.

### ToolCallCardModel
- `ToolCallId`, `ToolName`, `ArgsPreview`, `Status`, `ResultPreview`

### UiSidePanelModel
- `Id`, `Title`, `IsVisible`, `IsPinned`, `Size`, `Body`

### UiInputModel
- `Text`, `Caret`, `IsEnabled`, `Placeholder`, `Mode`

### UiStatusBarModel
- `Segments` — ordered list of text segments with alignment and style

### UiFocusTarget
- `Kind` (Chat/Input/Panel/Modal), `PanelId` (nullable)

### UiThemeModel
- `Name`, `SemanticTokens` (map of semantic role → color token)

### UiScreenModel (root)
- `Header`, `Transcript`, `StatusBar`, `Input`, `SidePanels`, `Focus`, `Theme`, `StateRevision`