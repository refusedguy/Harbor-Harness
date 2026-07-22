# Research

Updated: 2026-07-22 16:27
Status: active

## Active Summary (input for /aif:plan)
<!-- aif:active-summary:start -->
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
Next step: Составить план по включению ComponentTests + добавлению TUI state tests
<!-- aif:active-summary:end -->

## Sessions
<!-- aif:sessions:start -->
### 2026-07-22 16:27 — Аудит E2E тестовой инфраструктуры
What changed:
- Полный обзор tests/Harbor.E2E.* и src/Harbor.Ui.Framework/
- Выявлены проблемы покрытия состояний
- Составлена state-coverage matrix (58 состояний, ~21% покрыто)
Key notes:
- 2 HeadlessAvaloniaDriver: фреймворк (IE2eDriver) vs тест-проект (in-process)
- ComponentTests.disabled/ содержит 8 файлов с VlmVerifier — отключены
- 4 TUI E2E проекта имеют идентичные 4 теста каждый (copy-paste)
- TuiDriver.WaitForTextAsync в terminal-emulator mode = no-op (Task.Delay + return true)
- UiState имеет 22+ полей, ~21% покрыто (12/58 состояний)
- MockLlmServer.SetToolCallResponse не используется ни одним TUI E2E тестом
- Avalonia.Headless может не показывать динамический текст в скриншотах
- docs/screenshots/ содержит 21 Avalonia PNG + 12 TUI PNG (3 на рендерер)
Links (paths):
- tests/Harbor.E2E.Framework/
- tests/Harbor.E2E.App.Avalonia/
- tests/Harbor.E2E.Tui.SpectreTui/
- tests/Harbor.E2E.Tui.Termina/
- tests/Harbor.E2E.Tui.TerminalGui/
- tests/Harbor.E2E.Tui.RazorConsole/
- tests/Harbor.Tui.E2E.Tests/
- src/Harbor.Ui.Framework/State/
- docs/screenshots/
- docs/E2E_TESTING.md
<!-- aif:sessions:end -->

## Архитектура E2E инфраструктуры

```
tests/Harbor.E2E.Framework/          ← общая инфраструктура
├── IE2eDriver.cs                    ← 7-методный интерфейс (Start/Send/Read/Wait/Stop)
├── CliDriver.cs                     ← Process.Start для one-shot CLI
├── TuiDriver.cs                     ← PTY-wrapped (Python pty.openpty) для TUI
├── HeadlessAvaloniaDriver.cs        ← IE2eDriver: subprocess через CliDriver (framework)
├── MockLlmServer.cs                 ← HttpListener, OpenAI-compatible SSE mock
├── E2eTestBase.cs                   ← TUnit base: per-test MockLlmServer + temp HOME
├── AnsiStripper.cs / AnsiTerminalBuffer.cs / TerminalScreenshotRenderer.cs
└── HarborAppLocator.cs

tests/Harbor.E2E.App.Avalonia/HeadlessAvaloniaDriver.cs  ← ДРУГОЙ driver (in-process!)
```

Критически важно: существует ДВА HeadlessAvaloniaDriver:
1. Framework (IE2eDriver реализация) — запускает Avalonia как subprocess через CliDriver
2. Test-project (in-process) — Avalonia.Headless + UseSkia(), dedicated UI pump thread

Тестовый проект теневой (shadows) фреймворк-версию. Фреймворк-версия не используется ни одним тестом.

## Текущее покрытие тестами

### CLI E2E (Harbor.E2E.Cli/) — 6 тестов
| Тест | Состояние | Скриншот |
|---|---|---|
| VersionCommand | --version | 1 .txt |
| HelpCommand | help | — |
| TuiCommand | tui (список рендереров) | — |
| StorageCommand | storage | — |
| ProvidersCommand | providers | — |
| AskCommand | ask с mock server | — |

### Avalonia E2E (Harbor.E2E.App.Avalonia/) — 21 + 12 тестов
AvaloniaUiTests.cs (21 тест) + OrcaShellE2ETests.cs (12 тестов). 21 PNG в docs/screenshots/.

