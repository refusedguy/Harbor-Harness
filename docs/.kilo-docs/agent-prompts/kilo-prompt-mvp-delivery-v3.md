Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт MVP-DELIVERY.

СТАТУС: agentLoop дробит на ToolDispatcher/RetryPolicy/TokenTracker. Сначала СВЕРЬ git log/status сырым выводом.

ЖЁСТКИЕ ПРАВИЛА:
- ОДИН КОММИТ = ОДНА МАЛЕНЬКАЯ ПРАВКА.
- Не собирай гигантский дифф.
- Не прави документы, пока код не закрыт.

АНТИ-ЗАСТРЕВАНИЕ (ОБЯЗАТЕЛЬНО):
- Если `dotnet test` с `--treenode-filter` даёт "Запущено ноль тестов" / exit 8 — СРАЗУ запускай весь класс/проект без фильтра.
- Не diagnosticsь MTP/platform pitfalls — если тест не запустился, меняй команду, а не промпт.
- Любая диагностика > 1 попытки = пропуск этого теста, следующий пункт.
- Стейбл/флейковые тесты = пропуск, не более 2 перезапусков на тест.

ЗАДАЧИ В ПОРЯДКЕ:
1. Plugin commands: CLI install/list/uninstall + hot-reload + trust prompt — если застрял на PluginsCommandTests, переключись на другой пункт
2. ConsoleEx прод: widgets/PTY hardening
3. E2E: L1/L2/L3, recording-replay
4. Sub-agent edge cases
5. Session polish: fork/revert/search
6. UI builtin контролы

Дисциплина:
- один шаг → точечный тест dotnet run --project tests/X
- зелёный → коммит
- следующий шаг
- Observability-декораторы обёртывают новые компоненты
- Мессенджеры никогда в ядре src/

EOF
