# ADR-001: Гибридная MVU+MVVM архитектура для Harbor

**Status:** Accepted  
**Date:** 2026-08-14  
**Authors:** Harbor team  
**Context:** UI architecture refactor

---

## 1. Контекст

Текущая архитектура UI Harbor имеет три проблемы:

1. **Дублирование стейта.** `UiState` (TUI-ориентированный, 22 поля) и `ShellState` (Avalonia-only, 1 поле) описывают одно и то же приложение независимо. Никакой shared reducer, никакой shared source of truth.

2. **Constructor bloat в ViewModel'ах.** `MainViewModel` — 11+ зависимостей, `ChatViewModel` — 8, `SettingsViewModel` — 8. Каждый VM подписывается на EventBus вручную, маппит события в свойства вручную, управляет `ObservableCollection` вручную.

3. **Отсутствие декларативной навигации.** Навигация, модалки, тосты — imperative scattering по renderer'ам. Нет единого reducer'а для chrome.

**Специфичные боли:**
- Spectre TUI имеет TEA-паттерн (`UiState` + `UiReducer` + `UiStore`), но Avalonia/WPF живут на классическом mutable MVVM.
- EventBus (`IEventBus`) уже есть в Core и used только TUI. Desktop VMs не подписаны.
- `StoreSubscriberViewModel` уже есть, но используется только частично.

---

## 2. Решение

Принят **гибридный подход**, сочетающий:

### 2.1 EventBus как единая шина (уже есть)

`IEventBus` publishes typed `AgentEvent`s. Все UI (Spectre TUI, Avalonia, WPF, Blazor) подписываются на него. Никаких новых message bus'ов не создаётся.

### 2.2 Единый `AppState` (record)

`UiState` переименовывается в `AppState`. В него merge'ятся поля из `ShellState`. Получается один immutable record для всех UI.

```
record AppState(
    ChatState Chat,           // hot path: Lines, streaming flags, tool calls
    ChromeState Chrome,       // navigation, modals, toasts
    SessionsState Sessions,   // session list, active session
    InputModel Input,         // editable prompt
    FocusMode Focus,          // keyboard focus
    ...)                      // TUI-полные поля (scroll, panels, cost)
```

### 2.3 Per-VM Reducer'ы (pure функции)

Каждый hot-path экран имеет свой reducer:

```
ChatViewReducer:    AgentEvent → ChatViewState
ChromeReducer:      AgentEvent → ChromeViewState
SessionsReducer:    AgentEvent → SessionsViewState
AppReducer:         AgentEvent → AppState  (комбинирует три выше)
```

Reducers — pure функции, без side-effects, без I/O, без Dispatcher'ов.

### 2.4 StoreSubscriberViewModel.Select<T> как bridge

`StoreSubscriberViewModel` уже имеет `Select<T>(read, apply, cmp)` с `DistinctUntilChanged`. Этим расширяется:

- Каждый VM декларирует проекции: `Select(state => state.Chat.Lines, vm => SyncCollection(vm.Lines, value))`
- `ApplySelectors` автоматически применяет дельты только при изменении значения
- `OnAfterSelectorsApplied` — новый Template Method hook для платформенной логики

### 2.5 Template Method в StoreSubscriberViewModel

```csharp
protected virtual void OnAfterSelectorsApplied(UiState state) { }
```

Позволяет Avalonia/WPF VM'ам выполнять платформенную логику (Dispatcher.UIThread.Post) ПОСЛЕ применения селекторов, не дублируя код.

### 2.6 Decorator IRenderEngine

Рендеринг выносится из ViewModel в `IRenderEngine` interface + platform-specific decorator. ViewModel держит только `ChatViewState`, не знает о Avalonia/WPF.

---

## 3. Альтернативы, которые были отклонены

| Вариант | Почему отклонён |
|---|---|
| **V1: Pure TEA (единый AppState для всего)** | Слишком радикально для Avalonia/WPF XAML. Ломает существующие DataBinding. |
| **V2: Facade-only (только constructor bloat)** | Не решает проблему дублирования стейта и отсутствия reducer'ов. |
| **V3: Reducer-in-ViewModel (локальный reducer на каждый VM)** | Хорошо для hot-path, но без единого `AppState` Chrome/Navigation остаются scattered. |
| **V4: Elmish-style (один гигантский discriminated union)** | `UiMsg` становится огромным, XAML Views не могут быть pure functions. |

---

## 4. Последствия

### 4.1 Что меняется

| Компонент | Изменение |
|---|---|
| `UiState` | Переименовывается в `AppState`, расширяется полями из `ShellState` |
| `ShellState` | Удаляется как отдельный тип |
| `ChatViewModelBase` | Мигрирует на `StoreSubscriberViewModel` + `Select<T>` |
| `SettingsViewModelBase` | Мигрирует на `StoreSubscriberViewModel` + `Select<T>` |
| `SessionListViewModelBase` | Мигрирует на `StoreSubscriberViewModel` + `Select<T>` |
| `MainViewModel` | Constructor bloat решается через `ShellInfrastructure` facade record |
| Spectre TUI | Остаётся на `UiStore`/`UiReducer` как reference implementation до отдельного таска |

