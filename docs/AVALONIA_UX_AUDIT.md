# Harbor Avalonia Desktop GUI — UI/UX Quality Audit

> **Scope:** `apps/Harbor.App.Avalonia/`  
> **Date:** 2026-09-02  
> **Competitors:** Cursor, Windsurf, Continue.dev, GitHub Copilot, Cline

---

## 1. Текущие сильные стороны Avalonia-интерфейса Harbor

### 1.1 Система дизайн-токенов HDS (Harbor Design System)
Код содержит полноценную токен-систему в `Themes/Hds/`:
- **BaseTokens.axaml** — 12-ступенчатая шкала spacing, 8 corner-radius, motion-duration (100–300ms), typography scale, layout constants
- **Elevation.axaml** — 5 уровней тени (`ShadowSm` → `ShadowXl`)
- **HdsButton.axaml** — Primary/Secondary/Ghost/Danger с `focus-visible` outline (WCAG 2.4.7)
- **HdsCard.axaml** — hoverable elevation lift (`ShadowSm` → `ShadowMd`)
- **Typography.axaml** — Caption/Label/Code/Body/Subtitle/Title/Heading/Display

Это уровень **enterprise-grade** системы. У Cursor/Windsurf такого явного разделения нет — у них Tailwind classes.

### 1.2 Двухрежимная тема (Classic + HDS/Orca)
- **Classic:** Catppuccin-Mocha (dark) + Catppuccin-Latte (light) — полные палитры по стандарту
- **HDS/Orca:** `HarborDark.axaml` + `HarborLight.axaml` с amber-акцентом (`#F5A623`), отличным от классики набором ключей
- Оба режима сосуществуют через разные ключи ресурсов — нет конфликтов

### 1.3 Производительность чата (frame-coalesced rendering)
`ChatViewModel` квантует ререндеры до **16ms** через `DispatcherTimer`. При streaming LLM генерирует десятки `TextDelta` в секунду — Harbor не дергается, в отличие от большинства Avalonia-приложений.

### 1.4 Виртуализация чата
`VirtualizingStackPanel` в `ChatView.axaml` + `ListBox.ItemContainerTheme` с fade+slide entrance (150ms `CubicEaseOut`). У конкурентов на Avalonia часто `StackPanel` без виртуализации.

### 1.5 Карточки tool calls
`ToolCallCardView` — collapsible карточка с status pill, duration, args/result, inline diff preview (`HdsDiffCompact`). Это **лучше**, чем у Cline (просто текст) и на уровне Cursor/Windsurf.

### 1.6 Командна палитра (Cmd+K)
26 команд + slash-команды, fuzzy-фильтр, overlay с `BoxShadow`. `KeyboardShortcutService` централизует все глобальные hotkeys — это правильная архитектура.

### 1.7 Мульти-Overlay система
`OverlayController` + ZIndex-стек (500–1000): palette, settings, provider browser, diff, token usage, focus session, sessions flyout. Все overlays поверх контента с backdrop-click-to-dismiss.

### 1.8 Статус-бар с живыми метриками
Статус-бар (32px) показывает: status dot, agent, модель, токены in/out, sparkline, **анимированную стоимость** (`CostAnimator` с `DispatcherTimer`), message count.

### 1.9 Toast-уведомления
Bottom-right floating toasts с kind-based border (info/success/warning/error), auto-dismiss через 4 секунды, slide-in анимация.

### 1.10 Empty State с hero-composer
Когда чат пуст — показывается `EmptyState` с иконкой, заголовком, subtitle, **hero input** (похоже на Cursor/Windsurf), contextual suggestions.

### 1.11 Title bar с Cmd+K trigger
Кастомный title bar (44px) с search trigger по центру, theme/settings кнопки справа. Windows-стиль кнопок с Segoe Fluent Icons.

### 1.12 Pull-to-refresh в чате
`PointerWheelChanged` + `PullIndicator` + `PullProgress` — загрузка старых сообщений.

---

## 2. Разрывы vs конкурентам

### 2.1 КРИТИЧЕСКИЙ: Нет файлового дерева / sidebar explorer
**Cursor, Windsurf, Copilot, Continue.dev** — все имеют файловый проводник в sidebar. Harbor имеет только **ActivityRail** (56px, 4 icon-buttons). Нет дерева файлов, нет breadcrumbs, нет git status per-file.

**Влияние:** нельзя быстро навигировать по кодовой базе без command palette.

