# CellForge × Ui.Framework — полный бэклог интеграции

> Источник: аудит `src/Harbor.Tui.CellForge/` (~69 `.cs`: Capabilities/5, Input/15, Parsing/4, Rendering/22, Streaming/5, Widgets/23, `CellForgeTuiRenderer.cs`, `GlobalUsings.cs`) против 8 проектов `src/Harbor.Ui.Framework.*` + смежные TUI-проекты.
> Дата: 2026-09-03. Статус CellForge: рабочий alt-screen/inline движок, но **частично** интегрирован с фреймворком.

## 0. Диагноз: насколько CellForge интегрирован (факты, не мнения)

### 0.1. Матрица зависимостей

| Модуль Ui.Framework | csproj-ссылка | Реальное использование | Оценка |
|---|---|---|---|
| `Harbor.Ui.Framework.Rendering` (Cell/ScreenBuffer/Rect/TextWrap/UnicodeWidth, `Input/Key*`, `Widgets/ChatBlock/BasicBlocks/DiffBlock/WordDiff/StatusViewModel/StatusSegmentBar/PanelFx/ApprovalGateView/MascotPhases`, `Markdown/*`, `Protocol/*`, `DifferentialRenderPipeline`) | ✅ прямая | ✅ активное: `GlobalUsings.cs:5-8` (4 global using), `CellForgeDiffEncoder.cs` (`Rendering`+`Rendering.Protocol`), `MascotPanel/Director.cs`, `Assistant/StreamingMarkdownBlock.cs` + `Streaming/ChatScreenBridge.cs` (`Rendering.Markdown`) | **80% — интегрирован** |
| `Harbor.Ui.Framework.State` (`UiStore/UiState/UiMsg/UiReducer/InputModel/UiKey/ChatAction/ChatKeyMap/AsyncData/AsyncFeed/ShellStatus`, `Panels/*`, `AppState/ChatViewState/ChromeViewState/SessionsViewState`, `ChunkedBuffer/StreamingSync`) | ✅ прямая | ⚠️ точечно: `CellForgeTuiRenderer.cs:12,31,41,55,87-96` (`new UiStore()`, `_store.Dispatch(@event)` только `AgentEvent`), `Streaming/StreamBlock.cs:1` (`ChunkedBuffer/StreamingSync`), `Harbor.Ui.Framework.Panels` using без `PanelRegistry/SeedPanels` | **25% — фасад, не TEA-цикл** |
| `Harbor.Ui.Framework.Projection` (`DefaultUiProjector/UiScreenModel/StatusProjector/IUiProjector/IUiViewport/UiRenderedLine/StyledSpan/UiLineKind`, `ChatStreamingPresenter`) | ❌ нет (только транзитивно через Rendering) | ❌ один мёртвый `using` в `Widgets/JsonThemeLoader.cs:2` (реально работает через `Harbor.DesignSystem.ThemeJson`) | **0%** |
| `Harbor.Ui.Framework.Services` (`IToast/ITheme/IDialog/IOverlayStack/IFilePicker/IDispatcher/GitService+GitSessionInfo/SessionStatusTracker/OverlayController/IRenderEngine/EventBusAppStoreDispatcher`) | ❌ | ❌ (`JsonThemeLoader+ThemeFileWatcher` — свой велосипед вместо `IThemeService`) | **0%** |
| `Harbor.Ui.Framework.ViewModels` (`ChatLine/ToolCall/Diff/SessionRow/TokenUsage/StoreSubscriber`, `StatusMappers/CostAnimator`) | ❌ | ❌ | **0%** |
| `Harbor.Ui.Framework.Sessions` (`SessionManager/Factory/Switcher/Context/GitTracker/IChatViewBinder`) | ❌ | ❌ (один `new UiStore()`, нет `BindSession/Rebind`, `QuickSwitchSlots.cs` — свой реестр вместо `SessionSwitcher`) | **0%** |
| `Harbor.Ui.Framework.Reducers` (`AppStore/AppReducer/ChatView/Chrome/SessionsReducer`) | ❌ (и в мета-пакет не входит) | ❌ | **0% (требует решения: удалить или бриджевать)** |
| `Harbor.Ui.Framework.Abstractions` (`OverlayIds/IShellChrome/IContentHost/IWorkspaceCommands/IDiagnosticsPanel/ICommonConfigReader`) | ❌ (только транзитивно) | ❌ | **0%** |
| Мета-пакет `Harbor.Ui.Framework` | ❌ | ❌ | **0%** |

Итого: **~2 из 9 модулей подключены, из них полноценно 1 (`Rendering`). Реальная интеграция ≈ 20-25%.**

### 0.2. Главный разрыв в одном файле

`src/Harbor.Tui.CellForge/CellForgeTuiRenderer.cs:116-132` `ProjectStateIntoWidgets` маппит только 6 полей (`Status/Model/Cost.TokensIn-Out/CostUsd/IsStreaming/Active.TextBuffer`), игнорируя (схема `src/Harbor.Ui.Framework.State/State/UiState.cs`): `Lines`, `Active.ThinkBuffer`, `PendingStreamText/PendingStreamThink`, `Provider/AgentName/IsAgentRunning/WasRunning/ShouldQuit`, `Input:InputModel`, `Focus/FocusedPanelId`, `ScrollOffset/ViewportLines/TotalLines/ScrollPercent`, `PanelStates/PanelSizes/RegisteredPanelIds`, `Sessions/ActiveSessionId/IsLoading`. Плюс `CellForgeTuiRenderer.cs:63-71` `ShouldRenderPlacement` отрубает `ChatHistory/Input` из `Harbor.Terminal.Abstractions` — 2 из 4 builtin views никогда не красятся.

### 0.3. Что есть в других проектах и отсутствует в CellForge (кратко)

- `src/Harbor.Terminal.Abstractions/`: `InputViewModel/DiffPreviewViewModel`, `DiffPreviewView (Overlay)`, `StatusBarView (Formatted/running/error/compacting)`, `ITuiPlugin` (override-before-builtin), `IInteractiveTuiRenderer/SetSlashHandler/RunInteractiveAsync`, `GfmTable*`.
- `src/Harbor.Tui.AnsiPlain/`: `TerminalQrRenderer` (QR half-block), plain-фолбэк.
- `src/Harbor.Tui.NickConsoleEx/`: `ConsoleWindowSystem/MarkupControl`, `ForceRender()`, `ToolCallId→ToolName` трекинг.
- `src/Harbor.Tui.Notifications/`: `INotificationBackend` (Linux notify-send / macOS osascript / Windows Toast / Null) на `AgentError/AgentEnd/CompactionCompleted/ToolExecutionEnd(fail)`. В CellForge только OSC99/OSC777/OSC52/OSC1337-форматтеры без OS-бэкендов.
- `contrib/tui/Harbor.Tui.SpectreTui/`: `Help/TodoList/DiffPreview/FileTree/TokenBreakdown/Diagnostics/Logs` панели + `PanelRegistry/PanelViewProjector/PanelLayoutShell/SeedPanels`, `ChatViewProjector/DefaultUiProjector/TuiEffectHost`, TEA-цикл `Viewport/HistoryMeasured/ScrollResetToTail/ScrollClamp`, `ChatKeyMap/HandlePanelAction`.
- `contrib/tui/{Termina,TerminalGui,RazorConsole}/`: `SessionSidebar/CommandPalette(+State)/DiagnosticsView/ChatView/InputView/StatusBarView`, `*MarkdownRenderer+*ColorMapper+GfmTable`, `KeyHandler/ScrollHandler`, `*TeaBridge:+IDiagnosticsPanel/DumpDiagnostics()`, `*UiViewport:IUiViewport`.
- `contrib/tui/Harbor.Tui.Spectre.Fullscreen/`: `ChatState/InputState/ScrollManager`, `LayoutBuilder`, `MouseHandler`.
- `contrib/tui/Harbor.Tui.Sixel/`: Sixel-бэкенд (в CellForge только `ImageBlock/JpegProbe/InlineImageProbe` без Sixel).
- `src/Harbor.Hosting/Modules/TuiModule.cs`: регистрация рендереров, `HARBOR_TUI` резолв, onboarding-gate `/setup`.

### 0.4. Цель: полный feature-rich TUI как opencode/crush/claude code/codex (подтверждено)

Целевой UX = cxagent/ConsoleEx-мотивация (`nickprotop/ConsoleEx` = композитный cell-diff движок, `nickprotop/cxagent` = агентный TUI: очередь сообщений при running-ходе, Esc-стоп с возвратом очереди в композер, F1 help, F3 session-панель show/hide/auto, F4 фокус в композер, F9 тема, Shift+Tab цикл edit-режима, Ctrl+Q выход, `/mode`, `/stats`, session-панель с parent/worker токенами) + паритет opencode/crush/claude/codex:

