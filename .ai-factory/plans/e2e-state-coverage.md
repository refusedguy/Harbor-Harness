# Plan: Улучшение покрытия состояний E2E тестов для Harbor.Ui.Framework

**Branch:** `feature/e2e-state-coverage`
**Created:** 2026-07-22
**Mode:** Full
**Plan file:** `.ai-factory/plans/e2e-state-coverage.md`

## Original Request

улучшить e2e тесты в @src/Harbor.Ui.Framework/ чтобы все было идеально? потому что сейчас avalonia не совпадает с реальностью, не все состояния есть. а в TUI все одинаковое и тоже не все состояния

## Settings

- **Testing:** Yes — write tests for all new state coverage
- **Logging:** Verbose — detailed DEBUG logs for development
- **Docs:** Yes — mandatory docs checkpoint at completion
- **Roadmap Linkage:** none (no roadmap artifact exists)

## Research Context

Source: `.ai-factory/RESEARCH.md` (Active Summary, Updated: 2026-07-22 16:27, SHA256: a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2)

```
Topic: Улучшение покрытия состояний E2E тестов для Harbor.Ui.Framework
Goal: Исправить проблему неполного покрытия состояний в Avalonia E2E и TUI E2E тестах — Avalonia не совпадает с реальностью, TUI тесты одинаковы и не все состояния покрыты
Constraints:
- Avalonia.Headless (in-process off-screen) может отличаться от реального рендеринга
- TUI тесты требуют PTY (блокируется в sandbox)
- ComponentTests.disabled/ отключены
- 4 TUI E2E проекта имеют идентичные тесты (copy-paste)
- TuiDriver.WaitForTextAsync в режиме terminal-emulator — no-op (Task.Delay + return true)
- Два HeadlessAvaloniaDriver: framework (IE2eDriver) vs test-project (in-process)
Decisions:
- Нужен state-coverage matrix для UiState
- Нужен renderer-agnostic E2E state test harness
- Нужно включить ComponentTests (или адаптировать их)
- Нужно исправить TuiDriver.WaitForTextAsync в terminal-emulator mode
Open questions:
- Как тестировать TUI состояния без PTY?
- Как синхронизировать Avalonia headless с реальным UI?
- Какие состояния UiState вообще существуют и какие покрыты?
Success signals:
- Каждое состояние UiState покрыто хотя бы одним E2E тестом
- TUI тесты различаются по состояниям (не copy-paste)
- Avalonia headless покрывает все визуальные состояния
- ComponentTests включены и используют VlmVerifier
```

## Problem Statement

E2E тесты Harbor имеют три критические проблемы покрытия состояний:

1. **Avalonia не совпадает с реальностью** — `Avalonia.Headless` (off-screen software renderer) может не показывать динамический текст в скриншотах. `ComponentTests.disabled/` (8 файлов с `VlmVerifier`) отключены.

2. **TUI тесты одинаковы** — 4 рендерера (SpectreTui, Termina, TerminalGui, RazorConsole) имеют идентичные 4 теста (copy-paste). Покрывают только 3 состояния: boot, help-echo, input-typed.

3. **State coverage ~21%** — из 58 состояний `UiState` только 12 покрыты. Критически не хватает: streaming buffer, thinking, tool calls, compaction, panel focus, scroll, input history, autocomplete, error state.

## Tasks

### Phase 1: Инфраструктурные исправления

#### Task 1.1: Включить ComponentTests и исправить ссылки ✅

**Deliverable:** ComponentTests включены в сборку, VlmVerifier работает.

**Files to change:**
- `tests/Harbor.E2E.App.Avalonia/Harbor.E2E.App.Avalonia.csproj` — убрать `<Compile Remove="ComponentTests.disabled\**\*"/>`
- `tests/Harbor.E2E.App.Avalonia/ComponentTests.disabled/` → переименовать в `ComponentTests/`
- `tests/Harbor.E2E.App.Avalonia/ComponentTests/VlmVerifier.cs` — исправить путь к `z-ai` CLI (fallback если не установлен)

**Logging:**
- `INFO [aif-plan] ComponentTests enabled` при успешном включении
- `WARN [docs]` если VlmVerifier недоступен (HARBOR_SKIP_VLM=1)

**Dependencies:** Task 1.1 — нет зависимостей.

---

#### Task 1.2: Исправить TuiDriver.WaitForTextAsync в terminal-emulator mode ✅

**Deliverable:** `WaitForTextAsync` в terminal-emulator mode использует реальное чтение экрана вместо `Task.Delay + return true`.

