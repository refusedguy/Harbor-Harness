# Sprint: Renderer Unification

## Context
Выполняем после UI-V2. Цель: унифицировать рендереры, переименовать ConsoleEx, добавить Nickprotop/ConsoleEx как второй backend.

## Architecture
Shared business logic → multiple renderer backends. Каждый рендерер = отдельный проект.

```
Harbor.Ui.Framework.Rendering (shared)
    ↓
    ├── Harbor.Tui.CellForge       ← rename из ConsoleEx
    ├── Harbor.Tui.NickConsoleEx   ← wrapper над nickprotop/ConsoleEx
    ├── Harbor.Tui.AnsiPlain       ← merger Ansi+Plain
    ├── Harbor.Tui.Avalonia
    └── Harbor.Tui.Blazor
```

## Tasks

### Phase 1: Shared Business Logic
Вынести в `Harbor.Ui.Framework.Rendering`:
- ChatScreenLayout
- DiffBlock + UnifiedDiffParser
- ApprovalGateView
- StatusViewModel
- StreamingMarkdownRenderer

### Phase 2: CellForge Rename
- Переименовать `Harbor.Tui.ConsoleEx` → `Harbor.Tui.CellForge`
- Обновить namespace, csproj, все references
- Создать адаптер под ITuiRenderer/BaseTuiRenderer

### Phase 3: NickConsoleEx
- Добавить проект `Harbor.Tui.NickConsoleEx`
- Добавить зависимость на nickprotop/ConsoleEx
- Реализовать renderer через их abstractions
- Toggle в конфиг: `"ui.renderer": "cellforge|nickconsoleex|ansi|plain"`

### Phase 4: Unify Ansi/Plain
- Создать `Harbor.Tui.AnsiPlain` из Ansi + Plain
- Escape-code strategy в отдельный класс
- Убрать дублирование

### Phase 5: Visual Regression Tests
- Golden-frame tests для каждого рендерера
- `tests/Harbor.Tui.RendererTests/`

## Hard Rules
1. Один коммит = 1–2 файла
2. Nickprotop/ConsoleEx — это ДОБАВЛЕНИЕ, не замена CellForge
3. Мессенджеры НИКОГДА в ядре src/ — только contrib/plugins
4. НЕ трогай UiStore/reducers

## Deliverables
- Harbor.Tui.CellForge
- Harbor.Ui.Framework.Rendering
- Harbor.Tui.NickConsoleEx
- Harbor.Tui.AnsiPlain
- Visual regression tests
- HTML report в `.kilo-docs/sprints/renderer-unification/report.html`