### 2.2 КРИТИЧЕСКИЙ: Нет inline-редактирования (Cmd+K in editor)
**Cursor:** выделяешь код → Cmd+K → inline edit. **Windsurf:** Cascade — inline изменения. **Harbor:** CodeEditorView — только read-only `TextEditor` без контекстных actions.

**Влияние:** убивает основную use-case "AI-assisted editing".

### 2.3 ВЫСОКИЙ: Markdown не рендерится
`ChatView.axaml` — `TextBlock Text="{Binding Text}"`. Комментарий в коде: *"MarkdownRenderer returns once markdig API migration is done"*. `MarkdownRenderer.axaml`/`.cs` есть, но не подключен.

**У конкурентов:** Cursor/Windsurf/Continue — полноценный markdown с код-блоками, таблицами, ссылками.

### 2.4 ВЫСОКИЙ: Нет diff inline в чате
У Harbor diff — только modal overlay. У **Cursor** diff виден прямо в чате между пользовательским запросом и ответом. У **Windsurf** — inline diff с accept/reject.

### 2.5 ВЫСОКИЙ: Нет integrated terminal
`FloatingTerminalPaneWindow` и `TerminalPaneControl` существуют, но не интегрированы в основной layout. У **Windsurf** терминал — это основной элемент интерфейса.

### 2.6 СРЕДНИЙ: Анимации slide-in отключены
```xaml
<!-- NOTE: RenderTransform animation removed for Avalonia 12.x compat. -->
```
ToolCallCard и ToastCard имеют только opacity fade, нет slide-from-left/slide-from-bottom. У Cursor/Windsurf — smooth slide transitions.

### 2.7 СРЕДНИЙ: Один таб в редакторе
`CodeEditorView` — `ItemsControl` с табами, но нет split view, нет drag-to-reorder, нет pinned tabs, нет preview-on-hover. У Cursor — rich tab management.

### 2.8 СРЕДНИЙ: Нет code actions / quick fixes
Нет inline Cmd+Click actions, нет lightbulb icon, нет "Accept/Reject" для AI-предложений в коде.

### 2.9 СРЕДНИЙ: Плейсхолдер PluginPanelHostView
```xaml
<TextBlock Text="Plugin panels (v0.5)" ... />
```
Никакой функциональности. У конкурентов extensibility — ключевая фича.

### 2.10 СРЕДНИЙ: Нет onboarding flow
`OnboardingWindow.axaml`/`.axaml.cs` есть, но не видно связи с реальным процессом. У Cursor — step-by-step wizard с API key setup, model selection, agent config.

### 2.11 НИЗКИЙ: Нет a11y-фич
Только базовые `AutomationProperties.Name`. Нет screen reader announcements для streaming, нет high-contrast mode, нет font-size scaling, нет reduced-motion respect.

### 2.12 НИЗКИЙ: Нет локализации
Все строки hardcoded. Нет resx/po/JSON i18n.

### 2.13 НИЗКИЙ: Контекстное меню Board
`SessionCardView` — только Rename/Duplicate/Archive/Delete. Нет "Resume", "Branch here", "Compare", "Export".

---

## 3. Конкретные улучшения для уровня "max" UI/UX

### P0 (блокеры production-ready)

| # | Улучшение | Где | Сложность |
|---|-----------|-----|-----------|
| 1 | **Markdown renderer в ChatView** — подключить существующий `MarkdownRenderer.axaml` | `ChatView.axaml` | Средняя |
| 2 | **Inline diff в chat** — показать diff прямо между User/Assistant сообщениями, не только modal | `ChatView.axaml`, `ToolCallCardView.axaml` | Средняя |
| 3 | **File explorer sidebar** — добавить `FileTreeView` в `ActivityRailView` или расширить до полноценного sidebar | `ActivityRailView.axaml`, новый `FileTreeView.axaml` | Высокая |
| 4 | **Inline editor actions** — Cmd+K overlay для выделенного кода в `CodeEditorView` | `CodeEditorView.axaml`, новый `InlineEditOverlay` | Высокая |

### P1 (критично для parity с Cursor/Windsurf)

