# ADR-002: Project Restructuring — Production vs Experimental Isolation

**Status:** Accepted  
**Date:** 2026-08-16  
**Authors:** Harbor team  
**Context:** Large refactoring & production stabilization

---

## 1. Контекст

Текущая структура `Harbor.slnx` содержит 67 src + 7 app + 27 test проектов. Среди них:

- **Production-ready:** CLI (`Harbor.App.Cli`), Spectre TUI, Avalonia
- **Experimental/secondary:** WPF, MAUI, Blazor, Sixel, TerminalGui, RazorConsole, Termina

Проблемы:
1. `Harbor.sln` компилируется медленно (>5с) из-за включения всех UI-платформ
2. Experimental UI мешают рефакторингу Core — можно случайно сломать не-production код
3. Дублирование кода между Avalonia/WPF/MAUI/Blazor ViewModel'ами
4. CI/CD тянет все платформы, хотя production — только CLI + Avalonia

---

## 2. Решение

### 2.1 Production solution (`Harbor.slnx`)

Оставляем **только** production-ready проекты:

```
src/
├── Harbor.Abstractions/
├── Harbor.Domain/
├── Harbor.Core/
├── Harbor.Ui.Framework.*/
├── Harbor.Providers.*/
├── Harbor.Storage.*/
├── Harbor.Tools.Builtin/
├── Harbor.Tui.SpectreTui/
├── Harbor.Tui.Ansi/
├── Harbor.Tui.Plain/
└── Harbor.Cli/

apps/
└── Harbor.App.Cli/

tests/
├── Harbor.Core.Tests/
├── Harbor.Ui.Framework.Tests/
├── Harbor.Tui.Tests/
└── ... (только unit-тесты для production кода)
```

**Цель:** `Harbor.slnx` компилируется за <5 секунд, 0 warnings.

### 2.2 Samples solution (`Harbor.Samples.sln`)

Перемещаем experimental проекты:

```
apps/
├── Harbor.App.Avalonia/ → stays in main (production)
├── Harbor.App.Wpf/ → samples/
├── Harbor.App.Maui/ → samples/
├── Harbor.App.Blazor/ → samples/

src/
├── Harbor.Tui.Sixel/ → samples/
├── Harbor.Tui.TerminalGui/ → samples/
├── Harbor.Tui.RazorConsole/ → samples/
├── Harbor.Tui.Termina/ → samples/
├── Harbor.Tui.Spectre.Fullscreen/ → samples/ (пока экспериментальный)
└── Harbor.Desktop.Shared/ → samples/ (вместе с desktop apps)

tests/
├── Harbor.App.Avalonia.Tests/ → stays (production UI)
├── Harbor.App.Wpf.Tests/ → samples/
├── Harbor.App.Maui.Tests/ → samples/
├── Harbor.App.Blazor.Tests/ → samples/
├── Harbor.Tui.E2E.Tui.Sixeltests/ → samples/
└── ... (остальные E2E для experimental UI)
```

### 2.3 Правило

**`Harbor.slnx` = production only.** Любой experimental/unsupported UI живёт в `Harbor.Samples.sln`. Core не зависит от experimental UI.

---

## 3. Последствия

### 3.1 Что меняется

| Проект | Действие |
|---|---|
| `Harbor.App.Wpf` | Перемещается в `samples/` |
| `Harbor.App.Maui` | Перемещается в `samples/` |
| `Harbor.App.Blazor` | Перемещается в `samples/` |
| `Harbor.Tui.Sixel` | Перемещается в `samples/` |
| `Harbor.Tui.TerminalGui` | Перемещается в `samples/` |
| `Harbor.Tui.RazorConsole` | Перемещается в `samples/` |
| `Harbor.Tui.Termina` | Перемещается в `samples/` |
| `Harbor.Tui.Spectre.Fullscreen` | Перемещается в `samples/` |
| `Harbor.Desktop.Shared` | Перемещается в `samples/` |
| `Harbor.Scripting.*` | Перемещается в `samples/` (экспериментальный) |
| `Harbor.Plugins.*` | Перемещается в `samples/` (экспериментальный) |
| `Harbor.CodeGen` | Перемещается в `samples/` |

### 3.2 Что остаётся в production

| Проект | Причина |
|---|---|
| `Harbor.App.Cli` | Единственный production entry-point |
| `Harbor.App.Avalonia` | Production desktop UI |
| `Harbor.Tui.SpectreTui` | Production full-screen TUI |
| `Harbor.Tui.Ansi` | Production streaming TUI |
| `Harbor.Tui.Plain` | Production plain-text TUI |

### 3.3 Риски

| Риск | Митигация |
|---|---|
| Сломанные references из Core в Samples | Core НЕ зависит от Samples. Проверка через Architecture Tests. |
| Разрыв CI/CD | Обновить CI workflows для обоих solutions |
| Разрыв документации | Обновить README с новой структурой |

---

## 4. Правила для будущих проектов

1. **Новый UI renderer** → по умолчанию в `samples/`
2. **Новый tool/provider** → в `src/`, если production-ready
3. **Experimental feature** → в `samples/` или `samples/plugins/`
4. **Core никогда не зависит от Samples** — enforced Architecture Tests
