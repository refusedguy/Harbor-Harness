# Demo GIF — терминальные демо для README

## Цель
Сделать reproducible GIF/MP4 демо работы Harbor-Harness для README и docs, чтобы проект можно было показать без установки.

## Что уже есть
- `TuiDriver.CapturePngAsync()` — делает PNG скриншот текущего TUI
- `AnsiTerminalBuffer` — 2D эмулятор терминальной сетки для pixel-perfect рендеринга
- `Avalonia.Headless` — off-screen рендеринг для PNG
- `Mock LLM server` — in-process HTTP-сервер для E2E с streaming
- Golden-frame тесты в `tests/Harbor.Tui.RendererTests/`

## Что нужно сделать

### 1. Добавить `CaptureFramesAsync()` в `TuiDriver`
- Цикл: делаем N скриншотов с задержкой 100-200ms
- Возвращаем `IReadOnlyList<Image>` или сохраняем в `assets/demo/frames/`
- Поддержка both CellForge и AnsiPlain бэкендов

### 2. Создать `.tape` скрипт для VHS (рекомендуемый инструмент)
- Deterministic demo: запуск Harbor, streaming LLM ответ, tool call, approval gate
- Цель: 1 hero-GIF (5-10 сек) + 3 feature-GIF (3-5 сек каждый)
- Тема: Catppuccin Mocha или brand theme Harbor
- FPS: 10-15, размер <5MB, оптимизация через `gifsicle -O3`

### 3. Добавить `--demo` режим в Harbor CLI
- `harbor --demo` — запускает предзаписанный сценарий без реального LLM
- Использует `Mock LLM server` для deterministic ответов
- Выход: GIF/MP4 файл в `assets/demo/`

### 4. Настроить CI генерацию демо
- GitHub Actions шаг: `dotnet run --demo` → генерирует GIF
- Загружает артефакты в `assets/demo/`
- Автоматическое обновление при изменениях в TUI

### 5. Добавить демо в README.md
- 1 hero-GIF в центре README
- 3 feature-GIF: streaming markdown, approval gate, renderer switch
- Паттерн lazygit: `assets/demo/*-compressed.gif`

## Acceptance Criteria
- `dotnet run --demo` генерирует GIF/MP4 без ручных действий
- GIF размером <5MB, 10-15 fps, branding цвета
- README содержит hero-GIF + 3 feature-GIF
- CI автоматически обновляет демо при изменениях в TUI
- Демо воспроизводится локально через `vhs demo.tape`

## Hard Rules
- Один коммит = 1-2 файла
- Не добавляем новый TUI бэкенд
- Не трогаем UiStore/reducers
- Демо-режим = always-available, не требует API ключей

## Deliverables
- `src/Harbor.App.Cli/Commands/DemoCommand.cs`
- `src/Harbor.Tui.E2E/TuiDemoRecorder.cs`
- `demo/*.tape` — VHS скрипты
- `assets/demo/*.gif` — сгенерированные демо
- `.github/workflows/demo.yml` — CI генерация
- `README.md` — обновленный с GIF
