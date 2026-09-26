Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт Avalonia Desktop Polish.

## Context
Harbor имеет полноценную Avalonia desktop GUI (`apps/Harbor.App.Avalonia/`), но она не покрыта dedicated спринтом. В то же время в ROADMAP.md висят Avalonia-specific задачи: accessibility audit, reusable component catalog, E2E coverage.

## Tasks
1. **Accessibility audit (WCAG 2.1 AA).**
   Пройдись по всем `.axaml`/`.axaml.cs` в `apps/Harbor.App.Avalonia/`, проверь:
   - AutomationProperties.Name / HelpText на всех интерактивных контролах
   - Keyboard navigation (TabIndex, FocusManager)
   - Color contrast в `Resources/*.xaml` палитрах
   Acceptance: `dotnet test tests/Harbor.App.Avalonia.Tests/ -c Release --no-build` + чек-лист в `docs/ACCESSIBILITY.md`.

2. **Component catalog completion.**
   Доведи `docs/COMPONENT_CATALOG.md` до реального состояния: перечисли все кастомные контролы в `apps/Harbor.App.Avalonia/Views/Controls/` и `Harbor.Ui.Framework.Rendering/Widgets/`, для каждого укажи props, events, usage example. Acceptance: 100% coverage существующих reusable компонент.

3. **E2E test flake cleanup.**
   В CI падает `ToolCallCardView_GoldenFrame` + `Chat_ScrollHistory` в Avalonia E2E. Исправь или заskip наKnownFlake. Acceptance: `dotnet test tests/Harbor.E2E.App.Avalonia/ -c Release --no-build` проходит стабильно 2 раза подряд.

## Hard Rules
1. Один коммит = 1–2 файла.
2. Не ломай существующие Avalonia viewmodels — только дополняй.
3. После каждого коммита: `dotnet test tests/Harbor.App.Avalonia.Tests/ -c Release --no-build`.

## Deliverables
- `docs/ACCESSIBILITY.md`
- `docs/COMPONENT_CATALOG.md` (обновлённый)
- report.html в `.kilo-docs/sprints/avalonia/`
