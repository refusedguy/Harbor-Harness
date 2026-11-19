# ADR-071: issue #2662 (execution graph и command receipts) без общего slice

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2662](https://github.com/refusedguy/Harbor-Harness/issues/2662) прочитан
> полностью, комментариев нет. Проверены `gh pr list --search 2662 --state all`
> (PR, реализующего issue, нет), `docs/adr/DECISIONS.md` и дерево `origin/dev`
> (`27c312f7`). Первая версия PR использовала номер 069; пока PR был открыт,
> ADR-069 закрепили за #2700, а ADR-070 за #2687, поэтому этот ADR перенумерован
> в 071. Код, тесты, гейты, пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по #2662 не открывается общий slice. Граф исполнения уже влит
отдельными item #2673 A3 и A9. Идемпотентные command receipts и барьер перед
`AgentEndEvent` имеют свои контракты и свои критерии приёмки. Issue остаётся
OPEN, комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2662 выведен из `product-feature-gaps.md` разделами M01/M03/M05/M07.
Он просит `ExecutionNode { nodeId, runId, kind }`, интерфейс
`ICommandReceiptStore`, запись receipt и события в одной sqlite-транзакции,
`RunAttempt` с причиной и `CompletionBarrier` до `AgentEndEvent`. Критерий
приёмки: повтор `commandId` не создаёт побочных эффектов, а порядок событий
зафиксирован golden-тестом. Ограничения issue: база `origin/dev`, PR сразу
`--base dev`, локально не запускать dotnet, тесты и гейты.

Инвентаризация `origin/dev` (`27c312f7`) показывает, что первая часть уже
влита, а остальные две не имеют владельца в дереве.

| Часть #2662 | Что есть в дереве | Источник или разрыв |
|---|---|---|
| Граф исполнения (M01) | `ExecutionNodeId`, `ExecutionNodeKind`, `ExecutionNodeState`, `ExecutionNode` и `ExecutionNodeProjection` в `src/Harbor.AgentRuntime/Agents/ExecutionNodeProjection.cs`; `ActivityEntry` и `ActivityProjection` в `src/Harbor.AgentRuntime/Agents/ActivityProjection.cs`. Оба проекта чистые: превращают события агента в run-scoped представление. | #2673 A3, PR [#2863](https://github.com/refusedguy/Harbor-Harness/pull/2863), коммит `a72c5786`; #2673 A9, PR [#2871](https://github.com/refusedguy/Harbor-Harness/pull/2871), коммит `1006894c`. |
| Идемпотентные command receipts (M03) | SQLite-бэкенд сессий есть в `src/Harbor.Storage.Sqlite`, но `ICommandReceiptStore`, сущности receipt и ключа `commandId` в `src/` и `apps/` нет. | Транзакция «receipt + event» требует нового контракта в абстракциях и миграции хранилища. Этого в дереве нет. |
| `RunAttempt` и `CompletionBarrier` (M05/M07) | `AgentEndEvent` публикуется как терминальное событие прогона; типа `RunAttempt` и `CompletionBarrier` в дереве нет. | Барьер меняет порядок публикации событий и контракт завершения раннера, а не только добавляет тип. |

Смежные item из #2673 и 2704 уже зафиксированы ADR-060 и ADR-061. Этот ADR не
перевыводит влитый граф и не меняет эту границу.

## Decision

Код не писать. Конкретно:

- Граф исполнения не перевыводится. Тот же `ExecutionNodeProjection` и лента
  активности уже влиты через #2863 и #2871. Повторная правка создала бы второй
  источник истины и конфликт с уже влитыми проекциями.
- Command receipts не объединяются с барьером в один PR. У receipt store свой
  контракт, sqlite-транзакция и ключ идемпотентности; у барьера свой путь через
  event bus и завершение раннера. Общий PR не дал бы единого способа доказать
  ни «повтор не создаёт эффектов», ни «порядок событий зафиксирован».
- Барьер не добавляется «заодно» к графу. Без контракта, который определяет,
  какие события обязаны прийти до `AgentEndEvent`, criterion из #2662 нечем
  проверить, а golden-тест порядка событий был бы тестом текущей удачи.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть, ослаблять гейты запрещено.
- Issue #2662 не закрывается. Это каталог разрыва, а не трекер одной задачи.
  Каждая из двух недостающих частей открывается отдельным PR с владельцем и
  измеримой целью.
- Новый guard не вводится. На дереве, где граф влит, а receipt store и барьер
  требуют владельца, проверка их наличия была бы вакуумной.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`, их прогоняет CI.
- Пересмотр возможен по новому факту: владелец называет конкретную часть,
  потребителя контракта receipt store или барьера и измеримую цель. Тогда
  открывается отдельный PR, а не общий slice по #2662.

## References

- Issue [#2662](https://github.com/refusedguy/Harbor-Harness/issues/2662).
- Merged PR [#2863](https://github.com/refusedguy/Harbor-Harness/pull/2863),
  #2673 A3: execution graph.
- Merged PR [#2871](https://github.com/refusedguy/Harbor-Harness/pull/2871),
  #2673 A9: activity feed.
- [ADR-060](ADR-060-t3code-features-part2-report-verdict.md): граница item
  #2674.
- [ADR-070](ADR-070-dx-docs-gaps-post-verdict.md): предыдущий влитый номер.
- [DECISIONS.md](DECISIONS.md).
