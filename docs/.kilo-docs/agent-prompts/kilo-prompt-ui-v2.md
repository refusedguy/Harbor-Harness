Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт UI-V2.

СТАТУС: UI-FINAL закрыт. Твоя задача — только UI/UX код, не возвращайся к сессиям/плагинам/агенту/storage.

УЖЕ СДЕЛАНО (не трогай):
1. `Harbor.DesignSystem` проект с `ColorPalette`, `TerminalColorPalette`, `DesignTokens`
2. `ChatPalette.cs` подключен к `TerminalColorPalette`
3. Avalonia `HarborDesignTokens.axaml` создан
4. Blazor CSS custom properties добавлены
5. ConsoleEx widgets используют дизайн-токены

ЖЁСТКИЕ ПРАВИЛА:
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. После каждого коммита проверяй git status — должно быть 0 или 1–2 файла.
3. Документы уже готовы: docs/next-sprint-ui-v2-20260827.html — их достаточно, не пиши новые.
4. Мессенджеры НИКОГДА в ядре src/ — только contrib/plugins.

ДИЗАЙН-СИСТЕМА (обязательно используй):
- Terminal colors: accent #39bae6, success #7fd962, warning #ffb454, error #ff6b6b, tool #d2a6ff, system #f29668
- Surfaces: bg #0a0e14, panel #0d1117, surface #131820, surface2 #1a1f2b, border #1f2430, muted #5c6773
- Typography: JetBrains Mono 12px для TUI, system font для desktop
- Spacing: xs 4px, sm 8px, md 12px, lg 16px, xl 24px
- Animation: micro 100–150ms, standard 200–300ms, ease-out entrance, ease-in exit

ЗАДАЧИ В ПОРЯДКЕ ВЫПОЛНЕНИЯ:

PRIORITY 1: АНИМАЦИИ & TRANSITIONS (must have)
1. Panel fade-in/slide-in: 150ms fast, 300ms normal, ease-out
2. Tool cards slide-in + fade
3. Approval gate pulse/warn glow
4. Status bar smooth transitions между состояниями
5. Timeline smooth scroll + opacity transitions
6. Spring physics для panel resizing

PRIORITY 2: COMMAND PALETTE & NAVIGATION
1. Command Palette (ctrl+p) с fuzzy search + suggested commands
2. Quick-switch slots для recent sessions
3. Leader-key system для power users
4. Vim mode toggle для composer

PRIORITY 3: THEME SYSTEM EXPANSION
1. Multiple built-in themes: Harbor Dark, Light, Warm, Cool
2. Custom JSON theme support с live-reload
3. OSC 11 auto-theme detection
4. Per-component theme overrides

PRIORITY 4: SIDEBAR & CONTEXT PANELS
1. Optional right sidebar (42px default, auto-show на wide terminals)
2. Plugin-extensible sidebar slots
3. Session info, token counter, model picker в sidebar
4. Modified files / LSP status / MCP status widgets

PRIORITY 5: WEB UI POLISH
1. Floating pill composer (ChatGPT pattern)
2. 260px collapsible sidebar с session list
3. Spring-physics button animations
4. Dyslexic font support

PRIORITY 6: KILLER FEATURES
1. Ambient mascot/avatar (Petdex-style)
2. Cost/token display в prompt footer
3. Retry countdown timers
4. Copy-on-select
5. Braille streaming spinner

DEFERRED (не этот спринт):
- Multi-agent collaboration modes (Plan/Pair/Execute)
- /ide IPC bridge к external editors
- Worktree isolation для parallel sessions
- Shader-like post-render effects (tachyonfx)

ПОСЛЕ КАЖДОГО ЭТАПА:
- запусти dotnet run --project tests/X для проверки
- если зелёный — атомарный коммит
- если красный — фикс перед коммитом

ФИНАЛ: список коммитов, сырые числа прогонов, статус каждого пункта выше.

EOF
echo CREATED