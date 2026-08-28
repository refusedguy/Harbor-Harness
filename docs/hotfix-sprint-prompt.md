# HOTFIX SPRINT: Approval Gate Queue, Renderer Lag, Windows PTY

## Context
Bug investigation of `Harbor-Harness` (C#/.NET 10, Avalonia + ConsoleEx TUI) found three regressions blocking the next stable:

1. **Consecutive pending permissions impossible.** `ChatScreenBridge` (`src/Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs`) owns a single-slot `_pendingApproval`. `DrainGateQueue()` dequeues ALL pending gates from `_gateQueue` but only retains the LAST one in `_pendingApproval`; earlier gates are lost from the routing path and can never receive y/n/a input.
2. **Avalonia renderer lag / cleanup thrash.** `UiRenderEngine.ReconcileLines` and `ReconcileToolCalls` (`apps/Harbor.App.Avalonia/Services/UiRenderEngine.cs`) rebuild the entire `ObservableCollection` on every frame when counts differ (`Clear()` + re-add). During streaming this fires Avalonia layout passes for unchanged rows and leaks disposed gate/tool-card instances that are never removed from the timeline.
3. **Windows-specific PTY/rendering.** `WindowsVtModeController` (`src/Harbor.Tui.ConsoleEx/Input/WindowsVtModeController.cs`) silently swallows `SetConsoleMode` failures in `Restore()`, leaving the terminal in raw mode on crash. `Enter()` rolls back stdin when stdout VT setup fails, but the exception path does not guarantee the console recovers. The ConsoleEx renderer also never explicitly calls `EnableWindowsAnsi()` before writing ANSI sequences on Windows 10+, so VT processing may be disabled at startup.

## Tasks
- **T1 — Approval gate queue:** Change `_pendingApproval` from a single field to a FIFO queue of pending gates (`Queue<ApprovalGateView>`). Route key/click events to the OLDEST undecided gate first; only clear a gate from the queue after its `DecisionRecorded` fires. Keep `_gateQueue` for off-thread enqueue, but drain it into the pending queue on `Tick()`. Add a unit test in `ChatScreenBridgeTests.cs` that posts two consecutive `RequestApprovalGate` calls and asserts both receive key input in order.
- **T2 — Renderer lag / cleanup:** In `UiRenderEngine.cs`, replace full `Clear()`+re-add rebuilds with positional reuse: reuse existing `ChatLineViewModel` instances when `Role`+`Text` match, and only mutate changed indices. Add a `RemoveStaleBlocks()` pass after reconciliation that drops `ApprovalGateView` and `ToolCallViewModel` entries whose `IsPending` is false or whose tool call id no longer appears in `state.Lines`. Ensure `ReconcileTimeline` does not create new VMs for unchanged rows.
- **T3 — Windows PTY/rendering:** In `WindowsVtModeController.cs`, make `Restore()` throw `InvalidOperationException` with Win32 error code when `SetConsoleMode` fails (instead of silently ignoring). Add a guard in `Enter()` so stdout failure does NOT roll back stdin raw mode — leave stdin in raw mode and surface the stdout error separately. In `ConsoleExReplRunner.EnterAltScreen()` or `Program.cs`, call `EnableWindowsAnsi()` before writing any ANSI escape sequences on Windows (use the P/Invoke stub from `specs/07-tui.md`).

## Hard Rules
- Do NOT change the public event contracts (`DecisionRecorded`, `AgentEvent` flow).
- Do NOT introduce locks on the render thread; all timeline mutation must stay single-threaded via the frame loop.
- Do NOT break existing golden tests in `tests/Harbor.Tui.ConsoleEx.Tests/` or Avalonia headless specs.
- Windows changes must be guarded by `OperatingSystem.IsWindows()` and must not throw from `Restore()` in a way that aborts the REPL shutdown sequence — surface via `ILogger` instead.
- Keep `ObservableCollection` notification count flat; no full-list rebuilds on streaming ticks.
- Do NOT modify `specs/07-tui.md`; it is the design reference, not the implementation target.
- All new code paths must have a corresponding `[Test]` in the existing test project for that layer.

## Deliverables
- `src/Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs` — multi-gate pending queue, ordered routing, new test.
- `apps/Harbor.App.Avalonia/Services/UiRenderEngine.cs` — incremental reconciliation, stale-block cleanup.
- `src/Harbor.Tui.ConsoleEx/Input/WindowsVtModeController.cs` — fail-loud Restore, Enter() guard, ANSI enable on startup.
- `tests/Harbor.Tui.ConsoleEx.Tests/ChatScreenBridgeTests.cs` — new `ConsecutiveApprovalGates_RouteInOrder` test.
- Verification: `dotnet test tests/Harbor.Tui.ConsoleEx.Tests` and `dotnet test apps/Harbor.App.Avalonia` green.
- Commit message format: `fix(consoleex|avalon ia|windows): <short description>` — one commit per T-area.
## Acceptance Criteria
- **AC1:** Two back-to-back `RequestApprovalGate("bash", "cmd1")` then `RequestApprovalGate("bash", "cmd2")` both appear on the timeline, and pressing `y` on the first gate resolves only the first while the second remains pending.
- **AC2:** Streaming a 100-line assistant message into ConsoleEx produces no more than 5% layout passes vs. the current baseline; `ObservableCollection` `CollectionChanged` events stay under 2 per frame during steady-state streaming.
- **AC3:** On Windows 10/11, `ConsoleEx` renders ANSI colors and box-drawing characters correctly after startup without requiring the user to manually enable VT processing.
- **AC4:** If `Restore()` encounters a Win32 error, the REPL logs the failure and continues shutdown rather than crashing or leaving the terminal in raw mode.

## Test Plan
- Add `ConsecutiveApprovalGates_RouteInOrder` to `ChatScreenBridgeTests.cs`.
- Add `ReconcileLines_NoThrashOnUnchangedRows` to the Avalonia test project.
- Add `WindowsVtModeController_Restore_ThrowsOnFailure` to the ConsoleEx test project.
- Run full CI: `dotnet test tests/Harbor.Tui.ConsoleEx.Tests` and `dotnet test apps/Harbor.App.Avalonia` must pass on Linux and Windows runners.

## Verification Steps
1. Run `dotnet test tests/Harbor.Tui.ConsoleEx.Tests` — all existing tests must pass, new `ConsecutiveApprovalGates_RouteInOrder` must pass.
2. Run `dotnet test apps/Harbor.App.Avalonia` — all existing headless specs must pass, no new layout-thrash warnings in test output.
3. On a Windows 10+ VM, run `HARBOR_TUI=consoleex dotnet run --project apps/Harbor.App.Cli` and verify that ANSI colors render correctly in Windows Terminal without manual VT enabling.
4. Verify that killing the process mid-stream (Ctrl+F5 / taskkill) does not leave the host terminal in raw mode by checking `cmd /c echo test` afterward.

## Notes for the Agent
- The sprint-chain automation will run kilo coding agents against this prompt. Each T-area should be implemented in a single focused PR.
- Do NOT refactor unrelated code; keep changes surgical and scoped to the three bugs.
- If a fix requires a new helper method, place it in the same file as the call site to avoid cross-file diff noise.
- When in doubt about Windows behavior, consult `specs/07-tui.md` §14 for the canonical VT-processing recipe.
