Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт MVP-DELIVERY.

СТАТУС: 80% базы закрыто, есть конкретные невыполненные обещания. Сначала СВЕРЬ git log/status сырым выводом.

ЖЁСТКИЕ ПРАВИЛА:
- ОДИН КОММИТ = ОДНА МАЛЕНЬКАЯ ПРАВКА. Не собирай гигантский дифф.
- После каждой правки делай атомарный коммит с понятным сообщением.
- Не прави документы, пока все пункты ниже не закрыты кодом.
- Если застрял на большой задаче — разбей на сабзадачи и делай по одной.

ЗАДАЧИ В ПОРЯДКЕ ВЫПОЛНЕНИЯ:

1. ConsoleEx прод:
   - ввод: kitty/mouse/paste/resize/Ctrl+C — есть CE-5, но доделать edge cases
   - cell-diff: ScreenBuffer + DiffEngine — есть, добавить PTY сценарии Windows/macOS
   - widgets: ChatTimelineView, PromptEditor, ApprovalGateView, DiffPreviewView — доделать по Avalonia-образу
   - PTY hardening: soak-тесты на 8+ часов

2. E2E покрытие:
   - L1 golden / L2 PTY-real / L3 живой скрин
   - recording-replay вместо MockLlmServer
   - concurrent stress тесты
   - soak тесты memory leak
   - dogfooding Harbor на самом себе
   - миграции конфигов

3. Sub-agent execution:
   - уже есть ISubAgentRunner + SubAgentRunner — доделать edge cases, тесты

4. Session polish:
   - branching/fork/revert/search
   - JSONL import/export — уже есть, доделать
   - session rename

5. Plugin commands:
   - harbor plugin install/list/uninstall CLI
   - hot-reload FileSystemWatcher
   - trust prompt project-local plugins

6. UI прод:
   - builtin контролы: StatusBadge, ChatBubble, SessionRow
   - markdown rich editor
   - word-diff highlighting
   - tab-strip drag-reorder
   - image preview inline

Дисциплина:
- один шаг → точечный тест dotnet run --project tests/X
- зелёный → коммит
- следующий шаг
- Observability-декораторы обёртывают новые компоненты автоматически
- Мессенджеры никогда в ядре src/

Финал: список коммитов, сырые числа прогонов, статус каждого пункта.

EOF
