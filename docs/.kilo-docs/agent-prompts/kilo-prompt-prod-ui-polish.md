Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт PROD-UI-POLISH.

Цель: привести весь UI/UX до продакшенного качества. Только код и тесты, документы после.

ПОРЯДОК:
1. Screenshot-diff тесты для Avalonia/Blazor/WPF
2. Accessibility: WCAG 2.1 AA аудит + фиксы
3. Performance: 60fps timeline, zero-alloc render path, memory leak soak
4. Killer features: image preview inline, dictation input, tab-strip drag-reorder
5. i18n готовность: вынеси все строки в ресурсы
6. Markdown rich editor: TipTap-class editor в терминале
7. Word-diff highlighting: intra-line изменения

Дисциплина:
- один шаг → тест dotnet run --project tests/X
- зелёный → атомарный коммит
- Observability-декораторы на новых компонентах
- Мессенджеры только в contrib/plugins

EOF