ComponentTests.disabled/ — ОТКЛЮЧЕН (8 файлов: ChatView, CommandPalette, Onboarding, SessionList, Settings, StatusBar, Toast, VlmVerifier).

### TUI E2E (Harbor.E2E.Tui.*) — 4 теста × 4 рендерера = 16 тестов
ВСЕ 4 проекта имеют ИДЕНТИЧНЫЕ тесты (copy-paste, только TuiName меняется):

| Тест | Что проверяет | Скриншоты |
|---|---|---|
| Start_ShowsWelcomeBanner | "test-model" appears | — |
| SlashHelp_IsDispatched | "/help" echoed | — |
| CtrlC_AbortsTui | process exits | — |
| Screenshot_CapturesCoreStates | boot, help, input-typed | 3 PNG |

### TUI Unit E2E (Harbor.Tui.E2E.Tests/) — ~30 тестов
RendererE2E (2), BuiltinViewE2E (6), InteractiveRendererE2E (24), ChatBridgeE2E (Termina+RazorConsole ~20).

## State Coverage Matrix: UiState

UiState (из src/Harbor.Ui.Framework/State/UiState.cs) имеет 22 поля. Покрытие:

| Поле UiState | Avalonia E2E | TUI E2E | TUI Unit |
|---|---|---|---|
| Lines | ✅ (message-sent) | ❌ | ✅ (BuiltinView) |
| Active | ❌ | ❌ | ❌ |
| IsStreaming | ✅ (19-streaming) | ❌ | ✅ (InteractiveRenderer) |
| Status | ✅ (idle/running) | ✅ (idle) | ✅ (RendererE2E) |
| Cost | ❌ | ❌ | ❌ |
| Model | ✅ (status bar) | ✅ (boot sentinel) | ✅ |
| Provider | ✅ (status bar) | ✅ (boot sentinel) | ✅ |
| AgentName | ❌ | ❌ | ❌ |
| IsAgentRunning | ✅ (19-streaming) | ❌ | ✅ (InteractiveRenderer) |
| WasRunning | ❌ | ❌ | ❌ |
| ShouldQuit | ❌ | ✅ (/exit) | ❌ |
| Input | ✅ (input-typed) | ✅ (input-typed) | ❌ |
| Focus | ❌ | ❌ | ❌ |
| ScrollOffset | ❌ | ❌ | ❌ |
| ViewportLines | ❌ | ❌ | ❌ |
| TotalLines | ❌ | ❌ | ❌ |
| PanelStates | ❌ | ❌ | ❌ |
| PanelSizes | ❌ | ❌ | ❌ |
| FocusedPanelId | ❌ | ❌ | ❌ |
| RegisteredPanelIds | ❌ | ❌ | ❌ |

ChatAction coverage (22 действий):
| Действие | Avalonia | TUI E2E |
|---|---|---|
| Quit | ❌ | ✅ (CtrlC) |
| Abort | ❌ | ✅ (CtrlC) |
| Submit | ✅ (Send button) | ✅ (/help, /exit) |
| ToggleFocus | ❌ | ❌ |
| Scroll* | ❌ | ❌ |
| InputHistoryPrev/Next | ❌ | ❌ |
| Autocomplete | ❌ | ❌ |
| Backspace | ❌ | ❌ |
| Clear | ✅ (ClearCommand) | ❌ |
| TogglePanelSlot | ❌ | ❌ |
| CyclePanelFocus | ❌ | ❌ |
| ClosePanel | ❌ | ❌ |
| ResizePanelGrow/Shrink | ❌ | ❌ |
| HelpPanel | ✅ (? key) | ✅ (? key) |
| ToggleLogsPanel | ✅ (F12) | ✅ (F12) |

