Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт Multi-Agent.

## Context
Harbor умеет запускать фоновых агентов (R25-R26: per-session UiStore, EventBus маршрутизация по SessionId),
но sub-agent execution — заглушка. TaskTool валидирует, что агент — sub-agent, и падает с "not implemented"
(см. src/Harbor.Tools.Builtin/Tools/Task/TaskTool.cs).

Concurrent agents делят один EventBroadcaster с shared mutable _currentTurn (EventBroadcaster.cs:38),
а UiStore.Transition(Func<UiState,UiState>) — escape hatch, под которым два агента могут одновременно
сломать состояние друг друга (UiStore.cs:128-141).

Цель спринта: настоящий параллельный запуск агентов без взаимных помех — то, что делает Orca
"next-gen IDE for parallel agentic development", но с .NET lock-free перфомансом.

## Tasks (выполняй по порядку)

1. **Включи TaskTool execution.**
   TaskTool внедряет IAgentRegistry, находит IAgent по имени, запускает его в новой SessionContext
   с собственным CancellationTokenSource. Результат (tool output + messages) возвращается в родительский
   ход через Result<T>.
   Acceptance: `harbor run task agent=explore "find all .cs files"` — агент реально выполняется,
   вывод попадает в чат родителя; parent agent не блокируется во время execution.

2. **Убери UiStore.Transition escape hatch.**
   TuiEffectHost и все остальные вызыватели должны dispatcher'ить UiMsg.AgentStarted / AgentEnded /
   StatusChanged вместо прямого Transition. Transition делай private.
   Acceptance: concurrent agents в разных сессиях не могут вызвать состояние "error" друг у друга
   через shared mutation; регрессионный тест с 2 параллельными PromptAsync.

3. **Сделай EventBroadcaster session-scoped.**
   Замени shared int _currentTurn на ConcurrentDictionary<string, int> _turnsBySession.
   TurnStartEvent уже несёт TurnIndex — broadcaster не должен его перезаписывать.
   Acceptance: два параллельных agent run'a не видят чужой turn index в emitted TurnEnd events.

4. **Добавь AgentLoop pipeline behaviors (§3.5).**
   Вынеси PermissionCheckBehavior, CompactionBehavior, SteeringDrainBehavior, MaxStepsBehavior,
   LoggingBehavior в отдельные классы. AgentLoop.RunAsync превращается в _pipeline.HandleAsync(PromptRequest).
   Acceptance: каждый behavior тестируется изолированно mock'ом next delegate; AgentLoop unit tests не ломаются.

## Hard Rules
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. Sub-agent runs НИКОГДА не должны отменять parent agent — каждый получает свой CTS, parent видит только tool output.
3. UiStore CAS (Interlocked.CompareExchange) остаётся lock-free; не добавляй lock'и вокруг _state.
4. Не трогай JsonlSessionStore / storage в этом спринте — только agent loop + UI state + IPC events.
5. После каждого коммита: dotnet test tests/Harbor.Application.Tests/ -c Release --no-build.

## Deliverables
- TaskTool execution wired to IAgentRegistry (sub-agent → own session → result as tool output)
- UiStore.Transition → private, все переходы через Dispatch(UiMsg)
- EventBroadcaster session-scoped turn tracking
- 5 pipeline behaviors extracted from AgentLoop
- unit tests для каждого behavior + concurrent-agent regression test
- status.json: pass/fail counts + commit SHA
- HTML-отчёт в `.kilo-docs/sprints/multi-agent/report.html`
