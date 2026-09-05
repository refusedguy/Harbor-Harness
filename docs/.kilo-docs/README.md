# .kilo-docs — материалы спринтов и спринт-цепочка

Исследовательские заметки/аудиты, на которых строились спринты Harbor, плюс инфраструктура
автономной спринт-цепочки. Это НЕ живая документация: каждый файл — снимок состояния репо
на указанную в нём дату/HEAD. Актуальная истина: [docs/ROADMAP.md](../docs/ROADMAP.md),
[docs/PROJECT_STATUS.md](../docs/PROJECT_STATUS.md), [DECISIONS.md](../DECISIONS.md).

**Правило чтения:** баннер «СТАТУС» в шапке файла приоритетнее текста внутри — он помечает,
что изменилось с момента снятия снимка.

## Спринт-цепочка

- `sprint-chain.md` — очередь спринтов, формат `sprint|NAME|MODEL|PROMPT_PATH`
  (все поля обязательные; MODEL — полный ID с провайдером; новые строки добавляются сверху вниз);
- `scripts/sprint-chain.sh` — диспетчер: читает очередь и запускает промпты через kilo-dispatch;
  после каждого спринта продвигает BASE_SHA по коммиту — если прогресса нет, цепочка останавливается;
- `agent-prompts/` — промпты спринтов (например, `kilo-prompt-docs-zero.md` для DOCS-ZERO).

## Материалы исследований

| Файл | Тип | Статус относительно кода |
|---|---|---|
| `consoleex-design.md` | дизайн-библия ввода/конвейера ConsoleEx | ✅ реализован CE-0…CE-4 (+PTY CE-5) — ADR-003 |
| `consoleex-celldiff.md` | дизайн cell-diff ядра рендера | ✅ реализован CE-1…CE-3 + CE-5 |
| `consoleex-widgets.md` | дизайн виджетов/views | ✅ реализован CE-2…CE-4 |
| `consoleex-perf.md` | перф-аудит рендера и ввода | ✅ применён; 0-alloc steady-state закреплён тестами |
| `cse-full-design.md` | каталог CSE API + план внедрения | ✅ ROP-B/C/D закрыты (ADR-002); числа-снимок устарели |
| `rop-final-mile.md` | census try/catch → Result-рельсы | ✅ выполнен; ResultGuard удалён как дубликат канона |
| `competitor-tools-verdict.md` | аудит tools конкурентов (25.08) | ⚠️ частично: SSRF-guard webfetch готов; todo/question tool, MCP annotations, S1-admission — кандидаты |
| `memory-audit-freshness.md` | внешнее исследование freshness (Graft) | ℹ️ point-in-time; внешние пути могут отсутствовать |