**Files to change:**
- `tests/Harbor.E2E.Framework/TuiDriver.cs` — заменить no-op на чтение через `xdotool getwindowfocus getwindowname` или fallback на `ReadScreenAsync` с задержкой

**Logging:**
- `DEBUG [TuiDriver] WaitForTextAsync: polling for '{pattern}' in terminal-emulator mode`
- `WARN [TuiDriver] terminal-emulator text polling unavailable, falling back to delay`

**Dependencies:** Task 1.1.

---

#### Task 1.3: Унифицировать HeadlessAvaloniaDriver ✅

**Deliverable:** Один `HeadlessAvaloniaDriver` вместо двух. Framework-версия удалена или объединена.

**Files to change:**
- `tests/Harbor.E2E.Framework/HeadlessAvaloniaDriver.cs` — удалить (или перенести методы из test-project версии)
- `tests/Harbor.E2E.App.Avalonia/HeadlessAvaloniaDriver.cs` — остается как единственная реализация

**Logging:**
- `INFO [aif-plan] HeadlessAvaloniaDriver unified: framework version removed`

**Dependencies:** Task 1.1.

---

### Phase 2: State Coverage Matrix

#### Task 2.1: Создать state-coverage matrix в docs/E2E_TESTING.md ✅

**Deliverable:** Таблица покрытия состояний `UiState` × тестовые проекты в `docs/E2E_TESTING.md`.

**Files to change:**
- `docs/E2E_TESTING.md` — добавить секцию "State Coverage Matrix" с полным перечнем 58 состояний

**Logging:**
- `INFO [docs] State coverage matrix added to E2E_TESTING.md`

**Dependencies:** Task 1.1.

---

### Phase 3: TUI E2E state tests

#### Task 3.1: Добавить TUI state tests для streaming/tool-call/error/compaction

**Deliverable:** Новые тесты в каждом TUI E2E проекте, использующие `MockLlmServer.SetToolCallResponse` и streaming.

**Files to change:**
- `tests/Harbor.E2E.Tui.SpectreTui/SpectreTuiE2ETests.cs`
- `tests/Harbor.E2E.Tui.Termina/TerminaE2ETests.cs`
- `tests/Harbor.E2E.Tui.TerminalGui/TerminalGuiE2ETests.cs`
- `tests/Harbor.E2E.Tui.RazorConsole/RazorConsoleE2ETests.cs`

**New tests per renderer:**
- `Streaming_ShowsResponse` — `MockLlmServer.SetResponse` + проверка streamed текста в экране
- `ToolCall_RendersToolCard` — `MockLlmServer.SetToolCallResponse` + проверка tool call карточки
- `ErrorState_ShowsError` — `MockLlmServer` возвращает ошибку + проверка error состояния
- `Compaction_ShowsCompactionStatus` — проверка status=compacting
- `AgentRunning_ShowsRunningBanner` — проверка agent running состояния

**Logging:**
- `DEBUG [TUI-E2E] Streaming test: waiting for '{text}' in screen buffer`
- `DEBUG [TUI-E2E] Tool call test: verifying tool card rendering`

**Dependencies:** Task 1.2, Task 2.1.

---

#### Task 3.2: Добавить TUI state tests для panel/scroll/input-history/autocomplete

**Deliverable:** Тесты для состояний панелей, скролла, истории ввода, автодополнения.

**Files to change:**
- `tests/Harbor.E2E.Tui.SpectreTui/SpectreTuiE2ETests.cs` (и другие TUI проекты)

**New tests:**
- `F12_TogglesLogsPanel` — уже есть, расширить
- `Alt1_TogglesPanel` — Alt+1 для переключения панелей
- `CtrlTab_CyclesPanelFocus` — Ctrl+Tab для циклического переключения
- `ScrollUp_ScrollsHistory` — PageUp для скролла
- `AltUp_NavigatesInputHistory` — Alt+Up для навигации по истории
- `Tab_AutocompleteSlashCommand` — Tab для автодополнения /help

**Logging:**
- `DEBUG [TUI-E2E] Panel toggle test: Alt+1 pressed, verifying panel state`
- `DEBUG [TUI-E2E] Scroll test: PageUp pressed, verifying scroll offset`

**Dependencies:** Task 3.1.

---

### Phase 4: Avalonia E2E state tests

#### Task 4.1: Добавить Avalonia E2E тесты для streaming/thinking/tool-call/error

**Deliverable:** Новые тесты в `AvaloniaUiTests.cs` для состояний, не покрытых ComponentTests.

**Files to change:**
- `tests/Harbor.E2E.App.Avalonia/AvaloniaUiTests.cs`