| Фича класса opencode/crush/claude/codex | Эталон в репо | Статус CellForge |
|---|---|---|
| Chat timeline + streaming markdown + thinking-блок | `UiState.Lines/Active/PendingStream`, `StreamingMarkdownRenderer` | ⚠️ частичный (`CSB/SB/VCT` свои, `ProjectStateIntoWidgets` режет транскрипт/мышление) |
| Композер: мультлиния, история, очередь при running, Esc-стоп | `InputModel`, `TuiEffectHost(PromptAgent/AbortAgent)`, cxagent Enter/Esc-семантика | ⚠️ свой `PromptBuffer/ComposerController/PromptHistory` в обход стора |
| Slash-команды + палитра (Ctrl+P) | `ChatCommands.Slash`, `CPV`, `HelpPanel` | ⚠️ своя `CPV`, не на `OverlayStack/OverlayIds` |
| Панели: help/logs/diagnostics/diff/file-tree/todo/tokens (+sessions) | `contrib/tui/Harbor.Tui.SpectreTui/Panels/Builtin/*` (7 шт., контракт `IPanelProvider`) | ❌ свои `SBV/CPV`, билтины не зарегистрированы |
| Layout: Left/Right/Top/Bottom стеки, таб-стрип, focus/resize | `PanelLayoutShell + PanelViewProjector + PanelRegistryView` | ❌ свой `LayoutTree/CSL`, без `SeedPanels` |
| Approval/permissions гейт | `ApprovalGateView`, `CSB:RequestApprovalGate/...` | ⚠️ свой, не через `OverlayStack` |
| Сессии: switcher, slots, git-привязка | `Sessions/*`, `QuickSwitchSlots` | ❌ один `new UiStore()`, слоты свои |
| Диагностика/логи, темы live-reload, тосты, диалоги, file-picker, git-статус | `Services/*`, `IDiagnosticsPanel`, `IThemeService` | ❌ свои `JsonThemeLoader/ThemeFileWatcher` |
| OS-нотификации, clipboard OSC52, images (kitty/Sixel/OSC1337) | `Tui.Notifications/*`, `Osc*`, `ImageBlock` | ⚠️ только форматтеры, без бэкендов/Sixel |
| Keys: Alt+1..9 слоты, Ctrl+Tab фокус, Ctrl+Up/Down ресайз, F12 логи, ? help | `ChatKeyMap`, `HelpPanel`, `LogsPanel` | ⚠️ размазано по `LeaderKeyRouter/CPV/ComposerController` |

### 0.5. Референс интеграции (обязательный): `contrib/tui/Harbor.Tui.SpectreTui/Panels/`

Правило: **панели — единственный путь к одинаковым возможностям во всех рендерах**. Контракт `src/Harbor.Ui.Framework.State/Panels/`:
`IPanelProvider (Id/Title/DefaultPlacement/DefaultSize/Build(PanelContext):object?/OnKey(UiKey):bool)` — pure `Build` только при `Visible/Focused/Pinned`, мутации только через `UiStore.Dispatch`, thread-safe (build на render-потоке / onkey на input-потоке);
`PanelContext = record (UiState, Width, Height, Services)` (DI: `UiStore/IPanelRegistry/IDiagnosticsPanel/ChatKeyMap`);
`IPanelRegistry (Register/Unregister/Get, in-place замена с сохранением Alt+N слота)` → `PanelRegistryView (GetVisible/GetVisibleByPlacement/GetState/GetSize)`;
TEA-переходы только `UiMsg.TogglePanel/FocusPanel/CyclePanelFocus/ResizePanel` через `UiReducer` (`PanelStates/PanelSizes/FocusedPanelId`), хоткеи `ChatKeyMap (Alt+1..9/Ctrl+Tab/Ctrl+Up-Down)`, `ITuiPanelPlugin.RegisterPanels` для плагинов.
7 билтинов и их источники: `help(Right/48, статика+registry.All+ChatCommands.Slash, ?→TogglePanel)`; `logs(Bottom/10, IDiagnosticsPanel.GetRecent, F12→TogglePanel)`; `diagnostics(Bottom/10, Lines→ToolResult/Error+5 Regex, j/k локально)`; `diff-preview(Bottom/12, Lines пары Tool+ToolResult TrackedTools={edit,write,read,patch}, non-interactive)`; `file-tree(Left/32, напрямую filesystem + кэш _entries, j/k/h/r/Enter→Dispatch Submit)`; `todo-list(Right/40, Lines последний ToolResult с [ ]/[~]/[x] Regex, non-interactive)`; `token-breakdown(Bottom/10, Cost кумулятив, бары █/░)`.
`PanelLayoutShell.Ensure(state, streaming)` (сигнатура visible-set/size/streaming, shape Root→Header/Body[Left|Centre[Top/Header/History/StreamBar/Bottom/Input/Footer]|Right]/Input/Footer, BuildStack с таб-стрипом 1 row) + `PanelViewProjector.BuildWidgets(historyHeight, state)` (chat-регионы + `{Stack}.Tabs/{Stack}.{PanelId}`, `PanelContext(state,80,h)`, Placeholder/ErrorPlaceholder).
Вывод для CellForge: **не портировать построчно, а зарегистрировать те же 7 провайдеров в CellForge-`PanelRegistry` + написать один cell-адаптер `IPanelProvider→cell-буфер`**; чистые экстракторы (`Diagnostics.Collect/ExtractTodos/ExtractRecentChanges`) вынести в Framework чтобы кормить и Spectre, и CellForge (детали — EPIC E).

---

## Как читать бэклог

- ID: `CF-<EPIC>-<NNN>`. Порядок эпиков = рекомендуемый порядок исполнения (каждый эпик вертикально полезен сам по себе).
- Каждый таск: `Файлы` (трогать/смотреть) + `Готово когда` (acceptance).
- После каждого эпика: `dotnet build` + affected `dotnet test tests/<Project> -c Release --no-build` + `tests/Harbor.Architecture.Tests` при смене ProjectReference.
- Легенда затрагиваемых CellForge-файлов: `CF-R`=CellForgeTuiRenderer.cs, `GU`=GlobalUsings.cs, `CSB`=Streaming/ChatScreenBridge.cs, `IASB`=Streaming/InlineAgentStreamBridge.cs, `SB`=Streaming/StreamBlock.cs, `SS`=Streaming/ScreenSession.cs, `CTP`=Streaming/CommitTickPacer.cs, `VCT`=Widgets/VirtualizedChatTimeline.cs, `CSL`=Widgets/ChatScreenLayout.cs, `SBV`=Widgets/SideBarView.cs, `CPV`=Widgets/CommandPaletteView.cs.

---

## EPIC A — Каркас: зависимости, мета-пакет, доки, arch-тесты (фундамент всего остального)

- [x] `CF-A-001` Прямые ProjectReference в csproj: `Projection` + `Terminal.Abstractions` (использовались транзитивно) + `Services/ViewModels/Sessions/Ui.Framework.Abstractions` (поверхность эпиков B-K); матрица `FullLayerMatrixTests.cs:222-229` расширена (+4, Presentation→Presentation/Domain легитимны); неиспользуемый `Desktop.Abstractions` удалён из csproj; stale-комментарий `ConsoleEx must stay trim` → `CellForge must stay trim`. `TuiModule.cs` править не пришлось (cellforge уже first-class, consoleex→cellforge алиас есть). Сборка/тесты — за координатором после всех правок.
- [ ] `CF-A-002` Переключить потребителя на мета-пакет там где уместно: оценить замену 2 существующих референсов (`Rendering`, `State`) + новых на один `Harbor.Ui.Framework` (+ отдельно `Harbor.Ui.Framework.Rendering`, т.к. он НЕ входит в мета-пакет — см. аудит). Файлы: оба `.csproj`, `src/Harbor.Ui.Framework/README.md`. Готово когда: решение зафиксировано в README CellForge, сборка зелёная.
- [x] `CF-A-003` README CellForge: `# Harbor.Tui.CellForge`, `HARBOR_TUI=cellforge` везде, таблица 5→7 подсистем (файлы сверены с диском), callout про историческое имя ConsoleEx + legacy-алиас + `NickConsoleEx`-дисклеймер, JSON-ключ `ui.consoleEx` оставлен (миграция вне скоупа), тесты `Harbor.Tui.CellForge.Tests/PtyTests`, golden-путь подтверждён (11 файлов).
- [ ] `CF-A-004` Задокументировать целевую архитектуру: `UiStore → DefaultUiProjector → UiScreenModel → CellForge painters → DiffEngine → AnsiWriter`. Диаграмма + запрет рисовать напрямую из `AgentEvent` в обход стора (с исключениями: live-токены через pacer). Файлы: README CellForge + `docs/ARCHITECTURE.md` ссылка. Готово когда: любой новый виджет можно классифицировать как projector или painter по доке.
- [ ] `CF-A-005` Инвентаризация дубликатов: таблица «CellForge-свой vs Framework-общий» (StreamingMarkdownBlock vs StreamingMarkdownRenderer, ToolCallBlock vs ToolCallViewModel, JsonThemeLoader vs IThemeService, QuickSwitchSlots vs SessionSwitcher, SideBarView vs SessionSidebarView, CommandPaletteView vs Spectre/Termina палитры, DiffEngine vs DifferentialRenderPipeline, CellForgeDiffEncoder vs RowHashDiffEncoder). Для каждого — решение keep/port/delete. Готово когда: таблица в этом файле (§Приложение) заполнена, дубли без решения = 0.

## EPIC B — `ProjectStateIntoWidgets`: полная проекция UiState (самый жирный разрыв)

Контекст: `CF-R:116-132` маппит 6 полей, теряет транскрипт/мышление/скролл/панели/сессии/фокус.