| # | Улучшение | Где | Сложность |
|---|-----------|-----|-----------|
| 5 | **Slide-in анимации** — зарегистрировать `TransformAnimator` для `ToolCallCard` и `ToastCard` | C# code-behind или Avalonia 12 patch | Средняя |
| 6 | **Tab management** — split view, pinned tabs, dirty indicator animation, close-on-middle-click | `CodeEditorView.axaml` | Средняя |
| 7 | **Integrated terminal** — добавить `TerminalPaneControl` в bottom panel (похоже на VS Code) | `MainWindow.axaml`, `TerminalPaneControl.axaml` | Высокая |
| 8 | **Rich empty state** — анимация typing-эффекта для hero input, skeleton loaders | `EmptyState.axaml` | Низкая |
| 9 | **Provider browser улучшения** — search/filter, model comparison, favorites | `ProviderBrowserView.axaml` | Низкая |
| 10 | **Settings validation** — inline validation, error states, required field indicators | `SettingsView.axaml` | Низкая |

### P2 (полировка до "max")

| # | Улучшение | Где | Сложность |
|---|-----------|-----|-----------|
| 11 | **Animations library** — stagger entrance для Board cards, skeleton shimmer, smooth number counters | `AppStyles.axaml`, VMs | Средняя |
| 12 | **Focus mode improvements** — ambient background blur, hide chrome, zen mode | `FocusSessionView.axaml` | Низкая |
| 13 | **Context menu enrichment** — добавить Resume/Branch/Compare в SessionCardView | `SessionCardView.axaml` | Низкая |
| 14 | **A11y** — live regions для streaming, high-contrast toggle, font scaling | Глобально | Средняя |
| 15 | **i18n** — вынести все строки в ресурсы | Глобально | Высокая |
| 16 | **Plugin panel host** — реализовать `IPanelRegistry` → tabbed content | `PluginPanelHostView.axaml` | Средняя |
| 17 | **Onboarding flow** — multi-step wizard с progress stepper | `OnboardingWindow.axaml` | Средняя |

---

## 4. Рейтинг по категориям (1–10)

| Категория | Оценка | Комментарий |
|-----------|--------|-------------|
| **Visual Design** | 8/10 | Catppuccin palette excellent, HDS tokens solid, amber accent distinctive. Не хватает depth в некоторых поверхностях. |
| **Layout** | 7/10 | Grid-based shell clean, но нет file tree, нет bottom panel. ActivityRail слишком минималистичен (56px, 4 кнопки). |
| **Typography** | 8/10 | Inter + JetBrains Mono, full scale in BaseTokens, Typography classes. HDS Typography.axaml — отличная работа. |
| **Colors / Theme** | 9/10 | Catppuccin-Mocha/Latte + HDS amber themes — один из лучших наборов в AI-coding-space. |
| **Animations** | 6/10 | Frame-coalesced chat, fade+slide for rows, pulse dots. Но slide-in для cards/toasts отключен (Avalonia 12 compat). |
| **Interaction** | 7/10 | Keyboard shortcuts, command palette, pull-to-refresh. Нет inline edit, drag-drop, context actions. |
| **Feature Completeness** | 5/10 | Нет file explorer, inline edit, integrated terminal, markdown rendering, diff inline, code actions. |
| **Polish** | 7/10 | Micro-interactions (hover, focus, pulse), toast auto-dismiss, cost animation. Missing: skeleton loaders, error states, empty state animation. |
| **Performance** | 8/10 | Virtualized chat, 16ms frame coalescing, ObservableCollection projections. Нет известных hot-path проблем. |
| **Accessibility** | 4/10 | Только AutomationProperties.Name. Нет live regions, high-contrast, reduced-motion. |
| **Responsiveness** | 6/10 | Fixed 1280×800, MinWidth 720. Нет adaptive layout для узких окон, нет mobile breakpoints (ожидаемо для desktop). |

**Средний балл: 6.9/10**

---

## 5. Заключение

Harbor Avalonia GUI — **на удивление сильная основа**. HDS токен-система, Catppuccin темы, frame-coalesced чат, tool call cards, command palette — это уровень **beta, близкий к production**.

**Главные гэпы до Cursor/Windsurf:**
1. Нет файлового проводника (P0)
2. Нет inline-редактирования кода (P0)
3. Markdown не рендерится (P0)
4. Нет integrated terminal (P1)
5. Slide-анимации отключены (P1)

**Рекомендация:** сосредоточиться на P0 (file explorer + markdown + inline edit) — это даст **parity с Continue.dev** и **approach к Cursor** в плане core UX. P1/P2 — полировка.

**Конкурентное преимущество Harbor:** анимации streaming-рендеринга (16ms frame coalescing), tool call cards с diff preview, sparkline в статус-баре, provider browser с тестированием соединения — этих фич нет у Cursor/Windsurf в таком виде.
