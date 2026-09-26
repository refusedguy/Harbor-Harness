# SPRINT: Renderer Moat — Cell-Diff Deepening

## Context
Harbor's cell-diff engine is the #1 unfair advantage vs ratatui/line-based TUIs. This sprint hardens it: partial-scan diffs, lock-free hot-swap runtime, and shader-like post-render effects. Goal: make the renderer moat unclonable for 12+ months.

## Tasks
1. Partial-scan diffs via DiffEngine.FrameHint
   - Only rescan cells marked dirty by PanelFx/Widget changes
   - Zero allocations in steady-state scan path
   - Acceptance: full-timeline golden test passes; perf probe shows <2ms diff time for 500-row timeline

2. Lock-free hot-swappable runtime
   - Swap ScreenBuffer backends without stopping the render loop
   - Atomic double-buffer handoff for ConsoleEx ↔ Avalonia ↔ Blazor
   - Acceptance: theme switch mid-stream shows no torn frames; no lock contention under load

3. Shader-like post-render effects
   - TachyonFX-style bloom/glow for warning/error states only
   - Composable effect pipeline: apply after diff, before ANSI write
   - Acceptance: approval gate pulse uses post-render glow; zero perf regression on non-effect frames

## Hard Rules
- Do NOT rewrite ScreenBuffer or DiffEngine from scratch; extend existing types.
- Do NOT add allocations in the steady-state render path.
- Do NOT break existing ConsoleEx PTY golden tests.
- Preserve public IRenderer API; only add internal effect hooks.

## Deliverables
- 3 atomic commits with focused diffs
- Perf benchmark report (before/after diff time, frame time)
- Test summary per commit