- [x] `CF-B-001(part)+CF-B-003a` Точечно замапплено в `ProjectStateIntoWidgets`: `Provider→StatusBarViewModel.Provider`, `AgentName→Agent`, `Active.ThinkBuffer→ThinkingText + IsThinking(=Length!=0)`. Остаток B-001 (`IsAgentRunning/WasRunning, ContextTokens/Window/Pct/Formatted, SetModel`) — в CF-D-002/проекторы.
- [ ] `CF-B-002` Спроецировать транскрипт: `UiState.Lines → ChatHistoryViewModel.Entries` (+ `MessageEnd`-фолд сейчас теряется). Файлы: `CF-R`, `CSB`, `VCT`, `src/Harbor.Ui.Framework.State/State/UiState.cs`, `src/Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs:120-220`, `Views/ChatHistoryView.cs`. Готово когда: завершённые сообщения переживают конец стрима и видны в ленте (регрессия покрыта тестом).
- [ ] `CF-B-003b` Дотянуть мышление до ленты: `PendingStreamThink → StreamBlock/VCT` (хвост, недосброшенные чанки мышления; сам `ThinkBuffer→ThinkingText/IsThinking` уже в CF-B-003a). Файлы: `SB`, `VCT`, `CSB`. Готово когда: thinking-дельты не смешиваются с обычным текстом, dim+italic стиль сохранён (`CF-R:106-108`).
- [ ] `CF-B-011` Проводка VM под проекцию (нужна для B-005/B-006/B-008/B-009 — цели отсутствуют): завести в `CellForgeTuiRenderer` `InputViewModel` (+ новая SessionList-VM для `Sessions/ActiveSessionId/IsLoading`; скролл — через стор напрямую в `VCT/TimelineLayoutCache`, VM не требуется). Изменение ctor, делать после CF-F-001. Файлы: `CF-R`, `src/Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs`.
- [ ] `CF-B-004` Спроецировать недосброшенные чанки: `PendingStreamText/ChunkedBuffer+StreamingSync → StreamBlock` очередь. Файлы: `CF-R`, `SB`, `CTP`. Готово когда: при burst-стриме нет потери хвоста (тест: burst 100 чанков → все в ленте).
- [x] `CF-B-005` История через стор: Up/Down → `InputMsg.HistoryUp/Down` (`TryRecallViaStore`), draft save/restore, границы, cap 50 (`DefaultCapacity`, `TODO` персист), `PushSubmitted()` choke point, `IsRecalling`. Тесты: `InputHistoryTests.cs`. Проверено в волне (870/870).
- [ ] `CF-B-006` Спроецировать скролл: `ScrollOffset/ViewportLines/TotalLines/ScrollPercent → VirtualizedChatTimeline + TimelineLayoutCache`. Файлы: `CF-R`, `VCT`, `Widgets/TimelineLayoutCache.cs`, `Widgets/TimelineRing.cs`. Готово когда: tail-follow/unpin и процент скролла берутся из стора, а не локального состояния.
- [ ] `CF-B-007` Спроецировать панели: `PanelStates/PanelSizes/RegisteredPanelIds/FocusedPanelId → LayoutTree + CSL`. Файлы: `CF-R`, `Rendering/LayoutTree.cs`, `CSL`, `src/Harbor.Ui.Framework.State/Panels/*`. Готово когда: toggle/focus/resize панели меняют `LayoutTree` через стор (сейчас панели — локальный конструкт).
- [ ] `CF-B-008` Спроецировать сессии: `Sessions/ActiveSessionId/IsLoading → SideBarView + QuickSwitchSlots`. Файлы: `CF-R`, `SBV`, `Widgets/QuickSwitchSlots.cs` (см. Epic I). Готово когда: сайдбар и слоты читают сессии из стора.
- [ ] `CF-B-009` Спроецировать фокус: `Focus:FocusMode + FocusedPanelId → FocusRouter`. Файлы: `CF-R`, `Input/FocusRouter.cs`. Готово когда: Tab/Alt+1..9 меняют `FocusMode` в сторе, роутер только исполняет.
- [ ] `CF-B-010` Обработать `ShouldQuit`: `UiState.ShouldQuit → graceful shutdown` (сейчас Ctrl+C/EOF логика только в композере). Файлы: `CF-R`, `Rendering/ComposerController.cs`. Готово когда: `UiMsg.Quit` завершает REPL через единый путь.

## EPIC C — TEA-цикл: UiMsg/UiReducer/TuiEffectHost (убрать прямой рендер из AgentEvent)

Контекст: `CF-R:92-96` шлёт только `_store.Dispatch(AgentEvent)`; ветки `KeyInput/Viewport/HistoryMeasured/Toggle/Focus/Cycle/Resize/Scroll/Input/SeedPanels` из `UiMsg.cs` + `UiReducer.Update` не задействованы.

- [ ] `CF-C-001` Ввод через стор: `TerminalInputSource → InputEvent → UiMsg.KeyInput (ChatAction/UiKey) → UiReducer` вместо прямого вызова `ComposerController`. Файлы: `Input/TerminalInputSource.cs`, `Input/InputEvent.cs`, `Rendering/ComposerController.cs`, `src/Harbor.Ui.Framework.State/State/UiMsg.cs`, `UiReducer.cs`, `ChatKeyMap.cs`, `ChatAction.cs`, `UiKey.cs`. Готово когда: key-routing покрыт тестом на уровне `UiMsg`, композер — чистый исполнитель.
- [ ] `CF-C-002` Вьюпорт через стор: resize → `UiMsg.Viewport + HistoryMeasured` → пересчёт `TimelineLayoutCache`. Файлы: `Input/ResizeSignal.cs`, `SS` (resize-политика ED при shrink), `VCT`, `Widgets/TimelineLayoutCache.cs`. Готово когда: ресайз не рвёт ленту (тест shrink→grow).
- [ ] `CF-C-003` Скролл через стор: PageUp/PageDown/колесо → `ScrollResetToTail/ScrollClamp` (как в SpectreTui). Файлы: `Input/MouseEvent.cs`, `Input/MouseRouter.cs`, `VCT`, `src/Harbor.Ui.Framework.State/State/UiMsg.cs`. Готово когда: колесо/клавиши скролла идут через `UiMsg`, tail-follow логика одна.
- [ ] `CF-C-004` Панельные экшены через стор: `TogglePanelSlot/CyclePanelFocus/ResizeGrow-Shrink/Help/Logs-F12` по образцу `SpectreTuiRenderer.HandlePanelAction`. Файлы: `CSL`, `Rendering/LayoutTree.cs`, `src/Harbor.Ui.Framework.State/State/UiMsg.cs` (`Toggle/Focus/Cycle/ResizePanel/SeedPanels`). Готово когда: хоткеи панелей работают через стор, `LayoutTree` — проекция `PanelStates`.
- [ ] `CF-C-005` Сабмит через стор: `ClassifySubmit + TransitionAbort` из `UiReducer` (Enter=submit, Shift/Alt+Enter=newline сейчас в `ComposerController` напрямую). Файлы: `Rendering/ComposerController.cs`, `Rendering/VimComposerMode.cs`, `src/Harbor.Ui.Framework.State/State/UiReducer.cs`. Готово когда: kitty/non-kitty Enter-семантика определяется редуктором, композер не ветвится сам.
- [ ] `CF-C-006` Эффекты через `TuiEffectHost`: `PromptAgent/RunSlash/AbortAgent/QuitApp` вместо разрозненных вызовов. Файлы: `src/Harbor.Ui.Framework.State/State/TuiEffectHost.cs`, `UiStore.cs` (`TuiEffect`, `ITuiEffectRunner`), `CSB`, `IASB`. Готово когда: Ctrl+C abort, `/slash`, quit — через эффекты, покрыты тестами.
- [ ] `CF-C-007` Подключить `EventBusAppStoreDispatcher` (`Harbor.Ui.Framework.Services`): единый мост `IEventBus → AppStore/UiStore` вместо ручных подписок в `CSB`. Файлы: `CSB`, `src/Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs`. Готово когда: дублирующиеся `bus.Subscribe` в бриджах удалены.

## EPIC D — Projection: DefaultUiProjector + UiScreenModel как единственный мост к painters

- [ ] `CF-D-001` Встроить `IUiProjector/DefaultUiProjector`: `UiState → UiScreenModel (Header/Transcript/Blocks/MessageBlocks/Input/StatusBar)`; CellForge painters рисуют из `UiScreenModel`, а не из `UiState` напрямую. Файлы: `CF-R`, `VCT`, `CSL`, `SBV`, `src/Harbor.Ui.Framework.Projection/Projection/{DefaultUiProjector,IUiProjector,UiScreenModel}.cs`. Готово когда: `grep "UiState\."` в `Widgets/` = 0 (всё через `UiScreenModel`).
- [ ] `CF-D-002` Подключить `StatusProjector` (`ProjectStatusBar/ProjectFooter`, глифы `▌◐✗○`, scroll live/%): заменить ручной `StatusPanel` в `CSL:130-136`. Файлы: `CSL`, `Widgets/SpinnerStrip.cs`, `Widgets/RetryCountdown.cs`, `src/Harbor.Ui.Framework.Projection/Projection/StatusProjector.cs`. Готово когда: статус-футер и спиннер/ретрай идут из проектора, golden-тест совпадает.
- [ ] `CF-D-003` Подключить `ChatStreamingPresenter.DeriveStatus`: единый статус стрима для `CSB/IASB/SB`. Файлы: `CSB`, `IASB`, `SB`, `src/Harbor.Ui.Framework.Projection/Rendering/ChatStreamingPresenter.cs`. Готово когда: live-статусы alt-screen и inline путей идентичны.
- [x] `CF-D-004` `Rendering/CellForgeViewport.cs` (`IUiViewport`: Width/Height/ViewportLines/ScrollOffset/TotalLines/FollowTail, `FromConsole()` с фолбэком 80×24, `Apply(UiScreenModel)` zero-alloc, скролл-семантика 1-в-1 с VCT) + `CellForgeViewportTests.cs` (17). Wiring в CF-B-006/CF-C-002 — отдельно. На проверке у координатора.
- [ ] `CF-D-005` Стилизованные строки: `UiRenderedLine/UiLineKind/StyledSpan → AnsiWriter/CellStyle` маппинг (переиспользовать `CF-R:300-310 MapStyle`). Файлы: `CF-R`, `Rendering/AnsiWriter.cs`, `Rendering/Graphics.cs`, `src/Harbor.Ui.Framework.Projection/Projection/{UiRenderedLine,StyledSpan,UiLineKind}.cs`. Готово когда: таблица стилей одна, дублирующихся мапперов нет.

## EPIC E — Панели: PanelRegistry + 7 билтинов + добор из Avalonia/Framework (расширен по инвентаризации, TOP-1 #27)

> Принцип (подтверждён инвентаризацией всего репо): продакшн-`IPanelProvider` всего 7, и все — в `contrib/tui/Harbor.Tui.SpectreTui/Panels/Builtin/` (других реализаций нет, только тест-стабы и примеры в доках). Поэтому билтины НЕ портируем построчно, а **регистрируем те же провайдеры** через `CellForgePanelRegistry + EnsureSeeded` и рисуем через `CellForgePanelAdapter.RenderToRows/RouteKey`; чистые экстракторы (`ExtractTodos/ExtractRecentChanges/CollectDiagnostics`) выносим в `Harbor.Ui.Framework` чтобы кормить и Spectre, и CellForge. Всё сверх 7 — готовые Framework-блоки и Avalonia-референсы ниже.

