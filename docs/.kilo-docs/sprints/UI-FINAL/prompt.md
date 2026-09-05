Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт UI-FINAL.

СТАТУС: агент-человек уже сделал часть инфраструктуры, но UI всё ещё выглядит как кал. Твоя задача — довести до продакшена.

УЖЕ СДЕЛАНО (не трогай):
1. Создан отдельный проект `Harbor.DesignSystem` с:
   - `ColorPalette.cs` — Catppuccin Mocha/Latte palette
   - `TerminalColorPalette.cs` — ТОЧНЫЕ цвета из HTML-дизайн-системы (#39bae6, #7fd962, #ffb454, #ff6b6b, #d2a6ff, #f29668, #0a0e14, #0d1117, #131820, #1a1f2b, #1f2430, #5c6773)
   - `DesignTokens.cs` — spacing/radius/typography scale
   - Все через `Harbor.Ui.Framework.Projection.RgbColor`
2. `ChatPalette.cs` в ConsoleEx начат, но НЕ подключён к `TerminalColorPalette`
3. Цепочка спринтов переключена на этот промпт

ТВОЯ ЗАДАЧА — только UI/UX код, не возвращайся к сессиям/плагинам/агенту/storage.

ЖЁСТКИЕ ПРАВИЛА:
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. После каждого коммита проверяй git status — должно быть 0 или 1–2 файла.
3. Документы уже готовы: docs/design-system-report-20260827.html — их достаточно, не пиши новые.
4. Мессенджеры НИКОГДА в ядре src/ — только contrib/plugins.

ДИЗАЙН-СИСТЕМА (обязательно используй):
- Terminal colors: accent #39bae6, success #7fd962, warning #ffb454, error #ff6b6b, tool #d2a6ff, system #f29668
- Surfaces: bg #0a0e14, panel #0d1117, surface #131820, surface2 #1a1f2b, border #1f2430, muted #5c6773
- Typography: JetBrains Mono 12px для TUI, system font для desktop
- Spacing: xs 4px, sm 8px, md 12px, lg 16px, xl 24px
- Animation: micro 100–150ms, standard 200–300ms, ease-out entrance, ease-in exit

ЗАДАЧИ В ПОРЯДКЕ ВЫПОЛНЕНИЯ:

A. ВНЕДРИ ДИЗАЙН-ТОКЕНЫ (сначала это):
   1. `src/Harbor.Tui.ConsoleEx/Widgets/ChatPalette.cs` — переключи на `TerminalColorPalette` из Harbor.DesignSystem
   2. `apps/Harbor.App.Avalonia/Themes/` — создай `HarborDesignTokens.axaml` с цветами из TerminalColorPalette
   3. `contrib/apps/Harbor.App.Blazor/wwwroot/css/site.css` — добавь CSS custom properties для всех токенов
   4. Убедись что все виджеты ConsoleEx (ApprovalGateView, DiffBlock, ImageBlock, ToolCallBlock, StatusViewModel) используют ChatPalette

B. АНИМАЦИИ ПАНЕЛЕЙ В CONSOLEEX (самое важное — сейчас оно скучное):
   1. Добавь анимации появления/исчезновения панелей в ConsoleEx: fade 150ms, slide 300ms
   2. Tool cards — slide-in + fade
   3. Approval gate — pulse/warn glow
   4. Status bar — smooth transitions между состояниями
   5. Timeline — smooth scroll + opacity transitions
   6. Используй `Harbor.Desktop.Animations` токены: Fast=150ms, Normal=300ms, EasingEaseOut

C. SCREENSHOT-DIFF ТЕСТЫ:
   1. Напиши тесты для Avalonia сравнивающие скриншоты с golden'ами
   2. Используй `tests/Harbor.E2E.Framework/HeadlessAvaloniaDriver.cs`
   3. Добавь baseline hashes, не просто existence check

D. ACCESSIBILITY:
   1. Проверь contrast ratios для всех цветов дизайн-системы
   2. Focus indicators в ConsoleEx и Avalonia
   3. Keyboard navigation — все действия доступны без мыши

E. KILLER FEATURES:
   1. Image preview inline: Sixel/kitty graphics
   2. Dictation input: voice-to-text в композер
   3. Tab-strip drag-reorder

F. I18N:
   1. Вынеси все строки в .resx
   2. Русский/английский локали

G. MARKDOWN RICH EDITOR:
   1. Таблицы, задачи-листы, подсветка синтаксиса

H. WORD-DIFF:
   1. Интегрируй WordDiff.cs в DiffBlock.cs если ещё не сделано

ПОСЛЕ КАЖДОГО ЭТАПА:
- запусти `dotnet run --project tests/X` для проверки
- если зелёный — атомарный коммит
- если красный — фикс перед коммитом

ФИНАЛ: список коммитов, сырые числа прогонов, статус каждого пункта выше.

EOF
echo CREATED