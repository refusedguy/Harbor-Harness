# ADR-066: issue #2667 (scheduler и webhooks в daemon) без общего slice

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2667](https://github.com/refusedguy/Harbor-Harness/issues/2667) прочитан
> полностью. Проверены `gh issue view 2667 --comments`,
> `gh pr list --search 2667` (ноль PR),
> `docs/adr/DECISIONS.md` и дерево `origin/dev` (`74060665`). Номер 066
> следующий свободный после ADR-065, который PR #2987 закрепил за отчётом
> #2706. Инвентаризация сделана на `74060665`, ветка перебазирована на
> `2e94fa29`, где изменился только ADR-065. Код, тесты, гейты, пороги, golden
> и AOT-профиль этим PR не меняются.

## Status

**Decided:** по #2667 не открывается общий slice. Отправительная половина
уже в дереве через #2674 G11. Принимающая половина и политика уведомлений
требуют своего владельца и публичного контракта. Issue остаётся OPEN,
комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2667 выведен из `product-feature-gaps.md` разделами K01 и N01-N03, N07.
Он просит `harbor task schedule --cron "..." --prompt ...` и
`harbor daemon hook <name>` с HMAC-проверкой, фильтром, dedupe и rate-limit
60 req/min. Результат automation должен уходить в `INotificationPolicy` с
snooze-правилами и OSC 777 для headless. Критерий приёмки: сноузнутая сессия
просыпается только при новом результате, уведомление не спамит.

Инвентаризация `origin/dev` (`74060665`) показывает четыре готовые части.

| Часть #2667 | Что есть в дереве | Источник |
|---|---|---|
| `harbor task schedule --cron --prompt`, список и удаление | `apps/Harbor.App.Cli/Commands/TaskScheduleVerb.cs`, five-field cron, `tasks.json`, `harbor task list/remove/next` | #2674 G11, PR [#2957](https://github.com/refusedguy/Harbor-Harness/pull/2957), merge `0068e8e9` |
| HMAC-доказательство webhook | `WebhookSignature` в том же файле, HMAC-SHA256 и `CryptographicOperations.FixedTimeEquals` | #2674 G11 |
| `harbor daemon`, `serve`, `headless` | `apps/Harbor.App.Cli/Commands/CafDaemonIpcServeMcpVerbs.cs` и `CafMiscVerbs.cs` | #1289, #1290 |
| OSC 777 для headless | `Osc777Notify`, `NotifyProbe`, `Osc777NotificationBackend`, env `HARBOR_OSC777` | CellForge engine и `Harbor.Tui.Notifications` |

ADR-060 прямо относит недостающий HTTP-эндпоинт `harbor daemon hook` и
fire-диспетчер к тому же item G11 и не перевыводит их. Этот ADR не отменяет
эту границу и не открывает повторный PR по G11.

Остальное в #2667 не имеет владельца в дереве:

- `harbor daemon hook <name>` как принимающий HTTP-эндпоинт. В CLI нет
  регистрации `hook`, есть только `task`, `daemon`, `serve` и `headless`.
  Добавление слушателя меняет сетевую границу daemon и требует решения о
  bind-адресе, таймаутах и отказе при недоступном порту.
- Dedupe и rate-limit 60 req/min. В дереве нет ключа по `(sessionId,
  toolCallId)` для уведомлений и нет `RateLimitWindow` в provider model.
  Блок `HMAC` сам по себе не предотвращает повторную доставку.
- `INotificationPolicy` с snooze-правилами. В `Harbor.Abstractions` есть
  `INotificationProcessRunner`, который запускает процесс с argv и не знает
  ни расписания, ни состояния сессии. Правило «будить только при новом
  результате» требует связи между планировщиком, event bus и состоянием
  сессии, которой сегодня нет.
- Проверка критерия приёмки. `INotificationPolicy` и hook-эндпоинт нельзя
  прогнать без dotnet и тестов, а ограничения #2667 запрещают локальный
  dotnet, тесты и гейты.

## Decision

Код не писать. Конкретно:

- Отправительная половина #2667 не перевыводится. Тот же
  `harbor task schedule`, cron и HMAC уже влиты через #2957. Повторная правка
  создала бы второй источник истины.
- Принимающая половина не объединяется с отправительной в один PR. У неё
  свой публичный контракт, сетевая граница и критерий приёмки. Её открывают
  отдельной issue и PR, как это сделано для G11.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть, ослаблять гейты запрещено.
- Issue #2667 не закрывается. Это каталог разрыва, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`, их прогоняет CI.
- Новый guard не вводится. На дереве, где исполнимая часть влита, а остальная
  требует владельца, guard был бы вакуумным.
- Пересмотр возможен по новому факту: владелец называет конкретную часть,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2667.

## References

- Issue [#2667](https://github.com/refusedguy/Harbor-Harness/issues/2667).
- Merged PR [#2957](https://github.com/refusedguy/Harbor-Harness/pull/2957), G11
  из #2674.
- [ADR-060](ADR-060-t3code-features-part2-report-verdict.md), граница G11.
- [ADR-065](ADR-065-tui-ui-primitives-catalog-post-verdict.md), предыдущий номер.
- [DECISIONS.md](DECISIONS.md).
