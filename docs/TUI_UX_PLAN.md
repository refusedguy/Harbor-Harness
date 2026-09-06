# Harbor TUI UI/UX Improvement Plan

## Current State

### CellForge (primary renderer, always enabled)
- **Connected**: Yes, full DI registration in `CellForgeModule.cs`, `CellForgeReplRunner` owns the frame loop
- **Widgets**: VirtualizedChatTimeline, CommandPaletteView, MascotPanel, SideBarView, ToolCallBlock, ImageBlock, SpinnerStrip, TimelineRing, RetryCountdown
- **Layout**: ChatScreenLayout, LayoutTree, TimelineLayoutCache
- **Rendering**: AnsiWriter with SGR automaton, DiffEngine, PostFx, cell-diff blitter
- **Input**: TerminalInputSource (raw mode), FocusRouter, MouseRouter, PasteSanitizer, VimComposerMode
- **Streaming**: ChatScreenBridge (IEventBus → timeline), InlineAgentStreamBridge
- **Capabilities**: TerminalCapabilities, InlineImageProbe, NotifyProbe
- **Status**: StatusViewModel in ComposerController

### NickConsoleEx (additive backend)
- **Connected**: Yes, registered in TuiModule as additive backend
- **Current**: Basic MarkupControl log surface, no panel system usage
- **Potential**: Full SharpConsoleUI window system with 100+ controls

### SpectreTui (contrib, not default)
- **Connected**: No, not in default pipeline
- **Has**: PanelLayoutShell, ChatLayoutShell, 7 builtin panels, command palette, plugin host

## Gaps vs SpectreTui

1. **No panel system in CellForge** — SpectreTui has docked panels (Diagnostics, DiffPreview, FileTree, Help, Logs, TodoList, TokenBreakdown). CellForge has SideBarView but no panel host.
2. **No permission-prompt interactivity** — SpectreTui claims it, CellForge has CellForgePermissionAsker but needs UI polish.
3. **No inline image rendering** — InlineImageProbe exists but ImageBlock is basic.
4. **No mouse support** — MouseRouter exists but not wired to UI actions.
5. **No theme picker** — ThemeFileWatcher reloads on file change, no picker UI.
6. **No configurable keybindings** — LeaderKeyRouter has hardcoded bindings.
7. **NickConsoleEx underutilized** — Only uses MarkupControl, not the full window/panel/theme system.

## Proposed Improvements

### P1: CellForge Panel System
- Add `ITuiPanel` contract to `Harbor.Terminal.Abstractions`
- Implement `PanelLayoutShell` in CellForge (dock left/right/bottom)
- Port 3-4 high-value panels from SpectreTui:
  - TokenBreakdownPanel → StatusViewModel extension
  - FileTreePanel → SideBarView mode
  - HelpPanel → command palette overlay
  - DiagnosticsPanel → status bar expansion

### P1: Permission Prompt Polish
- Replace text-based permission ask with interactive card in timeline
- Add y/n/a buttons, timeout countdown, show tool args preview
- Wire to `CellForgePermissionAsker`

### P2: Image Support
- Enhance ImageBlock with Kitty protocol support
- Add image preview panel (double-click to expand)
- Inline image in transcript (constrained to cell grid)

### P2: Mouse & Keybinding UX
- Wire MouseRouter to panel resize, scroll, tab switching
- Add keybinding picker UI (editable JSON config)
- Show keybinding hints in command palette

### P2: NickConsoleEx Panel Host
- Use SharpConsoleUI's PanelControl, Grid, Splitter
- Create HarborChatWindow with sidebar + main + status bar
- Wire to existing Harbor event stream

### P3: Theme System
- Add theme picker command (`/theme`)
- Built-in themes: HarborDark, HarborLight, ConsoleExGray
- Live preview before apply

## Implementation Plan

### Phase 1: Panel Abstraction (shared)
- [ ] Add `ITuiPanel`, `PanelLayoutShell`, `DockPosition` to `Harbor.Terminal.Abstractions`
- [ ] Add `PanelViewProjector` to map UiState → panels
- [ ] Architecture tests for new contracts

### Phase 2: CellForge Panels
- [ ] Implement `CellForgePanelHost` (manages dock slots)
- [ ] Port TokenBreakdownPanel
- [ ] Port FileTreePanel → SideBarView
- [ ] Port HelpPanel → overlay
- [ ] Wire panel visibility to command palette

### Phase 3: NickConsoleEx Polish
- [ ] Replace single MarkupControl with Grid layout
- [ ] Add sidebar + status bar
- [ ] Wire to Harbor.Ui.Framework state

### Phase 4: UX Polish
- [ ] Permission card UI
- [ ] Image preview
- [ ] Keybinding picker
- [ ] Theme picker