**New tests:**
- `Chat_ShowStreamingBuffer` — `Chat.IsStreaming = true` + `StreamingBuffer` непустой
- `Chat_ShowThinkingBuffer` — `Chat.IsThinking = true` + thinking текст
- `Chat_ShowToolCallCard` — tool call карточка в чате
- `Chat_ShowErrorState` — error message в красном цвете
- `Chat_ShowCompactionStatus` — status=compacting в статус-баре

**Logging:**
- `DEBUG [Avalonia-E2E] Streaming buffer test: IsStreaming=true, buffer length={len}`
- `DEBUG [Avalonia-E2E] Tool call card test: verifying ToolCallViewModel`

**Dependencies:** Task 1.1.

---

#### Task 4.2: Добавить Avalonia E2E тесты для panel/scroll/focus/input-history

**Deliverable:** Тесты для состояний панелей, скролла, фокуса, истории ввода в Avalonia.

**Files to change:**
- `tests/Harbor.E2E.App.Avalonia/AvaloniaUiTests.cs`

**New tests:**
- `Panel_ToggleVisibility` — переключение видимости панели
- `Panel_FocusPanel` — фокус на панель
- `Input_HistoryNavigation` — Alt+Up/Down для навигации по истории
- `Input_AutocompleteSlashCommand` — Tab для автодополнения
- `Chat_ScrollHistory` — прокрутка истории чата

**Logging:**
- `DEBUG [Avalonia-E2E] Panel toggle test: IsSidebarVisible={visible}`
- `DEBUG [Avalonia-E2E] Input history test: HistoryIndex={index}`

**Dependencies:** Task 4.1.

---

### Phase 5: Renderer-agnostic E2E state harness

#### Task 5.1: Создать renderer-agnostic E2E state test harness

**Deliverable:** Абстракция над `IE2eDriver` + `UiState` projections, которая тестирует состояния независимо от рендерера.

**Files to create:**
- `tests/Harbor.E2E.Framework/StateTestRunner.cs` — класс для запуска state-based E2E тестов
- `tests/Harbor.E2E.Framework/StateTestBase.cs` — базовый класс для state-based тестов

**Design:**
```csharp
public abstract class StateTestBase : E2eTestBase
{
    // Feed a UiState snapshot and assert the driver renders it correctly
    protected async Task AssertStateRenderedAsync(
        IE2eDriver driver,
        UiState state,
        string expectedText,
        TimeSpan? timeout = null);
    
    // Drive the driver through a sequence of states
    protected async Task RunStateSequenceAsync(
        IE2eDriver driver,
        params (UiState state, string expectedText)[] steps);
}
```

**Logging:**
- `INFO [StateTestRunner] Running state sequence with {steps.Count} steps`
- `DEBUG [StateTestRunner] Step {i}: asserting '{expectedText}' in rendered output`

**Dependencies:** Task 2.1.

---

### Phase 6: Documentation

#### Task 6.1: Обновить docs/E2E_TESTING.md с новыми тестами

**Deliverable:** `docs/E2E_TESTING.md` обновлен с описанием новых тестов, state-coverage matrix, и рекомендациями по добавлению новых состояний.

**Files to change:**
- `docs/E2E_TESTING.md`

**Logging:**
- `INFO [docs] E2E_TESTING.md updated with state coverage matrix and new tests`

**Dependencies:** Tasks 3.1, 3.2, 4.1, 4.2, 5.1.

---

## Commit Plan

1. **Commit 1** (Tasks 1.1, 1.2, 1.3): Инфраструктурные исправления — включить ComponentTests, исправить TuiDriver, унифицировать HeadlessAvaloniaDriver
2. **Commit 2** (Task 2.1): State coverage matrix в docs
3. **Commit 3** (Tasks 3.1, 3.2): TUI E2E state tests
4. **Commit 4** (Tasks 4.1, 4.2): Avalonia E2E state tests
5. **Commit 5** (Task 5.1): Renderer-agnostic E2E state harness
6. **Commit 6** (Task 6.1): Документация

## Success Criteria

- [ ] ComponentTests включены и используют VlmVerifier
- [ ] TUI E2E тесты различаются по состояниям (не copy-paste)
- [ ] State coverage ≥ 60% (из 58 состояний)
- [ ] `TuiDriver.WaitForTextAsync` работает в terminal-emulator mode
- [ ] Один `HeadlessAvaloniaDriver` вместо двух
- [ ] State coverage matrix в `docs/E2E_TESTING.md`
- [ ] Renderer-agnostic E2E state harness создан
