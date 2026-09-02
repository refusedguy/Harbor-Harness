Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт Testing Strategy.

## Context
~1350 unit tests проходят, но E2E покрытие — 12 тестов + ConsoleEx PTY suite (8 сценариев).
Нет screenshot-diff тестов для Avalonia компонентов, нет concurrent multi-session load tests,
нет integration тестов для plugin compilation против реальных исходников.

Цель спринта: довести тестовую стратегию до уровня, где регрессия невозможна — не потому что "много тестов",
а потому что каждый критический path покрыт deterministic E2E + screenshot-diff + load.

## Tasks (выполняй по порядку)

1. **ConsoleEx PTY E2E suite: 8 → 25 сценариев.**
   Добавь: resize mid-stream, paste-injection (bracketed-paste anti-injection), Ctrl+C abort sequences
   (single vs double), mouse-mode interaction, raw-mode recovery after crash, kitty keyboard protocol edge cases,
   SGR mouse enable/disable, termios size mismatch. Используй существующий PtyHarness как основу.
   Acceptance: 25/25 PTY tests green на Linux + macOS (Windows — deferred, помечь в TODO).

2. **Screenshot-diff tests для Avalonia.**
   Используй HeadlessAvaloniaDriver + golden-frame SHA-256 hashes. Покрой 4 компонента:
   ToolCallCardView, TypewriterStreamingText, Sparkline, ToastNotifications.
   Baseline stored в tests/fixtures/golden/ с именем `<TestName>.golden.png` + `.sha256`.
   Acceptance: любой pixel-level diff от golden baseline — fail. Не existence check.

3. **Concurrent multi-session load test harness.**
   Создай Harbor.LoadTests: spawn 10 sessions с 3 agents each, drive MockLlmServer с token-bucket
   rate limiter (no real-time sleeps — time-dilation через MockLlmServer.SetChunkDelay).
   Assert: no session corruption, no EventBus deadlocks, no UiStore race conditions, total memory < 200 MB.
   Acceptance: test green на 4-core машине за < 60s.

4. **Plugin compilation integration tests.**
   Скомпилируй 3 реальных plugin source: (a) samples/plugins-cs/hello-world, (b) injected deliberate error
   (missing using), (c) circular dependency scenario. Assert: CachingCompiler cache hit на (a),
   error reporting с line numbers на (b), graceful failure на (c).
   Acceptance: все 3 сценария покрыты unit test'ами, никаких hand-waved "should work".

## Hard Rules
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. Все новые тесты должны быть GREEN на первом запуске. Никаких ExpectedFail, NoTest, Skip — только рабочие тесты.
3. Golden-frame tests — hash-based, не existence check. "Файл существует" — не assertion.
4. Load test harness — deterministic. Никаких Thread.Sleep, Task.Delay на реальное время — только MockLlmServer time-dilation.
5. После каждого коммита: dotnet test tests/<Project> -c Release --no-build.

## Deliverables
- 25 PTY E2E scenarios (green)
- Screenshot-diff harness с 4 golden baselines (SHA-256)
- Concurrent multi-session load test suite (10 sessions × 3 agents)
- Plugin compilation integration tests (3 scenarios)
- test coverage report в `.kilo-docs/sprints/testing-strategy/coverage.html`
- status.json + HTML-отчёт `.kilo-docs/sprints/testing-strategy/report.html`