- [x] `CF-E-001` Инфраструктура приёма: `Panels/CellForgePanelRegistry.cs` (владелец `PanelRegistry`, `Register()`, `EnsureSeeded(UiStore): TuiEffect` с preserve-правилом как `SpectreTuiRenderer.SeedPanelRegistryIntoState`) + `Panels/CellForgePanelAdapter.cs` (`RenderToRows(provider,ctx)/RouteKey`, `object?→строки`, без Spectre-зависимостей). `grep SeedPanels src/Harbor.Tui.CellForge` > 0 ✅. Не делает: регистрацию 7 билтинов (E-002…E-006), wiring в `CellForgeTuiRenderer`/`ChatScreenLayout` (E-007).
- [x] `CF-E-002` Cell-native провайдеры: `Panels/CellForgeBuiltinPanels.cs` (7 `IPanelProvider`, Id/Placement 1-в-1, `Build→IReadOnlyList<string>`, клиппинг, null-деградация; file-tree с lock + `TODO FP-005`) + `CellForgeBuiltinPanelsTests.cs` (24). Wiring в рендерер (EnsureSeeded/RenderToRows/RouteKey в CSL/LayoutTree) — следующий шаг.
- [x] `CF-D-002` `StatusProjectorPanel` в `ChatScreenLayout.cs` (`BuildSegments` из `StatusProjector`, глифы/форматы через `StatusMappers`, ноль прячется, truncation-порядок) + `StatusProjectorPanelTests.cs` (14). Проверено: CellForge 870/870.
- [x] `CF-E-003` Экстракторы в Framework: `Projection/PanelExtractors.cs` (`ExtractTodos/ExtractRecentChanges/CollectDiagnostics`, рекорды `TodoItem/PanelFileChange/PanelDiagnostic`, поведение 1-в-1) + 3 билтина переведены на делегирование (только рендер остался). Тесты: `PanelExtractorsTests.cs` (14). На проверке у координатора.
- [ ] `CF-E-004` Сессии — главный пробел 7 билтинов: `SessionSidebarView` (`contrib/tui/Harbor.Tui.{Termina,TerminalGui,RazorConsole}/Views/` — единственный готовый список сессий/панелей с Alt+1..9, `Build(UiState,PanelRegistry)`) + `SessionsFlyoutView/SessionListViewModel/BoardView/SessionCardView` (`apps/Harbor.App.Avalonia/Views/Shell/`, `Board/`) как UX-референс + `SessionManager/Switcher/Factory/SessionsReducer` (Framework) как бэкенд. Цель: cell-панель сессий (P0). Связано с EPIC I.
- [ ] `CF-E-005` Дифф: `DiffBlock/UnifiedDiffParser/WordDiff` (Framework, P0 — парсинг `@@` готов) + `DiffView/HdsDiffCompact/DiffPreviewHelper` (Avalonia, богатый референс) → cell `DiffPreviewPanel` поверх `ToolCallBlock` (P0). Связано с `CF-F-003` (Diff VM), `CF-L-005`.
- [ ] `CF-E-006` Статус/токены: `StatusProjector/StatusBarWidget/StatusSegmentBar` + `TokenUsageView/TokenUsageViewModel` (Avalonia) + `StatusBarView.axaml` (Avalonia Shell) → cell-футер и `TokenBreakdownPanel` (P0). Связано с `CF-D-002`, `CF-G-005`.
- [ ] `CF-E-007` Тул-коллы и апрувы в ленте: `ToolCallCardView/TypewriterStreamingText` (Avalonia Controls, P0 — карточки прямо в таймлайн) + `ApprovalGateView` (Framework `IChatBlock+IFocusTarget`, P0 — approve/deny гейт) → `VCT/ToolCallBlock/CSB` (P0). Связано с EPIC N, `CF-G-002`, `CF-L-004`.
- [ ] `CF-E-008` Палитра и провайдеры: `CommandPaletteView+FuzzySearchService+SlashCommands/BuiltInCommands` (Avalonia + `Desktop.Shared`, P0 — Ctrl+P уже есть в `Widgets/CommandPaletteView.cs`, подключить источники) + `ProviderBrowserView/ProviderModelPicker` (Avalonia, P1 — смена модели) → оверлеи через `OverlayStack/OverlayIds` (P1). Связано с `CF-H-004`, `CF-O-001`.
- [ ] `CF-E-009` Тосты/модалки/пикеры/настройки: `ToastNotificationsView/ModalHostView` (Avalonia) + `IToastService/IDialogService/IOverlayStack/IFilePicker` (Framework Services) + `SettingsView/ThemeSettingsViewModel` (Avalonia, P1) → cell-оверлеи (P1). Не переносить: `FocusSessionView/OnboardingWindow` (P2), `CodeEditorView/Monaco` (P2), `TerminalPaneControl` (P2), `ActivityRailView` (P2), `ComponentGalleryView` (P2-dev).
- [ ] `CF-E-010` Ужать `SideBarView.cs` до проекции: `SideBarState` (сессия+токены+модель+modified+LSP+MCP) маппить из `UiState.Cost/Sessions` (P0), файлы→`FileTreePanel`, токены→`TokenBreakdownPanel`; в `SBV` оставить pure-paint + `SideBarLayout.ComputeArea`. Готово когда: `SBV` без собственных источников данных.
- [ ] `CF-E-011` `DiffPreviewHelper.ExtractDiff → R/DiffPreview.cs` (P0, M): генератор контекстных диффов (`ctx=2`, трим, 6/80 строк, file-path экстрактор, сентенелы `… diff truncated`) + `ToolCallInfo{FilePath,DiffPreview,DiffFull}`. Сейчас `ToolCallBlock.DiffRenderer` только красит чужой `DiffText`. Источник: `apps/Harbor.App.Avalonia/Services/DiffPreviewHelper.cs`.
- [x] `CF-E-012` `StatusMappers` напрямую в `ToolCallBlock`: `FormatDuration`→шим над `DurationToText`, `StatusPill/StatusBrushKey` (пилюля красится только завершённым карточкам — Running-хедер заморожен golden-хэшами до EPIC S), `ChatPalette.ToolPill` — отдельная правка в DesignSystem (вне скоупа). Тесты: `ToolCallBlockMapperTests.cs` (17).
- [x] `CF-R-009` `ProjectionCoverageTests.cs` (13 кейсов, все 11 строк проекции; инжект свежих VM в ctor против удвоения через `BaseTuiRenderer.RenderAsync`; `InitializeAsync` обязателен).
- [ ] `CF-E-013` `CodeBlock.Tokenize + KeywordsFor(6 языков) → R/CodeTokenizer.cs` (P0, S): лёгкий хайлайтер (`Run`→`(span,CellStyle)`), цвета из `HarborDesignTokens` (keyword accent bold / string green / comment gray / number amber). Красит `AssistantMarkdownBlock`. Источник: `apps/Harbor.App.Avalonia/Views/Controls/CodeBlock.axaml.cs`.
- [ ] `CF-E-014` Markdown-паритет (P0, M): таблица блоков/инлайнов (`MarkdownBlockRenderer/MarkdownInlineRenderer`: H1-6 размеры/маржины, списки `•/N.`, цитаты, hr, ссылки с fallback на URL) → `Streaming/AssistantMarkdownBlock`; `MarkdownToPlainTextService.ToSummary(100)` → тосты/палитра. Источник: `Views/Controls/Markdown/*`, `src/Harbor.Desktop.Shared/Services/MarkdownToPlainTextService.cs`.
- [ ] `CF-E-015` `ChatViewModelBase`-спецификация (P0, M): Timeline (коллапс call/result в 1 карточку, позиционное переиспользование), `ParseToolLine` (иконки 10 тулз `edit✎ write✚ read▸ patch⌥ bash$ grep🔍 glob🌐 ls📁 task☐ web_fetch🌍` → ASCII/Nerd маппинг), `RoleBrushKey` (7 ролей), `InputPlaceholder`-машина (4 состояния), `UiRenderEngine`-инвариант «флаш мутирует только хвост» → `TimelineLayoutCache/TimelineRing`. Источник: `src/Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs`, `apps/Harbor.App.Avalonia/Services/UiRenderEngine.cs`.
- [ ] `CF-E-016` `HarborDesignTokens.axaml` → дефолт `JsonThemeLoader` (P0, S): hex-палитра 1-в-1 (accent `#39BAE6` / success `#7FD962` / warning `#FFB454` / error `#FF6B6B` / tool `#D2A6FF` / system `#F29668` / text/bg/panel/surface/border + роли чата + `CostLow/Mid/High`). Источник: `apps/Harbor.App.Avalonia/Themes/Hds/HarborDesignTokens.axaml`.
- [ ] `CF-E-017` Каталоги команд (P0, S): `SlashCommands.All` (10: help/clear/sessions/branch/providers/tokens/theme/editor/diff…) + `BuiltInCommands.Templates` (10 палитр) → палитра/композер, icon-ключи → Nerd Font. Источник: `src/Harbor.Desktop.Shared/Commands/`.
- [ ] `CF-E-018` Хоткеи и оверлеи (P0, S): `KeyboardShortcutService` (8: `Esc/Ctrl+P/B/Shift+T/O/S/L/backtick`) → `LeaderKeyRouter/VimComposerMode`; Z-order 500–1000 + backdrop-правило (`e.Source==sender`) + layout-константы (280/360/480/640/760/800/900) + форматы (`↓/↑ N0`, `running · $F2`) + тост (280–380, полоса 3px) + баннеры running/streaming/thinking + курсор `▋` 530мс → `OverlayStack/LayoutTree/SpinnerStrip/StatusPanel/StreamingBanner` (новый). Источник: `Services/KeyboardShortcutService.cs`, `Views/MainWindow/ModalHostView/ToastNotificationsView/StatusBarView/ComposerView`.
- [ ] `CF-E-019` Сессионные строки и доты (P1, S): `SessionRow` (формат `Title + agent · model + RelativeTime/MessageCount/StatusColorKey/IsDirty/IsActive`) → `SideBarView`; `StatusDot/SessionDotState` таблица → статус/сайдбар; `FileTreeNode` + `FileTypeToGeometry` → `FileTreeBlock` (новый). Источник: `Views/Components/{SessionRow,StatusBadge,StatusDot}`, `MainViewModel.FileTreeNode`.
- [ ] `CF-E-020` Анимации и мелочи (P1, S): `EasingFunctions` (pure math) + `AnimationTokens` (150/300мс, entrance `150ms/8px`, pulse `1s/0.3`) → `SpringFx/PostFx/CommitTickPacer` (детерминированно по кадрам); `FuzzySearchService.CamelHump+15` → `FuzzyMatcher`; `RecentItemsService` (MRU 50, путь `~/.harbor/cellforge/recent.json`, не GUI-шный) → `PromptHistory` персист. Источник: `src/Harbor.Desktop.Animations/*`, `Desktop.Shared/Services/{FuzzySearch,RecentItems}Service.cs`.
- [ ] `CF-E-021` НЕ тащить (зафиксировано): XAML-стили как есть; эмодзи-иконки без Nerd/ASCII-маппинга; попиксельная геометрия `DiagnosticsSquiggleRenderer` (только severity→стиль); `CodeEditorView/Monaco`, `TerminalPaneControl`, `FocusSessionView/OnboardingWindow`, `ActivityRailView`, `BoardView`, `ComponentGalleryView` (P2, вне скоупа TUI).

