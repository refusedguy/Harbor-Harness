# Harbor.Tui.CellForge

Следующее поколение терминального рендера Harbor: собственный входной пайплайн
(kitty / SGR-mouse / bracketed paste), клеточный cell-diff рендер и виджеты
живого чата. BCL-only, AOT-совместимо, 0 unsafe. **Движок (alt-screen/inline
рендер, ввод, стриминг) — рабочий и включён в интерактивный REPL как
полноценный бэкенд; интеграция с `Harbor.Ui.Framework` (проекция, TEA-цикл,
панели, сессии) — в процессе, см. `.ai-factory/plans/cellforge-full-integration-backlog.md`.**

> Историческое имя проекта — `Harbor.Tui.ConsoleEx` (MVP CE-0…CE-4). Канонический
> id бэкенда — `cellforge`, `consoleex` остался legacy-алиасом (принимается везде,
> где выбирается рендер). Не путать с `Harbor.Tui.NickConsoleEx` — это отдельный
> ADDITIVE-бэкенд (`HARBOR_TUI=nickconsoleex`) поверх nickprotop/ConsoleEx
> (SharpConsoleUI).

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
- Windows: raw-режим ещё не подключён (`WindowsVtModeController` бросает
  `PlatformNotSupportedException`) → автоматический откат на legacy;
- kitty-протокол не пушится (консервативный legacy-энкодинг); Shift+Enter /
  Ctrl-комбинации, неразличимые без kitty, деградируют явно.

## Что запускается

| Подсистема | Файлы | Ответственность |
|---|---|---|
| Capabilities | `Capabilities/` (5: `TerminalCapabilities`, `CapabilityProber`, `TerminalQueries`, `InlineImageProbe`, `NotifyProbe`) | проба возможностей терминала: цвета, inline-картинки (kitty/Sixel/OSC1337), уведомления (OSC99/OSC777) |
| Input | `Input/` (15: `TerminalInputSource(+Options)`, `TerminalInputStream`, `InputEvent`, `MouseEvent`/`MouseRouter`, `PasteEvent`/`PasteSanitizer`, `ResizeSignal`, `FocusRouter`, `CapabilityEvent`, `ITerminalModeController`/`Null`/`UnixTermios`/`WindowsVt`) | поток чтения stdin → типизированные события (key/mouse/paste/resize); raw-mode контроллеры; bracketed-paste anti-injection |
| Parsing | `Parsing/` (4: `EscapeSequenceParser(+Options)`, `ParserState`, `Utf8IncrementalDecoder`) | байтовый state-machine парсер: kitty-клавиатура, SGR-мышь, bracketed paste, инкрементальный UTF-8 |
| Rendering | `Rendering/` (22: `AnsiWriter` (SGR-автомат), `DiffEngine`, `BufferSwapChain`, `LayoutTree`, `ComposerController`, `PromptBuffer`/`PromptHistory`/`PromptRenderer`/`PromptViewport`, `VimComposerMode`, `MarkdownEditOps`, `InlineSession`, `Osc99Notify`/`Osc777Notify`/`Osc52Clipboard`/`Osc1337Image`, `Graphics`, `PostFx`, `SpringFx`, `CellForgeDiffEncoder`, `ITerminalBackend`/`StdoutBackend`) | BACK/FRONT буферы, один атомарный флаш на кадр; композер и его саб-режимы; OSC-форматтеры; remote/OTA diff-энкодер |
| Streaming | `Streaming/` (5: `ScreenSession`, `ChatScreenBridge`, `InlineAgentStreamBridge`, `StreamBlock`, `CommitTickPacer`) | события агента → блоки ленты (стрим через `CommitTickPacer`); alt-screen и inline пути |
| Widgets | `Widgets/` (23: `VirtualizedChatTimeline`, `ChatScreenLayout`, `SideBarView`, `CommandPaletteView`+`FuzzyMatcher`, `ToolCallBlock`, `StreamingMarkdownBlock`/`AssistantMarkdownBlock`, `SelectionEngine`, `TimelineRing`/`TimelineLayoutCache`, `SpinnerStrip`, `RetryCountdown`, `QuickSwitchSlots`, `LeaderKeyRouter`, `MascotDirector`/`MascotPanel`/`AmbientMascot`/`MascotMode`, `JsonThemeLoader`/`ThemeFileWatcher`, `ImageBlock`/`JpegProbe`) | лента чата, сайдбар, палитра, тулы, темы (live-reload), выделение copy-on-select, слоты сессий, маскот |
| Адаптер рендера | `CellForgeTuiRenderer.cs` (+ `CellForgeRenderContext`) | `ITuiRenderer`/`BaseTuiRenderer`-адаптер для event-driven пути (`HARBOR_TUI=cellforge`); проекция `UiState` в виджеты (`ProjectStateIntoWidgets`); контекст пишет через `AnsiWriter` |

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

\* Windows ожидает bring-up спринта raw-mode (см. ограничения).

## Разработка

```bash
dotnet test tests/Harbor.Tui.CellForge.Tests          # unit/golden тесты движка
dotnet test tests/Harbor.Tui.CellForge.PtyTests       # L2: реальный процесс в псевдотерминале (CE-5)
dotnet test tests/Harbor.App.Cli.Tests                # E2E-smoke полного REPL-цикла
HARBOR_UPDATE_GOLDENS=1 dotnet exec <test.dll>        # пересев golden-фикстур
```

Golden-фикстуры: `tests/fixtures/celldiff/*.golden.txt`. Спецификации:
`.kilo-docs/consoleex-{design,celldiff,widgets,perf}.md`.
