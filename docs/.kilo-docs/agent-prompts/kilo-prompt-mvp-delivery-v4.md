Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт MVP-DELIVERY.

СТАТУС: agentLoop дробит, plugin CLI почти готов, session search/revert готов, recording-replay готов, PTY hardening готов. Сначала СВЕРЬ git log/status сырым выводом.

ЖЁСТКИЕ ПРАВИЛА — НАРУШЕНИЕ = СТОП:
1. ОДИН КОММИТ = 1–2 файла. Не более.
2. После КАЖДОГО коммита проверяй: `git status --short | wc -l` — должно быть 0 или 1–2.
3. Если у тебя накопилось >3 изменённых файла — СРАЗУ коммить точечно, не собирай монолит.
4. Не прави документы. Документы = отдельный агент, не твоя забота.
5. Не застревай на диагностике тестов. Если `dotnet test` падает с MTP/tree-filter — запускай весь проект без фильтра.
6. Каждый коммит должен иметь ЗЕЛЁНЫЙ тест: `dotnet run --project tests/X` или `dotnet test --filter "FullyQualifiedName~ClassName"`.
7. Не пиши 100+ строк за один раз. Пиши 20–40 строк, коммить, следующий шаг.

ЗАДАЧИ В ПОРЯДКЕ:
1. Plugin trust prompt — доведи до коммита. Если уже готово — коммить сейчас.
2. AgentLoop decomposition — ToolDispatcher/RetryPolicy/TokenTracker по отдельности.
3. ConsoleEx widgets — ApprovalGateView/PromptEditor до production-ready.
4. E2E concurrent stress — FileClaimRegistry тесты.
5. Session fork — ветвление сессий.
6. UI builtin контролы — markdown editor, word-diff, image preview.

EOF
