Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт MVP-DELIVERY.

СТАТУС: 80% базы закрыто, но есть конкретные невыполненные обещания из аудита/сессии. Сначала СВЕРЬ git log/status сырым выводом.

ТВОЁ ТЗ — делать код, не документы, пока все пункты не закрыты:

1. ConsoleEx прод:
   - ввод: kitty/mouse/paste/resize/Ctrl+C — есть CE-5, но доделать edge cases и документацию внутри кода
   - cell-diff: ScreenBuffer + DiffEngine — есть, но не покрыт реальными PTY сценариями на Windows/macOS
   - widgets: ChatTimelineView, PromptEditor, ApprovalGateView, DiffPreviewView — переписать/дополнить по образцу Avalonia, чтобы builtin views были production-ready
   - PTY hardening: есть 8 сценариев, но нет soak-тестов на 8+ часов

2. E2E покрытие:
   - L1 golden / L2 PTY-real / L3 живой скрин — пирамида как у codex
   - реальный LLM-трафик в тестах: recording-replay вместо MockLlmServer
   - concurrent stress: FileClaimRegistry конкурентные тесты
   - долгоживущие сессии: memory leak soak после 500 сообщений
   - реальные репозитории: dogfooding Harbor на самом себе
   - миграции конфигов: старый config.json читается после обновления

3. Sub-agent execution:
   - TaskTool сейчас только валидирует — реализовать реальный запуск sub-agent в изолированном контексте с own session
   - результат возвращаться как tool output parent-агенту
   - поддержка explore/plan/custom sub-agents

4. Session polish:
   - branching/fork/revert/search
   - JSONL import/export
   - session rename

5. Plugin commands:
   - harbor plugin install/list/uninstall CLI
   - hot-reload FileSystemWatcher
   - trust prompt project-local plugins

6. UI прод:
   - встроенные контролы: StatusBadge, ChatBubble, SessionRow — доделать все 3 платформы
   - Markdown rich editor: текущий базовый, нужно продвинутый
   - intra-line word-diff highlighting
   - tab-strip drag-reorder + close-gesture
   - image preview inline
   - dictation/speech-to-text

Дисциплина:
- один шаг → точечный тест dotnet run --project tests/X (НЕ dotnet test)
- зелёный → атомарный коммит
- следующий шаг
- Observability-декораторы обёртывают новые компоненты автоматически
- Мессенджеры никогда в ядре src/ — только contrib/plugins

Финал: список коммитов, сырые числа прогонов, статус каждого пункта выше.

EOF
