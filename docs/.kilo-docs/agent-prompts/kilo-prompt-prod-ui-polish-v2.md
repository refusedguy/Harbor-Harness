Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт PROD-UI-POLISH.

СТАТУС: MVP-DELIVERY закрыт. Твоя задача — ТОЛЬКО UI/UX, не возвращайся к сессиям/плагинам/агенту.

ЖЁСТКИЕ ПРАВИЛА:
1. НЕ прави код сессий, плагинов, агента, storage — только UI/UX.
2. Один коммит = 1–2 файла. Не собирай гигантский дифф.
3. После каждого коммита проверяй git status — должно быть 0 или 1–2 файла.
4. Документы уже готовы: docs/design-system-report-20260827.html и /tmp/harbor-analysis/harbor-design-system-v1.md — их достаточно, не пиши новые.

ИСПОЛЬЗУЙ ГОТОВЫЙ ДИЗАЙН:
- Color palette: accent #39bae6, success #7fd962, warning #ffb454, error #ff6b6b, tool #d2a6ff, system #f29668
- Surfaces: bg #0a0e14, panel #0d1117, surface #131820, surface2 #1a1f2b, border #1f2430, muted #5c6773
- Typography: JetBrains Mono 12px для TUI, system font для desktop
- Spacing: xs 4px, sm 8px, md 12px, lg 16px, xl 24px
- Animation: micro 100–150ms, standard 200–300ms, ease-out entrance, ease-in exit
- Components: status bar, chat bubbles, tool cards, approval gates, markdown blocks

ЗАДАЧИ В ПОРЯДКЕ:

A. Внедри дизайн-токены в код:
   - Terminal: src/Harbor.Tui.ConsoleEx/Rendering/ChatPalette.cs — замени цвета на design tokens
   - Avalonia: apps/Harbor.App.Avalonia/Themes/ — добавь HarborDesignTokens.axaml
   - Blazor: apps/Harbor.App.Blazor/wwwroot/site.css — добавь CSS custom properties

B. Screenshot-diff тесты:
   - Напиши tests для Avalonia/Blazor/WPF, которые сравнивают скриншоты с golden'ами
   - Используй Harbor.E2E.Framework/HeadlessAvaloniaDriver.cs

C. Accessibility фиксы:
   - Проверь contrast ratios для всех цветов в дизайн-системе
   - Добавь focus indicators в ConsoleEx и Avalonia
   - Убедись что все действия доступны с клавиатуры

D. Performance:
   - 60fps timeline: убедись что ConsoleEx рендерит за 16ms
   - Zero-alloc render path: убери аллокации из hot path
   - Memory leak soak: запусти тест на 8+ часов

E. Killer features:
   - Image preview inline: Sixel/kitty graphics рендеринг
   - Dictation input: voice-to-text в композер
   - Tab-strip drag-reorder: перетаскивание вкладок

F. i18n:
   - Вынеси все строки в ресурсы .resx
   - Добавь русский/английский локали

G. Markdown rich editor:
   - TipTap-class редактор в терминале
   - Таблицы, задачи-листы, подсветка синтаксиса

H. Word-diff highlighting:
   - Уже есть в WordDiff.cs, интегрируй в DiffBlock.cs

EOF