## EPIC F — Terminal.Abstractions: перестать обходить контракт

- [x] `CF-F-001` Заглушка `ShouldRenderPlacement` удалена — все 4 builtin views красятся через базовый фильтр; `ChatHistoryView/InputView` пишут через `CellForgeRenderContext/AnsiWriter`. Golden `cellforge.golden.txt` покраснеет ожидаемо — перегенерация только в EPIC S.
- [x] `CF-B-011` Проводка VM: `InputViewModel` в ctor обоих конструкторов (дефолт `ViewModels.Get("input")`), двусторонний биндинг с `ComposerController.Buffer` (`SyncInputFromState` + `OnInputVmChanged`, флаг `_syncingInput`); `Sessions/ActiveSessionId/IsLoading` → снапшот-поля (`TODO(CF-B-008)` на проводку в SideBarView/QuickSwitchSlots); история `InputModel.History` не тронута (это CF-B-005). Тесты: `RendererWiringTests.cs` (7). Сборка/тесты — на проверке у координатора.
- [ ] `CF-F-002` Запитать `InputViewModel (Text/Cursor/Placeholder/Submit/Cancel)`: двусторонний биндинг с `PromptBuffer/ComposerController` (см. CF-B-005). Файлы: `Rendering/ComposerController.cs`, `Rendering/PromptBuffer.cs`, `src/Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs`. Готово когда: ввод через VM, прямой доступ к буферу из вне — только через VM.
- [ ] `CF-F-003` Запитать `DiffPreviewViewModel (Diffs/Current/NextDiff/PreviousDiff, эвристика Wrote/Edited)`: кормить из tool-result пайплайна (`ToolCallBlock.ToolResultBody`). Файлы: `Widgets/ToolCallBlock.cs`, `CSB`, `src/Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs`, `Views/DiffPreviewView.cs`. Готово когда: Overlay `Diff N of M + n/p` показывает реальные диффы.
- [ ] `CF-F-004` Реализовать `IInteractiveTuiRenderer/SetSlashHandler/RunInteractiveAsync`: перенести интерактив из разрозненных `CSB/SS/IASB/CTP` в хост-цикл. Файлы: `CF-R`, `CSB`, `SS`, `IASB`, `src/Harbor.Terminal.Abstractions/ITuiRenderer.cs`. Готово когда: `grep RunInteractiveAsync src/Harbor.Tui.CellForge` > 0; `ReadLineAsync:Console.ReadLine()` (`CF-R:141-148`) удалён как интерактивный путь (оставить только для non-interactive).
- [ ] `CF-F-005` Поддержка `ITuiPlugin` (override-before-builtin по Id): кастомные views по `status-bar/chat-history/input/diff-preview` должны подхватываться (сейчас Chat/Input жёстко отрублены). Файлы: `CF-R`, `src/Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs`, `ViewRegistry.cs`. Готово когда: тест «плагин переопределяет input view» зелёный.
- [ ] `CF-F-006` GFM-таблицы: использовать `GfmTable*/GfmTableParser/GfmTableFormatter` из `Terminal.Abstractions.Rendering` в `AssistantMarkdownBlock/StreamingMarkdownBlock` вместо своего рендера таблиц (если есть). Файлы: `Widgets/{AssistantMarkdownBlock,StreamingMarkdownBlock}.cs`. Готово когда: GFM-таблица из LLM рисуется общим форматтером, golden-тест добавлен.

## EPIC G — ViewModels (Harbor.Ui.Framework.ViewModels): выкинуть дубли

- [ ] `CF-G-001` `ChatLineViewModel` вместо самодельных блоков транскрипта: `VCT` + `CSB` строят ленту из VM. Файлы: `VCT`, `CSB`, `src/Harbor.Ui.Framework.ViewModels/ViewModels/ChatLineViewModel.cs`, `StoreSubscriberViewModel.cs`.
- [ ] `CF-G-002` `ToolCallViewModel + ToolCallStatus` вместо `ToolCallBlock` статусов `Running/Ok/Error`: унифицировать с `NickConsoleEx` трекингом `ToolCallId→ToolName`. Файлы: `Widgets/ToolCallBlock.cs`, `CSB`.
- [ ] `CF-G-003` `DiffViewModel/DiffRowViewModel` для diff-панелей (см. CF-E-004/CF-F-003). Файлы: `Widgets/DiffPreviewPanel.cs` (новый), `Widgets/ToolCallBlock.cs`.
- [ ] `CF-G-004` `SessionRowViewModel/SessionItemViewModel` для `QuickSwitchSlots + SideBarView` (см. Epic I). Файлы: `Widgets/QuickSwitchSlots.cs`, `SBV`.
- [ ] `CF-G-005` `TokenUsageViewModel/TokenUsageBarViewModel` для статусбара/TokenBreakdownPanel. Файлы: `CSL` (`StatusPanel`), `Widgets/TokenBreakdownPanel.cs` (новый).
- [ ] `CF-G-006` `StatusMappers` конвертеры + `CostAnimator` анимация стоимости в статусбаре (вместо ручного кода в `CF-R:116-132`). Файлы: `CF-R`, `CSL`.
- [ ] `CF-G-007` `StoreSubscriberViewModel` как базовый класс подписки на `UiStore` для всех CellForge VM (убрать ручные `Changed +=` в `CF-R:77,87-90,135-139`). Готово когда: подписок вручную = 0.

## EPIC H — Services (Harbor.Ui.Framework.Services): тосты/диалоги/темы/оверлеи/git

- [ ] `CF-H-001` `IToastService/ToastKind/ToastNotification`: тосты для `ToolExecutionEnd(fail)/CompactionCompleted/AgentError` поверх ленты (сейчас только OSC-уведомления). Файлы: новый `Widgets/ToastOverlay.cs`, `CSB`, `src/Harbor.Ui.Framework.Services/Services/IToastService.cs`.
- [ ] `CF-H-002` `IThemeService`: удалить велосипед `Widgets/JsonThemeLoader.cs + ThemeFileWatcher.cs` (свой merge + polling 500мс через `TerminalColorPalette.Apply`) в пользу общего сервиса; `ThemeJson/HarborTheme` из `Harbor.DesignSystem` оставить как модель. Файлы: `Widgets/JsonThemeLoader.cs`, `Widgets/ThemeFileWatcher.cs`, `src/Harbor.Ui.Framework.Services/Services/IThemeService.cs`. Готово когда: live-reload тем идёт через сервис, дубли удалены.
- [ ] `CF-H-003` `IDialogService (Confirm/Prompt/Alert)`: модалки поверх alt-screen (сейчас `/setup` печатает подсказку вместо ввода — см. README ограничения). Файлы: новый `Widgets/DialogOverlay.cs`, `CSB`.
- [ ] `CF-H-004` `IOverlayStack/OverlayStackService + OverlayController + OverlayIds`: единый стек `palette/settings/diff/tokenUsage/providerBrowser/modelPicker/sessionsFlyout` для `CommandPaletteView/DialogOverlay/DiffPreview/TokenBreakdown`. Файлы: `CPV`, новые оверлеи, `src/Harbor.Ui.Framework.Abstractions/Navigation/OverlayIds.cs`. Готово когда: оверлеи не знают друг о друге, только про стек.
- [ ] `CF-H-005` `IFilePicker (PickFiles/PickSave/PickFolder)`: файловый пикер для `FileTreePanel` и аттачей в композер. Файлы: `Widgets/FileTreePanel.cs` (новый), `Rendering/ComposerController.cs`.
- [ ] `CF-H-006` `GitService/GitSessionInfo/SessionStatusTracker`: гит-статус (branch/dirty) в `SideBarView/StatusPanel` вместо ручного парсинга (если есть). Файлы: `SBV`, `CSL`, `src/Harbor.Ui.Framework.Services/Services/GitService.cs`.
- [ ] `CF-H-007` `IDispatcherAdapter + IRenderEngine`: ввод/рендер через адаптеры (testability, headless-драйвер по образцу `NickConsoleEx: HeadlessConsoleDriver`). Файлы: `Input/TerminalInputSource.cs`, `SS`, `CF-R`.

## EPIC I — Sessions (Harbor.Ui.Framework.Sessions): мультисессии вместо одного UiStore

