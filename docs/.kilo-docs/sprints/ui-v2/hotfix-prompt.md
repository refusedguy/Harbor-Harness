# HOTFIX SPRINT: Approval Gate Queue, Renderer Lag, Windows PTY

## Context
Bug investigation of `Harbor-Harness` (C#/.NET 10, Avalonia + ConsoleEx TUI) found three regressions blocking the next stable:

1. **Consecutive pending permissions impossible.** `ChatScreenBridge` owns a single-slot `_pendingApproval`. When multiple approval requests arrive, `DrainGateQueue()` overwrites it; older gates become unreachable zombie blocks. `ApprovalGateView.Id` uses only `toolName`, so same-tool gates collide.
2. **Renderer lag / improper cleanup.** `PanelFx.BlendRegion` calls `SetStyleAt` on every cell, but `SetStyleAt` skips wide lead cells, leaving artifacts. Per-cell animation blending is O(region). `VirtualizedChatTimeline.Append` recomputes budget on every append — O(n²) during rapid streaming.
3. **Windows-specific PTY issues.** `ConsoleExReplRunner.RunAsync` has `await backend.WriteAsync(...)` inside `finally`; if it throws, `modeController.Restore()` and `inputSource.Dispose()` are skipped, leaving Windows console in raw mode. `Console.WindowWidth` is unreliable in raw/VT mode on Windows.

## Tasks (выполняй по порядку)

1. **Fix approval gate queue (Bug 1).**
   - Replace single `_pendingApproval` with a `ConcurrentQueue<ApprovalGateView>` or stack.
   - Route keys/clicks to newest pending gate (`Peek`), pop on decision.
   - Make `ApprovalGateView.Id` unique: include per-gate discriminator (GUID or hash).
   - Acceptance: two consecutive `bash` permission prompts can both be accepted in order; `FocusRouter.FocusById` finds correct gate.

2. **Fix renderer lag / artifacts (Bug 2).**
   - Fix `ScreenBuffer.SetStyleAt` to update wide lead cells (and their `WideTail` halves) during `BlendRegion`.
   - Reduce `PanelFx.BlendRegion` scope: blend only dirty rows, not full block rect.
   - Maintain running `long _totalBudgetBytes` in `VirtualizedChatTimeline` to eliminate O(n) scan on append.
   - Acceptance: wide glyphs animate correctly; frame-time spikes disappear during multi-block animation; streaming append stays smooth at 1000+ blocks.

3. **Fix Windows PTY cleanup (Bug 3).**
   - Guard cleanup `await` in `ConsoleExReplRunner.RunAsync` with try/catch so `Restore()` and `Dispose()` always run.
   - Add Windows-specific terminal-size fallback: `GetConsoleScreenBufferInfo` via P/Invoke when `Console.WindowWidth` fails.
   - Unify stdin handle acquisition: use `SafeFileHandle` on Windows too, matching Unix path.
   - Acceptance: killing the process on Windows restores console to cooked mode; resize works reliably in raw/VT mode; no input latency regression.

## Hard Rules
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. Не трогай `DiffEngine`, `Cell`, `ScreenBuffer` core diff logic — только `SetStyleAt` и `BlendRegion`.
3. Не ломай PTY E2E tests: 25/25 green после каждого коммита.
4. Не меняй `ApprovalGateView` public API — только internal `Id` generation and state tracking.
5. После каждого коммита: `dotnet test tests/Harbor.Tui.ConsoleEx.PtyTests/ -c Release --no-build`.

## Deliverables
- Approval gate queue fix with unique IDs
- Renderer lag / artifact fixes (wide cells, blend region, budget eviction)
- Windows PTY cleanup + size probe fixes
- PTY E2E tests updated if needed
- HTML-отчёт в `.kilo-docs/sprints/ui-v2/hotfix-report.html`
