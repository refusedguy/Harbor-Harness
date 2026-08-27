# Harbor.Tui.ConsoleEx

Следующее поколение терминального рендера Harbor: собственный входной пайплайн
(kitty / SGR-mouse / bracketed paste), клеточный cell-diff рендер и виджеты
живого чата. BCL-only, AOT-совместимо, 0 unsafe. **MVP (CE-0…CE-4) — готово,
включено в интерактивный REPL как второй путь рендера.**

## Как включить

ConsoleEx — opt-in, legacy-рендеры не затронуты. Три способа (по убыванию приоритета):

```bash
# 1) Переменная окружения
HARBOR_TUI=consoleex dotnet run --project apps/Harbor.App.Cli

# 2) config.json (~/.harbor/config.json)
{ "tui": "consoleex" }

# 3) cli.json (~/.harbor/cli.json)
{ "defaultTuiRenderer": "consoleex" }
```

Kill-switch в `~/.harbor/config.json` (приоритет выше выбора рендера):

```jsonc
{
  "tui": "consoleex",
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

Ограничения MVP:

- onboarding-мастер (`/setup`) требует legacy-рендер; если он не завершён,
  ConsoleEx откладывается до следующего запуска;
- `/setup` внутри ConsoleEx печатает подсказку вместо интерактивного ввода;
- Windows: raw-режим ещё не подключён (`WindowsVtModeController` бросает
  `PlatformNotSupportedException`) → автоматический откат на legacy;
- kitty-протокол не пушится (консервативный legacy-энкодинг); Shift+Enter /
  Ctrl-комбинации, неразличимые без kitty, деградируют явно.

## Что запускается

| Компонент | Ответственность |
|---|---|
| `TerminalInputSource` | поток чтения stdin → типизированные `InputEvent` (key/mouse/paste/resize) |
| `ScreenSession` + `DiffEngine` + `AnsiWriter` | BACK/FRONT буферы, один атомарный флаш на кадр |
| `ChatScreen` (`LayoutTree`) | timeline ↑ composer ↓ status footer |
| `ChatScreenBridge` | события агента → блоки ленты (стрим через `CommitTickPacer`) |
| `ComposerController` | редактирование, Enter-семантика, Ctrl+C |

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
dotnet test tests/Harbor.Tui.ConsoleEx.Tests          # 370+ unit/golden тестов
dotnet test tests/Harbor.Tui.ConsoleEx.PtyTests       # L2: реальный процесс в псевдотерминале (CE-5)
dotnet test tests/Harbor.App.Cli.Tests                # E2E-smoke полного REPL-цикла
HARBOR_UPDATE_GOLDENS=1 dotnet exec <test.dll>        # пересев golden-фикстур
```

Golden-фикстуры: `tests/fixtures/celldiff/*.golden.txt`. Спецификации:
`.kilo-docs/consoleex-{design,celldiff,widgets,perf}.md`.