- [ ] `CF-I-001` `SessionManager/SessionFactory/SessionContext/ISessionManager`: per-session `UiStore`, `New/Open/Branch/Delete/Rename/EnsureDefault`, реплей `GetMessagesAsync → UiMsg.AppendLine`, `ClearTokenUsage`. Заменить `new UiStore()` в `CF-R:41,55` на фабрику. Файлы: `CF-R`, `CSB`.
- [ ] `CF-I-002` `SessionSwitcher.OpenAsync(session, targetStore)` для переключения без потери ленты/скролла; `QuickSwitchSlots` (9 слотов, slot0 pin, `leader+1..9`) перевести на свитчер. Файлы: `Widgets/QuickSwitchSlots.cs`, `Widgets/LeaderKeyRouter.cs`.
- [ ] `CF-I-003` `SessionGitTracker`: привязка сессии к ветке (вместе с CF-H-006). Файлы: `SBV`, `src/Harbor.Ui.Framework.Sessions/Sessions/SessionGitTracker.cs`.
- [ ] `CF-I-004` `IChatViewBinder`: гидратация `VCT` из сессии (вместо ручного реплея `AgentStart→replay` в `CSB`). Файлы: `CSB`, `VCT`.
- [ ] `CF-I-005` `ICommonConfigReader` (`Abstractions/Configuration`): `RebindFromCommonConfig` при смене провайдера/модели. Файлы: `CF-R`, `CSB`.

## EPIC J — Reducers-legacy: решить судьбу AppStore-ветки

- [ ] `CF-J-001` Аудит `Harbor.Ui.Framework.Reducers/{AppStore,AppReducer,ChatViewReducer,ChromeReducer,SessionsReducer}` + `State/{AppState,ChatViewState,ChromeViewState,SessionsViewState}`: используются ли GUI-хостами (Avalonia/WPF/Maui/Blazor в `contrib/apps`), или мёртвый код после переезда на `UiStore/UiState`. Файлы: `grep AppStore|AppReducer contrib src apps`. Готово когда: решение зафиксировано — (а) удалить, (б) оставить для GUI и НЕ тащить в CellForge, (в) бриджевать.
- [ ] `CF-J-002` (если в) Тонкий бридж только при необходимости: `AgentEvent → AppStore + UiStore` без двойного состояния. Не делать по умолчанию.

## EPIC K — Abstractions: shell-chrome, навигация, диагностика, конфиг

- [ ] `CF-K-001` `OverlayIds + IShellChrome/IContentHost/IWorkspaceCommands`: CellForge как шелл-хост (открыть палитру/настройки/diff/tokenUsage/providerBrowser/modelPicker/sessionsFlyout по ID, а не по прямым ссылкам на виджеты). Файлы: `CPV`, новые оверлеи, `src/Harbor.Ui.Framework.Abstractions/Navigation/*`.
- [ ] `CF-K-002` `IDiagnosticsPanel/InMemoryDiagnosticsPanel/DiagnosticEntry/DiagnosticsPanelLoggerProvider`: лог-панель + `DumpDiagnostics()` по образцу `*TeaBridge` в Termina/TerminalGui/RazorConsole. Заменить ad-hoc логирование в `Input/TerminalInputSource.cs`, `Parsing/EscapeSequenceParser.cs`, `Rendering/DiffEngine.cs`. Файлы: `Widgets/DiagnosticsPanel.cs` (см. CF-E-003).
- [ ] `CF-K-003` `ITuiPanelPlugin`: сторонние панели как плагины (вместе с CF-F-005). Файлы: `src/Harbor.Ui.Framework.State/Panels/ITuiPanelPlugin.cs`.

## EPIC L — Rendering-конвергенция: убрать форки пайплайнов

- [ ] `CF-L-001` `DifferentialRenderPipeline` (Framework.Rendering) vs `DiffEngine + BufferSwapChain + ScreenBuffer` (CellForge): выбрать один diff-путь, второй — адаптер. Файлы: `Rendering/DiffEngine.cs`, `Rendering/BufferSwapChain.cs`, `src/Harbor.Ui.Framework.Rendering/DifferentialRenderPipeline.cs`, `ScreenBuffer.cs`, `Cell.cs`. Готово когда: решение + бенчмарк (`RendererPerformanceContract`) до/после.
- [ ] `CF-L-002` `RowHashDiffEncoder` vs `CellForgeDiffEncoder`: один энкодер `ICellDiffEncoder/CellDiffBatch` для remote/OTA-пути (`Harbor.Transport.Remote`, `Harbor.Ipc.*`). Файлы: `Rendering/CellForgeDiffEncoder.cs`, `src/Harbor.Ui.Framework.Rendering/Protocol/*`.
- [ ] `CF-L-003` Markdown: `StreamingMarkdownRenderer + DifferentialMarkdownPipeline + FrozenTailMarkdownCache + MarkdownBlockParser + MdModel` (Framework) vs `StreamingMarkdownBlock + AssistantMarkdownBlock + StreamBlock + CommitTickPacer` (CellForge). Цель: общий парсер/кэш замороженного хвоста, CellForge — только pacer + painters. Файлы: `Widgets/{StreamingMarkdownBlock,AssistantMarkdownBlock}.cs`, `SB`, `CTP`, `src/Harbor.Ui.Framework.Rendering/Markdown/*`. Готово когда: `FrozenTail` используется, `>300мс force-batch` поведение сохранено, перф-контракт `MarkdownRenderPerformanceContract` зелёный.
- [ ] `CF-L-004` `ApprovalGateView/ApprovalChoice` (Framework) vs `CSB:RequestApprovalGate/BeginApprovalGate/TryRouteApprovalKey/Click` (CellForge): один approval-гейт. Файлы: `CSB`, `src/Harbor.Ui.Framework.Rendering/Widgets/ApprovalGateView.cs`.
- [ ] `CF-L-005` `DiffBlock/UnifiedDiffParser/WordDiff` (Framework) vs `ToolCallBlock.ToolResultBody(output/diff, MaxBodyLines=4)` (CellForge): общий diff-рендер (см. CF-E-004/CF-F-003). Файлы: `Widgets/ToolCallBlock.cs`.
- [ ] `CF-L-006` `StatusSegmentBar/StatusViewModel/StatusBarWidget/PanelFx + MascotPhases.AgentPhase` (Framework) vs `ChatScreenLayout.StatusPanel + SpinnerStrip + MascotDirector/AmbientMascot/MascotPanel` (CellForge): статусные сегменты и фазы маскота из одного источника (`MascotDirector.Advance/Derive` сейчас читает `StatusViewModel.Mode+AgentPhase` напрямую). Файлы: `CSL`, `Widgets/{SpinnerStrip,MascotDirector,AmbientMascot,MascotPanel}.cs`.

## EPIC M — Другие TUI как доноры фич (всё, что есть в других проектах)

- [ ] `CF-M-001` `TerminalQrRenderer` (`src/Harbor.Tui.AnsiPlain`): QR half-block + quiet-zone для share/auth-URL. Новый `Widgets/QrBlock.cs` (или reuse напрямую). Готово когда: CellForge рисует QR тем же кодом, а не копией.
- [ ] `CF-M-002` Plain/Ansi фолбэк (`AnsiTuiRenderer/PlainTuiRenderer/AnsiPlainTuiRenderer`): деградация при piped-stdin/no-TTY/CI (`NullModeController` уже есть) — явный фолбэк-путь вместо молчаливой поломки. Файлы: `CF-R`, `Input/NullModeController.cs`, `src/Harbor.Hosting/Modules/TuiModule.cs`.
- [ ] `CF-M-003` OS-нотификации (`src/Harbor.Tui.Notifications`: `LinuxNotifySendBackend/MacOsascriptBackend/WindowsToastBackend/NullNotificationBackend` на `AgentError/AgentEnd/CompactionCompleted/ToolExecutionEnd(fail)`): подключить как `INotificationBackend` поверх существующих OSC99/OSC777-форматтеров. Файлы: `Rendering/{Osc99Notify,Osc777Notify,Osc52Clipboard}.cs`, `Capabilities/NotifyProbe.cs`.
- [ ] `CF-M-004` Sixel (`contrib/tui/Harbor.Tui.Sixel/SixelTuiRenderer.cs`): Sixel-бэкенд рядом с kitty-APC passthrough (`Rendering/Graphics.cs`) и OSC1337 (`Rendering/Osc1337Image.cs`); выбор по `Capabilities/InlineImageProbe`. Файлы: `Rendering/Graphics.cs`, `Rendering/Osc1337Image.cs`, `Widgets/ImageBlock.cs`, `Widgets/JpegProbe.cs`.
- [ ] `CF-M-005` `SessionSidebarView/CommandPalette(+State)/DiagnosticsView` (`contrib/tui/{Termina,TerminalGui,RazorConsole}`): сверить с CF-E/CF-K, не плодить третьи реализации. Готово когда: для каждого — reuse или явный keep с причиной.
- [ ] `CF-M-006` `ScrollManager/KeyHandler/ScrollHandler/MouseHandler/LayoutBuilder` (`Spectre.Fullscreen`, Termina/TerminalGui): перенести в `VCT/TimelineLayoutCache/Input/{MouseRouter,FocusRouter}` вместо локальных велосипедов.
- [ ] `CF-M-007` `NickConsoleEx`: `ToolCallId→ToolName` трекинг + `maxLines=500+Trim()` политика + `Escape([/])` — перенести в `CSB/TimelineRing` (сейчас `TimelineRing` — byte-budget 1МБ, строкового лимита нет). Файлы: `CSB`, `Widgets/TimelineRing.cs`.
- [ ] `CF-M-008` Clipboard: `Osc52Clipboard` (copy-on-select, cap 100k base64, SSH-safe) связать с `SelectionEngine` (press-anchor/drag-extend/release-extract) как единый copy-on-select путь. Файлы: `Rendering/Osc52Clipboard.cs`, `Widgets/SelectionEngine.cs`.

