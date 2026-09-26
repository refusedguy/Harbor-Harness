Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт DOCS-SYNC — полный аудит и обновление всей документации под текущий код.

Цель: сделать так, чтобы любая часть репо (новый разработчик, сабкин, пользователь) могла открыть любой README/docs файл и получить АКТУАЛЬНУЮ информацию, которая точно соответствует коду.

СТАТУС: после MVP-DELIVERY и PROD-UI-POLISH вся кодовая база может измениться. Твоя задача — синхронизировать документацию с реальностью.

ЖЁСТКИЕ ПРАВИЛА:
1. READ-ONLY для src/**/*.cs файлов. Не меняй производственный код.
2. WRITE только для: README.md (в src/, apps/, contrib/), docs/*.md, .kilo-docs/*, XML-doc комментарии в .cs файлах.
3. Не ломай сборку. Не прави код ядра, только комментарии и маркдаун.
4. Один коммит = одна область. Не собирай гигантский дифф.

ЗАДАЧИ В ПОРЯДКЕ:

A. Полный XML-doc аудит (исправление):
   - docs/XML_DOC_AUDIT.md — аудит уже есть, но он только READ-ONLY.
   - Теперь ДОБАВЬ XML комментарии ко ВСЕМ public членам, которые в нём отмечены HIGH-priority без доков.
   - Покрыть сначала: public API surface, core abstractions, plugin contracts, tool interfaces, user-facing методы.
   - Проверь чтобы комментарии соответствовали реальной сигнатуре и поведению кода.

B. Полный docs/*.md ревайт:
   - docs/ROADMAP.md — сверь каждый чекбокс, версию, метрики, CE- спринты, plugin статусы
   - docs/PROJECT_STATUS.md — тесты, benches, known flakes, E2E coverage
   - docs/ARCHITECTURE.md, docs/ARCHITECTURE_LAYERS.md, docs/ARCHITECTURE_AUDIT_V2.md — namespaces, слои, enforcement matrix
   - docs/BUILD.md, docs/DEVELOPMENT.md — команды сборки, per-project тесты, MTP-warning, .slnx
   - docs/E2E_TESTING.md — L1/L2/L3 пирамида, PTY, recording-replay
   - docs/CONFIGURATION.md — все секции config.json, env vars, feature flags
   - docs/PLUGIN_SYSTEM.md, docs/PLUGIN_DEVELOPMENT.md — plugin hosting split, Roslyn pipeline, trust, hot-reload
   - docs/REMOTE-TAILSCALE.md — актуальность
   - docs/KILLER_FEATURES.md — что реализовано, что частично, что нет
   - docs/COMPONENT_CATALOG.md — все контролы, views, viewmodels
   - docs/ALTERNATIVE_UIS.md — Spectre, TerminalGui, Avalonia, Blazor статусы
   - docs/ANALYZERS.md, docs/ANTIPATTERNS.md, docs/CODE_PRINCIPLES_AUDIT.md — актуальность
   - docs/CI_UI_TESTS.md, docs/CENTRAL_PACKAGE_MANAGEMENT.md — если есть
   - docs/SPECTRE_TUI_DEEP_DIVE.md — если существует
   - docs/shell-recon*.md — пометь архивными если устарели

C. README coverage — 100%:
   - Убедись что КАЖДЫЙ проект в src/, apps/, contrib/ имеет README.md
   - Для каждого README: сверь purpose, public API, build command, test project, dependencies с реальным .csproj и кодом
   - Если проект изменился с прошлого DOCS-ZERO — полностью перепиши README

D. Cross-check:
   - Все ссылки внутри docs/*.md должны работать
   - Все примеры кода должны компилироваться (проверь по .csproj)
   - Все пути к файлам должны существовать
   - Версии, команды, флаги — соответствуют текущему коду

E. Аудит устаревшего:
   - Найди все упоминания "planned", "todo", "not started" в docs — если уже сделано, поставь ✅
   - Найди все упоминания устаревших namespaces (Harbor.Tui.Abstractions → Harbor.Terminal.Abstractions, Harbor.Domain → Harbor.Abstractions.Contracts)
   - Найди все упоминания старых команд/путей/архитектурных решений, которые уже не актуальны

ФИНАЛ: список коммитов, сырые числа: сколько README проверено/обновлено, сколько docs файлов, сколько XML доков добавлено, сколько битых ссылок исправлено.

EOF