Event Coverage: AgentEvent → UiReducer (8 типов):
| Событие | UiReducer | Avalonia | TUI E2E |
|---|---|---|---|
| AgentStartEvent | ✅ | ✅ | ✅ (boot) |
| MessageStartEvent | ✅ | ❌ | ❌ |
| MessageUpdateEvent | ✅ | ❌ | ❌ |
| MessageEndEvent | ✅ | ❌ | ❌ |
| ToolExecutionStartEvent | ✅ | ❌ | ❌ |
| ToolExecutionEndEvent | ✅ | ❌ | ❌ |
| CompactionStartedEvent | ✅ | ❌ | ❌ |
| CompactionCompletedEvent | ✅ | ❌ | ❌ |
| AgentErrorEvent | ✅ | ❌ | ❌ |
| AgentEndEvent | ✅ | ✅ | ✅ (CtrlC) |

MockLlmServer.SetToolCallResponse существует, но ни один TUI E2E тест его не использует.

## Root causes

1. TUI E2E = copy-paste 4 раза → нет renderer-specific state coverage
2. TuiDriver.WaitForTextAsync = no-op в screenshot mode → assertions бесполезны
3. ComponentTests.disabled/ отключены → 8 файлов с глубоким покрытием + VlmVerifier не используются
4. Два HeadlessAvaloniaDriver → путаница, framework-версия не используется
5. Нет state-coverage matrix → никто не знает какие состояния покрыты
6. MockLlmServer.SetToolCallResponse не используется → tool call UI пути не тестируются

## Полный перечень состояний UiState (58 состояний)

Состояния данных (data states):
1. Lines пустой (empty chat)
2. Lines с user message
3. Lines с assistant message
4. Lines с tool call (start)
5. Lines с tool result (end)
6. Lines с error message
7. Lines с thinking block
8. Lines с system message (compaction)
9. Active.TextBuffer непустой (streaming)
10. Active.ThinkBuffer непустой (thinking)
11. Active пустой (not streaming)

Состояния статуса (status states):
12. Status = "idle" + !IsAgentRunning
13. Status = "running" + IsAgentRunning + IsStreaming
14. Status = "running" + IsAgentRunning + !IsStreaming (thinking)
15. Status = "compacting"
16. Status = "error"

Состояния фокуса (focus states):
17. Focus = Input
18. Focus = Chat
19. Focus = Panel + FocusedPanelId != null

Состояния скролла (scroll states):
20. ScrollOffset = 0 (pinned to tail)
21. ScrollOffset > 0 (scrolled up)
22. ScrollPercent = 0 / > 0 / 100

Состояния панелей (panel states):
23. PanelStates пустой (нет панелей)
24. Панель Hidden
25. Панель Visible
26. Панель Focused
27. Панель Pinned
28. PanelSizes[id] > 0 (resized)
29. RegisteredPanelIds непустой

Состояния ввода (input states):
30. Input.Text пустой
31. Input.Text непустой
32. Input.History пустой
33. Input.History непустой (можно навигировать)
34. Input.HistoryIndex = -1 (не в истории)
35. Input.HistoryIndex >= 0 (в истории)

Состояния агента (agent states):
36. IsAgentRunning = false + WasRunning = false
37. IsAgentRunning = true + WasRunning = false (rising edge)
38. IsAgentRunning = true + WasRunning = true
39. IsAgentRunning = false + WasRunning = true (falling edge)
40. ShouldQuit = true

Состояния транзакций (cost states):
41. Cost.TokensIn = 0 + Cost.TokensOut = 0
42. Cost.TokensIn > 0 + Cost.TokensOut > 0
43. Cost.CostUsd > 0

Комбинированные состояния (combined states):
44. Agent running + streaming + tool call active
45. Agent running + thinking (not streaming)
46. Error + agent stopped
47. Compaction in progress
48. Input with autocomplete active (starts with /)
49. Panel focused + input hidden
50. Multiple toasts stacked
51. Session list с git dirty badge
52. Theme dark vs light
53. Sidebar visible vs hidden
54. Chat view vs code view
55. Diff modal open
56. Command palette open с search results
57. Settings modal open
58. Provider browser open

Текущее покрытие: ~21% (12 из 58 состояний покрыты).