## EPIC N — Стриминг-паритет: мышление/тулы/апрувалы/ретраи/компакшн

- [ ] `CF-N-001` Полный lifecycle тулов: `ToolCallStart → delta → StepFinish → ToolExecutionStart/End (ok/fail, duration)` в `ToolCallBlock` через `ToolCallViewModel` (см. CF-G-002), включая fail-стили и ретраи (`RetryCountdown`: `retry a/b in Ns` + braille progress). Файлы: `CSB`, `Widgets/{ToolCallBlock,RetryCountdown,SpinnerStrip}.cs`.
- [ ] `CF-N-002` Approval-гейты как очередь с фокусом (см. CF-L-004): `RequestApprovalGate/BeginApprovalGate/TryRouteApprovalKey/Click` через `OverlayStack` (см. CF-H-004), а не напрямую. Файлы: `CSB`.
- [ ] `CF-N-003` События памяти/ошибок: `CompactionStarted/Completed/Failed`, `AgentError`, `SessionStats/SessionChanged` — тосты (CF-H-001) + статус (CF-D-002) + OS-нотификации (CF-M-003). Сейчас в CellForge нет явной обработки. Файлы: `CSB`, `IASB`.
- [ ] `CF-N-004` Inline-паритет: `InlineAgentStreamBridge + InlineSession + StreamBlock` довести до alt-screen паритета (`CSB + ScreenSession`): дельты/коммиты/апрувалы/композер снизу живой в обоих путях. Файлы: `IASB`, `Rendering/InlineSession.cs`, `SB`.

## EPIC O — Ввод-паритет: кеймапы, лидер-клавиши, vim, мышь, paste

- [ ] `CF-O-001` `ChatKeyMap/ChatAction/FocusMode/UiKey` как единственный словарь хоткеев (сейчас часть в `ComposerController`, часть в `LeaderKeyRouter` `Ctrl+X`-chords 1500мс, часть в Spectre `HandlePanelAction`). Файлы: `Rendering/ComposerController.cs`, `Widgets/LeaderKeyRouter.cs`, `Widgets/CommandPaletteView.cs` (Ctrl+P), `src/Harbor.Ui.Framework.State/State/{ChatKeyMap,ChatAction,UiKey}.cs`.
- [ ] `CF-O-002` `VimComposerMode` (NORMAL/INSERT `h/l/w/b/0/$/x/j/k`) согласовать с `ChatCommands`/vim-режимом других хостов (если есть) — один vim-слой, pass-through по умолчанию. Файлы: `Rendering/VimComposerMode.cs`.
- [ ] `CF-O-003` `MarkdownEditOps` (`**` wrap, `#`/`-` toggle через `PromptBuffer`) как `ChatCommands` через стор (см. CF-C-005). Файлы: `Rendering/MarkdownEditOps.cs`, `Rendering/PromptBuffer.cs`.
- [ ] `CF-O-004` Мышь: `MouseRouter` (hit-test press/release/wheel по `Rect`) + `MouseHandler/ScrollManager` доноров (CF-M-006) + `IPointerTarget`/`IFocusTarget` (`Rendering.Input`) — единый hit-test через `LayoutTree.Panel.Rect`. Файлы: `Input/{MouseRouter,MouseEvent}.cs`, `Rendering/LayoutTree.cs`.
- [ ] `CF-O-005` Paste: `PasteSanitizer` (strip ANSI/control, `ReadOnlySpan+SearchValues`) + bracketed-paste anti-injection (`PasteEvent` никогда не содержит key/mouse) связать с `InputModel` вставкой как одной атомарной опсы (а не потоком клавиш). Файлы: `Input/{PasteEvent,PasteSanitizer}.cs`, `Parsing/EscapeSequenceParser.cs`.
- [ ] `CF-O-006` `PromptViewport` (горизонтальное scroll-окно, lookahead 1 cell) + `PromptRenderer` (slice/placeholder/caret `▓` без DECTCEM per-frame) перевести на `InputViewModel.Cursor/Placeholder` (см. CF-F-002). Файлы: `Rendering/{PromptViewport,PromptRenderer,PromptBuffer}.cs`.

## EPIC P — Хостинг/DI: регистрация, конфиг, onboarding

- [ ] `CF-P-001` `src/Harbor.Hosting/Modules/TuiModule.cs`: `cellforge` как first-class бэкенд (`HARBOR_TUI=cellforge`, `config.json {tui}`, `cli.json {defaultTuiRenderer}`), kill-switch `ui.consoleEx.enabled=false → fallback legacy` (см. README CellForge), Windows raw-mode fallback (README: `WindowsVtModeController` бросает → авто-откат). Готово когда: матрица выбора рендера покрыта тестами.
- [ ] `CF-P-002` Onboarding `/setup`: сейчас требует legacy-рендер (README ограничения) — довести `IDialogService.Prompt/Confirm` (CF-H-003) до уровня, когда мастер работает в CellForge. Файлы: `CSB`, onboarding-код в `Harbor.Core`/CLI.
- [ ] `CF-P-003` `ICommonConfigReader` для провайдера/модели/агента в статусбаре (см. CF-I-005): смена модели без рестарта.
- [ ] `CF-P-004` `RuntimeRendererSwapMiddleware/RendererPipeline` (`src/Harbor.Hosting`): горячая смена рендера без потери `UiStore` (см. CF-I-001). Готово когда: swap legacy↔cellforge не теряет ленту.

## EPIC Q — Темы/дизайн-система/анимации

- [ ] `CF-Q-001` `Harbor.DesignSystem (HarborTheme/ThemeJson/TerminalColorPalette/TerminalBackgroundProbe)` + `Widgets/JsonThemeLoader + ThemeFileWatcher` → `IThemeService` (см. CF-H-002). Сохранить live-reload и merge поверх активной.
- [ ] `CF-Q-002` `Harbor.Desktop.Animations (AccentRamp/PanelFx)` + `Rendering/SpringFx` (damped oscillator 60fps deterministic) + `Rendering/PostFx` (`GlowRegion/IPostEffect/GlowEffect`, pure/alloc-free) + `MascotDirector` crossfade: единая анимационная шина с тиком 80мс (кадровый цикл REPL: ввод/событие агента/спиннер-тик). Файлы: `Rendering/{SpringFx,PostFx}.cs`, `Widgets/MascotDirector.cs`, `CSB`/`SS`.
- [ ] `CF-Q-003` `MascotMode (Footer/Panel/Off, HARBOR_MASCOT[_MODE], off для CI/golden)` — сохранить kill-switch при переезде на `StatusViewModel/MascotPhases` (см. CF-L-006). Файлы: `Widgets/{MascotMode,AmbientMascot,MascotPanel}.cs`.

## EPIC R — Наблюдаемость, перф-контракты, тесты (по-взрослому: каждая задача с тестами)

> Политика: **нет таска без тестов**. Каждый CF-таск привозит: unit-тесты логики (TUnit, `tests/Harbor.Tui.CellForge.Tests/`) + golden-тест рендера где есть видимый output. Сабагентам запрещено билдить/тестить — тесты пишет автор таска, гоняет координатор.
>
> **Движок vs контент (строго):** умный рендерер НЕ перепиливаем — `DiffEngine/AnsiWriter/BufferSwapChain/LayoutTree/Input+Parsing/Streaming`-механика неприкосновенна, её тесты (DiffEngine*, AnsiWriter*, Golden*-byte/input/layout/session-routing, AllocationBudgets, фаззинг) обязаны быть зелёными в каждой волне. Меняем только **что отображается**: проекция `UiState→VM`, состав панелей/карточек/статусбара, палитра, форматы. Контентные golden'ы (`GoldenChatScreen/DiffBlock/MarkdownBlock/StatusSegmentBar`, `ScreenshotHashTests`, `cellforge.golden.txt`) имеют право краснеть — гасим разом в EPIC S.
>
> **Что значит «модно» конкретно (визуальная цель, всё из плана):** сегментный статусбар с `↓/↑`, `$`, `ctx%`-баром (E-006+T-008+U-005); тул-карточки с пилюлей/диффом/длит. (E-007+E-012+T-002); pills-очередь+todos над композером (T-004+U-001); `@`/`/`/`!` композер с хинтом (T-005+U-007); палитра с табами (T-009); exec-approval карточка с sandbox-бейджем (U-002); `/diff` viewer + `/goal` пин (U-003+U-006); палитра Harbor Terminal 1-в-1 с Avalonia (E-016); тосты с полосой 3px + баннеры running/streaming/thinking (E-018); курсор `▋` 530мс (E-018).

