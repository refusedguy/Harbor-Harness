# ADR-073: issue #2661 (MCP elicitation и roots как client capabilities)

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2661](https://github.com/refusedguy/Harbor-Harness/issues/2661) прочитан
> полностью, комментариев нет. `gh pr list --search 2661` не показывает PR,
> реализующего issue; `git log --all --grep=elicitation` и `--grep=roots`
> не показывают влитого slice. Номер 073 выбран потому, что 071 и 072 заняты
> другими slice выше по `origin/dev` (`11db4c63`). Код, тесты, гейты, пороги,
> golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по #2661 не открывается один общий PR. Три части issue меняют
разные контракты и проверяются разными тестами. Issue остаётся OPEN,
комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue выведен из `product-feature-gaps.md` (A05/C01/C02). Он просит `roots/list`
и `notifications/roots/list_changed` при смене workingDirectory, плюс
`ElicitationRequestedEvent` с CLI-формой: обязательные поля, defaults, таймаут
300 с и Ctrl+C. Acceptance: mock MCP-сервер запрашивает структурированные данные
и получает ответ, не роняя turn.

Инвентаризация `origin/dev` (`11db4c63`) показывает, что готового нет, но
предпосылки разрознены.

| Часть #2661 | Что есть в дереве | Что отсутствует |
|---|---|---|
| Client capabilities | `McpRegistry.InvokeAsync` (`src/Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs:360`) шлёт один запрос через `McpJsonRpcTransport`; `ProcessResponse` (`McpRegistry.cs:450`) только собирает `instructions`. Вызова `initialize` из клиента нет: строка `"initialize"` в `src/` встречается только в LSP и в plugin-host сервере. | `capabilities.roots.listChanged` и `capabilities.elicitation` в initialize не объявляются, `notifications/initialized` не шлётся. |
| Разбор входящих запросов | `McpJsonRpcTransport.ReadAsync` (`McpJsonRpcTransport.cs:31`) пропускает уведомления без `id` (#2228); запрос сервера с `id` вернётся как ответ на ожидаемый вызов. | Демультиплексор ответов и запросов сервера. `roots/list` и `elicitation/create` не обрабатываются. |
| CLI-форма и событие | `IUserQuestionService`, `UserQuestionService` и `PlainUserQuestionAsker` уже дают форму с таймаутом (по умолчанию 120 с, максимум 10 минут, значит 300 с влезает) и отменой через `ct`; `AgentEvent` и `IEventBus` уже есть. | `ElicitationRequestedEvent` в `[JsonDerivedType]`-union; преобразование JSON Schema полей в `UserQuestionRequest` и ответа в MCP `accept/decline/cancel`. |

[`docs/specs/06-mcp.md`](../specs/06-mcp.md) §4.1 по-прежнему помечает
`elicitation` как не поддерживаемый, а `roots` как MVP. Issue #2661 меняет это
решение, поэтому этот ADR фиксирует новый курс для client capabilities;
сам spec правится отдельным slice вместе с кодом.

## Decision

Код не писать. Порядок трёх slice и границы каждого зафиксированы заранее:

1. **Handshake и roots.** Внутри `Harbor.Tools.Builtin/Tools/Mcp` добавить
   сборку `initialize` с `capabilities.roots.listChanged=true` и
   `capabilities.elicitation={}`, отправку `notifications/initialized` и Pump,
   который читает кадры и отвечает на `roots/list`. Roots берутся из
   `Environment.CurrentDirectory` в формате `file://`. При смене каталога Pump
   шлёт `notifications/roots/list_changed` перед следующим запросом. Публичный
   `IMcpRegistry` не меняется: весь demux остаётся internal.
2. **Elicitation.** `ElicitationRequestedEvent` добавляется в
   `src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs` с registration
   `[JsonDerivedType(typeof(ElicitationRequestedEvent), "mcp_elicitation_requested")]`;
   типы запроса и результата живут в `Harbor.Abstractions/Interaction`, responder
   в `Harbor.Application/Interaction`. Responder публикует событие через
   `IEventBus`, а ответ пользователя получает через `IUserQuestionService` с
   окном 300 с. Ctrl+C остаётся отменой, а не вердиктом: так же, как в
   `UserQuestionService`. Обязательные поля и defaults читаются из
   `requestedSchema`, ответ мапится в `accept/decline/cancel` с `content`.
3. **Тест с mock MCP-сервером.** В `tests/Harbor.Tools.Builtin.Tests/` Pump
   прогоняется по scripted in/out потокам: сервер присылает
   `elicitation/create` посреди `tools/list`, клиент отвечает на него и всё ещё
   возвращает ответ `tools/list` вызывающему. Это и есть acceptance issue.

Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Новый guard
не вводится: на дереве без Pump проверка «все capabilities работают» была бы
вакуумной. Issue не закрывается. Пересмотр по новому факту: владелец называет
потребителя и mock-тест для конкретной части, тогда открывается один из трёх
slice, а не общий PR по #2661.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`, их прогоняет CI.
- Пока три slice не влиты, `docs/specs/06-mcp.md` продолжает описывать
  elicitation как выключенный; это известное расхождение, зафиксированное выше.

## References

- Issue [#2661](https://github.com/refusedguy/Harbor-Harness/issues/2661).
- [ADR-067](ADR-067-tool-search-deferred-tools-verdict.md): та же форма решения
  по concrete issue с несколькими разными интерфейсами.
- [ADR-072](ADR-072-file-rewind-preimage-design.md): предыдущий номер.
- [DECISIONS.md](DECISIONS.md).
