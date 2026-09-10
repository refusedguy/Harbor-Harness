# HOTFIX SPRINT: Approval Gate Queue, Renderer Lag, Windows PTY

## Context
Bug investigation of `Harbor-Harness` (C#/.NET 10, Avalonia + ConsoleEx TUI) found three regressions blocking the next stable release. This sprint fixes them with minimal surface area. Do NOT refactor adjacent code. Do NOT rewrite the renderer. Fix only the identified roots.

## Tasks

1. Fix ApprovalGateView queue — accept two consecutive pending permissions
- **Root:** `ChatScreenBridge._pendingApproval` is a single slot. `DrainGateQueue()` overwrites it for each queued gate, so older pending permissions become unreachable zombies. `ApprovalGateView.Id` also uses only `toolName`, causing collisions for same-tool gates.
- **Fix:** Replace single `_pendingApproval` with a bounded pending queue. `DrainGateQueue()` must enqueue all gates and keep them interactable in arrival order. Make `Id` unique per gate instance, not per tool name.
- **Acceptance:** Two consecutive `bash` permission prompts can be accepted one after another without blocking or loss. No zombie `IsPending` blocks remain after acceptance. `ApprovalGateView` tests pass.

2. Fix renderer lag and wide-cell artifacts
- **Root:** `ScreenBuffer.SetStyleAt` skips wide lead cells, so `PanelFx.BlendRegion` animations leave wide glyphs with stale colors. `VirtualizedChatTimeline.Append` does an O(n) budget scan and `Array.Copy` eviction on every append, yielding O(n²) during streaming.
- **Fix:** Update wide-cell handling in style writes so animations apply to full clusters. Replace repeated budget scans with a running total. Make eviction amortized, not per-append.
- **Acceptance:** Wide characters no longer show color artifacts during `PanelFx.BlendRegion` animations. Streaming benchmark shows stable frame times under sustained append load. Existing tests pass.

3. Fix Windows PTY restore path
- **Root:** `ConsoleExReplRunner.RunAsync` `finally` block uses `await backend.WriteAsync(...)` before `modeController.Restore()`. If the write throws, `Restore()` is skipped, leaving Windows consoles in raw mode. `Console.WindowWidth` is also unreliable in raw/VT mode on Windows.
- **Fix:** Make `Restore()` unconditional in `finally` — do not await non-restore writes there, or wrap them in a nested try/catch so restore cannot be skipped.
- **Acceptance:** Process termination on Windows returns the console to normal mode. Unix path unchanged. No new allocations in the hot path.

## Hard Rules
- One commit = 1–2 files.
- After each commit run focused tests for the touched area only.
- Do NOT touch `Harbor.DesignSystem` unless the test forces it.
- Do NOT change sprint-chain, mascot, or unrelated widgets.
- Preserve public API; only fix internals.

## Deliverables
- 2–3 atomic commits with focused diffs
- Test summary per commit
- Short findings note in `.kilo-docs/sprints/ui-v2/findings.json`