- [ ] `CF-R-001` Диагностика (см. CF-K-002): `IDiagnosticsPanel + DumpDiagnostics()` для input-парсера, diff-движка, layout-кэша. Golden-тесты на `tests/fixtures/celldiff/*.golden.txt` + `HARBOR_UPDATE_GOLDENS=1` пересев.
- [ ] `CF-R-002` Перф-контракты `RendererPerformanceContract/MarkdownRenderPerformanceContract` (`Harbor.Ui.Framework.Rendering/PerformanceContracts`): прогнать CellForge diff + markdown пайплайны, зафиксировать цифры в `docs/BENCHMARKS.md` (сейчас там только ProviderRegistry/ToolRegistry/PermissionRuleset). Готово когда: бенчи до/после CF-L-001/CF-L-003.
- [ ] `CF-R-003` PTY-тесты (`tests/Harbor.Tui.CellForge.PtyTests` L2: реальный процесс в псевдотерминале) — кейсы cellforge-бэкенда (resize, paste-flood truncate из `Parsing/ParserOptions`, ESC-flush, paste-watchdog 10с из `TerminalInputSourceOptions`).
- [ ] `CF-R-004` Парсерные бюджеты (`Parsing/ParserOptions`: cap CSI-параметров/intermediates, paste-flood truncate, OSC/DCS guard) + `Utf8IncrementalDecoder` (overlong/surrogate→U+FFFD) — фаззинг-тесты на вредоносные байты (см. `PasteSanitizer`).
- [ ] `CF-R-005` E2E против Kilocode free (`kilocode/tencent/hy3:free`, plain-захват — см. AGENTS.md §E2E): прогнать после каждого эпика B/C/D/F (AgentLoop-события `agent_start→…→agent_end` не должны рваться).
- [ ] `CF-R-006` Golden-кадры каждой панели/виджета (аналог `ToolCallCardView_GoldenFrame` из Avalonia E2E): `CellForgeGoldenFrameTests` — help/logs/diagnostics/diff/file-tree/todo/tokens + ToolCallCard + StreamingBanner + ToastStack + TokenUsagePanel, светлая/тёмная темы, узкий/широкий вьюпорт. Маскот `HARBOR_MASCOT=off` в golden (детерминизм). Готово когда: новый виджет без golden-теста не принимается в ревью.
- [ ] `CF-R-007` TUI демо-стенд (аналог Avalonia `ComponentGalleryView` + `NickConsoleEx` showcase): `harbor tui --demo` (или `HARBOR_TUI_DEMO=1`) — интерактивная галерея всех панелей/виджетов/состояний (running/streaming/thinking/error/approval/retry/compacting, пустые состояния `No diagnostics/todos/edits/logs`) на мок-данных без LLM. Каждая новая панель обязана появиться в демо. Готово когда: ревьюер смотрит фичу в демо, а не в коде.
- [ ] `CF-R-008` Контрактные тесты панелей: для каждого `IPanelProvider` — `Build` pure (два вызова → одинаковый output), `OnKey` только через `Dispatch` (проверка на `UiStore`-стаб), отсутствие исключений на `Width<20/Height<5` (клиппинг), `null`-сервисы в `PanelContext` (деградация как в `HelpPanel` fallback). Один параметризованный тест-класс на все 7+ панели.
- [x] `CF-R-009` выполнено (см. EPIC E): `ProjectionCoverageTests.cs`, 13 кейсов.

## EPIC S — Зачистка: удалить форки, закрыть дубли (только после B→R зелёных)

> Золотое правило: **golden-хэши/скриншоты перегенерируются один раз в самом конце** (`HARBOR_UPDATE_GOLDENS=1`), когда финальный вид UI зафиксирован. По ходу эпиков B→R golden-файлы НЕ трогаем — расхождения ожидаемы, фиксируем их списком и гасим все разом здесь. Пилюли/стили/лейауты mid-way golden'ами не пиним.

- [ ] `CF-S-001` Удалить мёртвый `using Harbor.Ui.Framework.Projection` из `Widgets/JsonThemeLoader.cs:2` (или заменить реальным использованием после CF-D).
- [ ] `CF-S-002` По итогам CF-A-005 удалить проигравшие дубли (кандидаты: свой `PromptHistory`→`InputModel`, свой theme-watcher→`IThemeService`, свой слот-менеджер→`SessionSwitcher`, свой diff-энкодер→один, свои статусные мапперы→`StatusMappers`). Каждый — отдельным коммитом с тестами.
- [ ] `CF-S-003` Обновить `docs/COMPONENT_CATALOG.md`, `docs/PATTERNS.md`, `docs/ROADMAP.md`, `.ai-factory/DESCRIPTION.md` под итоговую картину (CellForge — дефолтный интерактивный рендер; SpectreTui — панели-донор; ConsoleEx-имя — retired).

## EPIC T — Украдено у opencode/crush (разведка TOP-1 #27)

> Источники: `sst/opencode` (TS/`@opentui`, `SplitPaneLayout` + `PlaceOverlay`), `charmbracelet/crush` (Go/BubbleTea v2, штатный сайдбар + pills). Урок из crush-issue #149: approval-модалка обязана быть сворачиваемой/inline, иначе юзер вслепую жмёт allow.
- [ ] `CF-T-001` RejectPrompt с сообщением (`Tell what to do differently` → reply.message): reject чинит агента сразу. → `CF-N-002` (approval-очередь через OverlayStack).
- [ ] `CF-T-002` Паттерны `always` в диалоге (`git status*` suggested-patterns) + fullscreen `Ctrl+F`. → `CF-N-001/002`, `ApprovalGateView`.
- [ ] `CF-T-003` Живой `auto/YOLO`-индикатор в композере + `Ctrl+Y`-тоггл без рестарта. → `CF-N-002` + `CF-O-001`.
- [ ] `CF-T-004` Pills-очередь+todos над композером (печать при running → очередь, `Ctrl+T` toggle, Esc возвращает в draft). Главный UX-разрыв CellForge. → `CF-N-004` + `CF-E-002`.
- [ ] `CF-T-005` Триггерная тройка `@`/`/`/`!` + `Ctrl+V`-картинка + `$EDITOR`-фолбэк + `Tab`-complete/`Esc`-hide в одном композере. → `CF-O-001/005/006`, `CF-B-005`.
- [ ] `CF-T-006` Git-undo/redo (`/undo` сворачивает сообщения+файлы) + per-turn diff-viewer. → `CF-E-005` + EPIC N.
- [ ] `CF-T-007` Живые сессии (`IsBusy`/`AttachedClients`, rename/delete/fork, parent/child-навигация). → `CF-E-004` + EPIC I.
- [ ] `CF-T-008` Сегментный statusbar + per-tool cost-бары `█` + `ctx%`/tok-s всегда на виду. → `CF-E-006`, `CF-D-002`.
- [ ] `CF-T-009` Палитра с табами System/User/MCP + фильтр + условные пункты (Summarize/Thinking когда релевантны). → `CF-E-008`.
- [ ] `CF-T-010` `system`-тема (наследование цветов терминала, `none`/ANSI) + иерархия builtin<user<project<cwd + live-reload + автокомпакт <120 + notify-только-когда-blurred. → `CF-Q-001/002` + EPIC E.

## EPIC U — Украдено у claude code / codex CLI (разведка TOP-1 #27)

> claude: inline permission-промпт (Yes/No+comment, табы ←/→), plan-цикл Shift+Tab, checkpoints/rewind (100/промпт), statusline-скрипт на JSON stdin. codex: steer (Enter-инжект в идущий turn) + Tab-очередь (вкл. очередь slash), exec-approval карточка с sandbox-бейджем, `/diff` + `/review` гейт, `/goal` пин, `/side` ephemeral-чат, `codex queue` headless.
- [ ] `CF-U-001` Steer + Queue: Enter-инжект инструкций в идущий turn, Tab-очередь (включая очередь slash), pills-очередь над композером. → `CF-N-004` + `CF-T-004`.
- [ ] `CF-U-002` Exec-approval карточка: команда + sandbox-бейдж + per-command ID + `/approve`-retry одного denial; спрашивать только на escalation (сеть/вне workspace). Модалка сворачиваемая/inline (урок crush #149). → `CF-N-002`, `CF-L-004`.
- [ ] `CF-U-003` Интерактивный `/diff` (←/→ по turns, Enter drill в файл, untracked включены) + `/review` гейт перед коммитом. → `CF-E-005`, `CF-T-006`.
- [ ] `CF-U-004` Plan-toggle Shift+Tab + approve-меню `Yes+auto / Yes manual / No keep planning` + Ctrl+G правка плана в `$EDITOR`. → `CF-O-001` + EPIC N.
- [ ] `CF-U-005` Футер-пикер `/statusline` (model/reasoning/context %/лимиты/git/токены/session) + `/title` + `/status` одной командой. → `CF-E-006`, `CF-T-008`.
- [ ] `CF-U-006` `/goal` пин (долгая цель висит в статусе) + `/side|/btw` ephemeral-чат без засорения транскрипта. → EPIC N + EPIC E (панель цели).
- [ ] `CF-U-007` `@` unified picker (файлы+dirs+плагины+скиллы+сессии) + `!` shell + image-чип `[Image #N]` + Ctrl+G редактор + Alt+R raw-copy. → `CF-O-005/006`, `CF-H-005`, `CF-T-005`.
- [ ] `CF-U-008` Esc+Esc rewind (restore code/conversation/summarize from|up-to here) + fork-vs-new-vs-clear семантика. → EPIC E (rewind-панель) + `CF-P-003/004`.
- [ ] `CF-U-009` Ctrl+R fuzzy history across-проектов + Ctrl+S stash/restore промпта + Ctrl+O/T транскрипт с timestamp+model + `?` cheat-sheet. → `CF-O-001/002`, `CF-H-004`.
- [ ] `CF-U-010` Headless-очередь (`queue --thread --message`) + `/compact` на ~70% + `/rename|/archive` из TUI + тосты Compaction/AgentError. → EPIC P + `CF-H-001`.

---

## Приложение: как исполнять

1. Эпики A→D→F→B/C — критический путь (каркас → проекция → контракт → TEA). E/G/H/I можно параллелить после D.
2. Каждый таск = отдельная ветка + `dotnet build` (0 warnings) + тесты затронутых проектов + arch-тесты при смене референсов. Solution-wide `dotnet test` не гонять (MTP-хрупкость, см. AGENTS.md). **Окружение 2026-09-04: `dotnet test`-обёртка отдаёт 0 тестов (exit 5) даже на зелёных проектах — гонять тестхост напрямую**: `dotnet build <proj> -c Release` + `./tests/<Proj>/bin/Release/net10.0/<Proj>... (MTP exe). Проверено: Core.Tests 92/92 напрямую.
3. AOT-чистота: CellForge `IsAotCompatible=true` — новые зависимости проверять на trim/IL2xxx (см. `Harbor.Tui.CellForge.csproj:9-10`), `Newtonsoft/EF/xUnit/AssemblyLoadContext/Spectre.Console.Cli` запрещены (см. AGENTS.md §What NOT to do).
4. `Harbor.Core` из TUI не референсить напрямую — только события `AgentEvent` (см. AGENTS.md §Add a TUI view).
