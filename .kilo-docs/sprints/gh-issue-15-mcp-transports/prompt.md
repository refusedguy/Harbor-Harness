# SPRINT: gh-issue-15-mcp-transports

## Goal
Довести MCP-слой Harbor до production-уровня: добавить HTTP/SSE транспорты, resources, prompts, OAuth, lazy reconnect.

## Context
Сейчас MCP имеет минимальную реализацию. Нужно расширить до полноценного протокола, как у конкурентов.

## Tasks
- Добавить HTTP/SSE транспорты поверх MCP
- Реализовать resources и prompts API
- Добавить OAuth аутентификацию для MCP
- Lazy reconnect при обрыве соединения
- Тесты и документация

## Acceptance
- Все транспорты работают в CI
- OAuth flow покрыт тестами
- Lazy reconnect стабилен при нагрузке
