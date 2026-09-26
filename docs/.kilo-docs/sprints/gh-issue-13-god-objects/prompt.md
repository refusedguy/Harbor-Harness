# SPRINT: gh-issue-13-god-objects

## Goal
Разбить оставшиеся god-objects на domain-объекты: OpenAI/Anthropic клиенты, HarborConfig, HostBuilder, SessionManager.

## Context
Критический техдолг. Декомпозиция ускорит разработку и упростит тестирование.

## Tasks
- Разбить OpenAI/Anthropic клиенты на стратегии
- Убрать HarborConfig в отдельный конфиг-провайдер
- Вынести HostBuilder в модульную композицию
- SessionManager → per-session менеджер

## Acceptance
- Нет классов > 500 LOC
- Все DI-правила пройдены
- Покрытие тестами >= 80%