### 4.2 Что остаётся без изменений

| Компонент | Причина |
|---|---|
| XAML Views (Avalonia/WPF) | DataBinding через `ObservableObject` работает |
| CommunityToolkit.Mvvm | `[ObservableProperty]`, `[RelayCommand]` остаются |
| Spectre TUI renderer | Уже на TEA, не трогаем |
| `IEventBus` интерфейс | Уже есть, используется |
| `ValueObject` ID'ы | Уже есть, не трогаем |

### 4.3 Риски и митигация

| Риск | Митигация |
|---|---|
| Миграция 60+ ViewModel'ов | Делаем incrementally: hot-path VMs первыми, остальные позже |
| AvaloniaDispatcher в reducer | Reducers pure, Dispatcher только в `OnAfterSelectorsApplied` |
| Performance hit от immutable collections | `ImmutableArray` — value type, copy is cheap; hot path uses `CreateBuilder` |
| Spectre TUI ломается | `UiState` не удаляется, только deprecated. Spectre TUI продолжает работать. |

---

## 5. План реализации

### Wave A (Prod code)

| ID | Задача | Файлы |
|---|---|---|
| impl-001 | `AppState` record (merge UiState + ShellState) | `State/AppState.cs`, `State/UiState.cs` |
| impl-002 | `ChatViewState` + `ChatViewReducer` | `State/ChatViewState.cs`, `Reducers/ChatViewReducer.cs` |
| impl-003 | `ChromeViewState` + `ChromeReducer` | `State/ChromeViewState.cs`, `Reducers/ChromeReducer.cs` |
| impl-004 | `SessionsViewState` + `SessionsReducer` | `State/SessionsViewState.cs`, `Reducers/SessionsReducer.cs` |
| impl-005 | `AppReducer` (комбинирует 3 reducer'а) | `Reducers/AppReducer.cs` |
| impl-006 | `StoreSubscriberViewModel` Template Method hook | `ViewModels/StoreSubscriberViewModel.cs` |
| impl-007 | `ChatViewModelBase` → StoreSubscriber | `ViewModels/ChatViewModelBase.cs` |
| impl-008 | `AvaloniaChatViewModel` platform hook | `apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs` |
| impl-009 | `SettingsViewModelBase` → StoreSubscriber | `ViewModels/SettingsViewModelBase.cs` |
| impl-010 | `MainViewModel` constructor bloat | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs` |
| impl-011 | `SessionListViewModelBase` → StoreSubscriber | `ViewModels/SessionListViewModelBase.cs` |
| impl-012 | `IRenderEngine` + Decorator | `Services/IRenderEngine.cs` |
| impl-013 | Удаление `ShellState` | `State/ShellState.cs` → delete |
| impl-014 | `AppStore` для Spectre TUI | `Tui/SpectreTui/State/AppStore.cs` |
| impl-015 | `EventBus` → `AppStore` dispatcher | `Services/EventBusAppStoreDispatcher.cs` |

### Wave B (Tests — только файлы, не запускать)

| ID | Задача | Файлы |
|---|---|---|
| test-001 | `ChatViewReducerTests` | `tests/Harbor.Ui.Framework.Tests/ChatViewReducerTests.cs` |
| test-002 | `ChromeReducerTests` | `tests/Harbor.Ui.Framework.Tests/ChromeReducerTests.cs` |
| test-003 | `AppReducerTests` | `tests/Harbor.Ui.Framework.Tests/AppReducerTests.cs` |
| test-004 | `StoreSubscriberSelectorTests` | `tests/Harbor.Ui.Framework.Tests/StoreSubscriberSelectorTests.cs` |

### Wave C (CI / build+test)

Запрещено в этом эпике. Пользователь гонит C самостоятельно.

---

## 6. Принятые принципы

Ссылки на нарушения из `CODE_PRINCIPLES_AUDIT.md`, которые этот ADR решает:

| Принцип | Как решается |
|---|---|
| §OOP-001 (instance mutable state в singleton) | Reducer'ы — static partial classes, нет instance state |
| §FP-003 (fire-and-forget async) | EventBus → AppStore dispatcher обрабатывает все события синхронно |
| §PERF-002 (reflection в JSON) | Records + `ImmutableArray`, no reflection |
| §OOP-002 (switch on string) | Strategy через per-VM Reducer'ы, не строковый switch |
| §ENCAPSULATION (god ViewModel) | Facade `ShellInfrastructure` + per-VM Reducer'ы |

---

## 7. Референсы

- `docs/ARCHITECTURE_LAYERS.md` — layering rules
- `docs/CODE_PRINCIPLES_AUDIT.md` — known violations
- `docs/ANTIPATTERNS.md` — forbidden patterns
- `AGENTS.md` — project conventions
