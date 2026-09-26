Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт AGENTLOOP-DECOMP.

Цель: декомпозиция AgentLoop.cs (681 строка) на отдельные сервисы.
- Извлеки ToolDispatcher (параллельное/последовательное выполнение тулов)
- Извлеки RetryPolicy (экспоненциальный backoff для транзиентных LLM ошибок)
- Извлеки TokenTracker (агрегация usage)
- Сохрани публичные контракты AgentLoop.cs — backward compatibility обязательна
- Обнови соответствующие README/PLAN файлы в группах проектов
- Не ломай сборку, не прави UI/тесты без необходимости
- Если правки закончены — закоммить с сообщением "feat(agentloop): decompose into ToolDispatcher + RetryPolicy + TokenTracker"
EOF
echo CREATED