# Harbor.Tui.CellForge

Следующее поколение терминального рендера Harbor: собственный входной пайплайн
(kitty / SGR-mouse / bracketed paste), клеточный cell-diff рендер и виджеты
живого чата. BCL-only, AOT-совместимо, 0 unsafe. **Движок (alt-screen/inline
рендер, ввод, стриминг) — рабочий и включён в интерактивный REPL как
полноценный бэкенд; интеграция с `Harbor.Ui.Framework` (проекция, TEA-цикл,
панели, сессии) — в процессе, см. [docs/ROADMAP.md §Completed — ConsoleEx MVP](../../docs/ROADMAP.md#-completed--consoleex-mvp-ce-0ce-5).**

> Историческое имя проекта — `Harbor.Tui.ConsoleEx` (MVP CE-0…CE-4). Канонический
> id бэкенда — `cellforge`, `consoleex` остался legacy-алиасом (принимается везде,
> где выбирается рендер). Раньше рядом жил отдельный ADDITIVE-бэкенд
> `Harbor.Tui.NickConsoleEx` (`HARBOR_TUI=nickconsoleex`) поверх
> nickprotop/ConsoleEx (SharpConsoleUI). Он убран из продукта и переехал в
> `contrib/` (ADR-019), так что `nickconsoleex` больше ничего не выбирает.

## Как включить

CellForge — opt-in, legacy-рендеры не затронуты. Три способа (по убыванию приоритета):

```bash
# 1) Переменная окружения (consoleex — legacy-алиас того же бэкенда)
HARBOR_TUI=cellforge dotnet run --project apps/Harbor.App.Cli

# 2) config.json (~/.harbor/config.json)
{ "tui": "cellforge" }

# 3) cli.json (~/.harbor/cli.json)
{ "defaultTuiRenderer": "cellforge" }
```

Kill-switch в `~/.harbor/config.json` (приоритет выше выбора рендера).
Имя JSON-секции историческое (`consoleEx`) — так её читает
`Harbor.Application/Configuration` (`CellForgeUiConfig`), переименованию не подлежит
без миграции конфигов:

```jsonc
{
  "tui": "cellforge",
  "ui": {
    "consoleEx": {
      "enabled": true,      // false ⇒ откат на legacy ANSI с предупреждением в логе
      "syncUpdates": true   // DECSYNC CSI ?2026 h/l вокруг кадра
    }
  }
}
```

Секция пишется в config.json только при отклонении от дефолтов. Устаревшая
корневая форма `{ "consoleEx": { … } }` (CE-4 промежуточные сборки) всё ещё
читается, но больше не записывается.

Ограничения текущей интеграции:

- onboarding-мастер (`/setup`) требует legacy-рендер; если он не завершён,
  CellForge откладывается до следующего запуска;
- `/setup` внутри CellForge печатает подсказку вместо интерактивного ввода;
- Windows: `HARBOR_TUI=cellforge` использует `WindowsVtModeController`
  по умолчанию (issue #1249, bring-up slice 2: Win32 Console API —
  `SetConsoleMode` + `VIRTUAL_TERMINAL_INPUT`/`VIRTUAL_TERMINAL_PROCESSING`,
  CP 65001 + UTF-8). Откат на legacy остаётся только capability-откатом:
  raw-mode проба не удалась (перенаправленный stdin, консоль отказала
  `SetConsoleMode`) — отказ пишется в лог, не в молчаливый фолбэк.
  `HARBOR_CELLFORGE_WINDOWS=0` — явный opt-out на legacy (до slice 2 эта
  переменная была opt-in: `=1` включал bring-up);
- kitty-протокол не пушится (консервативный legacy-энкодинг); Shift+Enter /
  Ctrl-комбинации, неразличимые без kitty, деградируют явно.

## Что запускается

Движок терминала ввода-вывода живёт в `Harbor.Tui.CellForge.Engine/` (подробно —
его [README](../Harbor.Tui.CellForge.Engine/README.md)); чат-слой — в `Chat/`
этого проекта. Неймспейсы остались `Harbor.Tui.CellForge.*` (сплит — issue #33).

| Подсистема | Файлы | Ответственность |
|---|---|---|
| Capabilities | `Engine/Capabilities/` (7: `TerminalCapabilities`, `CapabilityProber`, `TerminalQueries`, `TerminalBackgroundProbe`, `InlineImageProbe`, `NotifyProbe`, `RgbColor`) | проба возможностей терминала: цвета, фон, inline-картинки (kitty/Sixel/OSC1337), уведомления (OSC99/OSC777) |
| Input | `Engine/Input/` (21: `TerminalInputSource(+Options)`, `TerminalInputStream`, `InputEvent`, `KeyEvent(+Type/Code/Modifiers)`, `MouseEvent`/`MouseRouter`, `PasteEvent`/`PasteSanitizer`, `ResizeSignal`, `FocusRouter`/`IFocusTarget`, `CapabilityEvent`, `UiKeyDto`, `ITerminalModeController`/`Null`/`UnixTermios`/`WindowsVt`) | поток чтения stdin → типизированные события (key/mouse/paste/resize); raw-mode контроллеры; bracketed-paste anti-injection |
| Parsing | `Engine/Parsing/` (4: `EscapeSequenceParser`, `ParserOptions`, `ParserState`, `Utf8IncrementalDecoder`) | байтовый state-machine парсер: kitty-клавиатура, SGR-мышь, bracketed paste, инкрементальный UTF-8 |
| Rendering | `Engine/Rendering/` (27: `AnsiWriter` (SGR-автомат), `DiffEngine`/`FrameDiff`, `ScreenBuffer`/`BufferSwapChain`, `Cell`/`CellStyle`/`UnicodeWidth`, `LayoutPrimitives`+`ContainerNode`/`FlexLayout`/`ZStackNode`/`DirtyRect`, `ITerminalBackend`/`ISyncTerminalBackend`/`StdoutBackend`, `Osc99Notify`/`Osc777Notify`/`Osc52Clipboard`/`Osc1337Image`+`InlineImageEncoder`, `Graphics`, `PostFx`, `SpringFx`, `EngineStageCounters`) | BACK/FRONT буферы, один атомарный флаш на кадр; layout-дерево; OSC-форматтеры |
| Widgets (verbatim batch A + adapted batch B) | `Engine/Widgets/` (10: `SpinnerStrip`, `RetryCountdown`, `JpegProbe`, `ScrollableViewport`+`ScrollInfo`, `MascotMode`, `ToolOutputCollapse`, `ThinkingSummary`, `FuzzyMatcher`, `AutocompletePalette`, `AutocompleteMode`), `Engine/Streaming/` (1: `CommitTickPacer`) | #1620 (batch A): чистые BCL-листья дословно; #1627 (batch B): адаптированные — guard-retype `Ensure.NotNull` → `ArgumentNullException.ThrowIfNull`, `AutocompleteMode` переехал из `AutocompleteTrigger.cs` |
| Streaming | `Chat/Streaming/` (9: `ScreenSession`, `ChatScreenBridge`, `InlineAgentStreamBridge`, `StreamBlock`, `StreamCoalescer`, `ApprovalGateRouter`, `ContextSegmentSync`, `EventSubscription`, `ToolCardTracker`) | события агента → блоки ленты (стрим pacing — `CommitTickPacer`, переехал в Engine #1620); alt-screen и inline пути |
| Chat-слой | `Chat/Rendering/` (18: `ComposerController`, `PromptBuffer`/`PromptHistory`/`PromptRenderer`/`PromptViewport`, `VimComposerMode`, `MarkdownEditOps`, `InlineSession`, `LayoutTree`, …), `Chat/Panels/` (17 панелей), `Chat/Adapters/` (2), `Chat/Onboarding/` (1) | композер и его саб-режимы; панели; адаптеры; онбординг |
| Widgets | `Chat/Widgets/` (46: `VirtualizedChatTimeline`, `ChatScreenLayout`, `SideBarView`, `SessionTabStripPanel`, `CommandPaletteView`+`PaletteCatalogs`, `ToolCallBlock`, `TimelineBlocks`, `StreamingMarkdownBlock`/`AssistantMarkdownBlock`, `SelectionEngine`+`Scrollbar`, `TimelineRing`/`TimelineLayoutCache`, `GaugeBar`, `LeaderKeyRouter`+`WhichKeyHelpOverlay`, `JsonThemeLoader`/`ThemeFileWatcher`, `ImageBlock`+`MarkupPngBaker`, `TableBlock`, `ReadGroupBlock`, `PanelChrome`, `ToastOverlay`, `FilePickerView`, `DialogFormWidgets`, `DiffViewerOverlay`, `ImageViewerOverlay`, `MarkupOverlay`, `SetupChecklistOverlay` (+`*Layer` для оверлеев); batch A (`SpinnerStrip`, `RetryCountdown`, `JpegProbe`, `ScrollableViewport`/`ScrollInfo`, `MascotMode`, `ToolOutputCollapse`, `ThinkingSummary`) переехал в Engine #1620; batch B (#1627, adapted) — `FuzzyMatcher`, `AutocompletePalette`, `AutocompleteMode`) | лента чата, сайдбар, палитра, тулы, темы (live-reload), выделение copy-on-select, оверлеи |
| Маскот | `Chat/Mascot/` (3: `MascotDirector`, `MascotPanel`, `AmbientMascot`) | #1619: декорация (кот в футере / отдельная панель) вынесена в собственную область; входных ссылок ровно две — `ChatScreenLayout` и `ChatScreenBridge`; `MascotMode`/`MascotModeEnv` остались в Engine (#1620), `MascotReaction` — в `Harbor.Ui.Framework.Rendering` |
| Адаптер рендера | `CellForgeTuiRenderer.cs` (+ `CellForgeRenderContext`) | `ITuiRenderer`/`BaseTuiRenderer`-адаптер для event-driven пути (`HARBOR_TUI=cellforge`); проекция `UiState` в виджеты (`ProjectStateIntoWidgets`); контекст пишет через `AnsiWriter` |

Клавиши `DiffViewerOverlay` разбирает одна таблица команд
(`DiffViewerCommandTable`, internal — #1544): строка на gesture, объект-команда
на действие. Оба `HandleKey` (legacy `ConsoleKeyInfo` и kitty `KeyEvent`) —
тонкие адаптеры: нормализуют вход в словарь виджета и ищут команду в этой
таблице. Новая клавиша — одна строка, а два пути ввода разойтись не могут,
потому что таблица одна.

## Кадровой цикл (REPL)

Event-driven: кадр собирается только при пробуждении — ввод, событие агента или
спиннер-тик 80 мс в режиме Running. Пустые кадры DiffEngine отбрасывает сам.
Порядок кадра: `Solve → PrepareFrame → BeginFrame → Paint×panels → Flush`.

Клавиши: текст/Enter — промпт, Shift+Enter — новая строка (kitty), PageUp/PageDown
и колесо — скролл истории, Ctrl+C — прервать текущий ход агента, повторный Ctrl+C
(или пустой буфер дважды) — выход, EOF/Ctrl+D — выход после завершения хода.

## Терминалы

Требования: VT100+ последовательности, UTF-8. Полный опыт — xterm-совместимые
терминалы с поддержкой bracketed paste (kitty, alacritty, wezterm, iTerm2,
GNOME Terminal, Windows Terminal*). Деградация явная: без 256 цветов/юникода
спиннер и статусы остаются читаемыми, но упрощаются. Внутри tmux/screen kitty
не пушится по дизайну (§2.5 design-doc).

\* Windows: bring-up идёт по issue #1249; slice 2 — raw-режим по умолчанию
(`WindowsVtModeController`), opt-out через `HARBOR_CELLFORGE_WINDOWS=0`.

## Разработка

```bash
dotnet run --project tests/Harbor.Tui.CellForge.Tests -c Release --no-build    # unit/golden тесты движка
dotnet run --project tests/Harbor.Tui.CellForge.PtyTests -c Release --no-build # L2: реальный процесс в псевдотерминале (CE-5)
dotnet run --project tests/Harbor.App.Cli.Tests -c Release --no-build         # E2E-smoke полного REPL-цикла
HARBOR_UPDATE_GOLDENS=1 dotnet exec <test.dll>        # пересев golden-фикстур
```

Golden-фикстуры: `tests/fixtures/celldiff/*.golden.txt`. Спецификации:
`docs/.kilo-docs/consoleex-{design,celldiff,widgets,perf}.md`.
