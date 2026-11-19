# ADR-068: issue #2666 (PR watch / минимальный IForgeClient) — нового slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2666](https://github.com/refusedguy/Harbor-Harness/issues/2666) прочитан
> полностью, комментариев нет. Проверены `gh pr list --search 2666` (PR,
> реализующего issue, нет; совпадения по номеру в теле других PR не считаются),
> `git log --all --grep=2666` (пусто), `docs/adr/DECISIONS.md` и дерево
> `origin/dev` (`bb507484`). Номер 068 следующий свободный после ADR-067,
> закреплённого за issue #2663. Код, тесты, гейты, пороги, golden и AOT-профиль
> этим PR не меняются.

## Status

**Decided:** по #2666 не открывается общий slice. Три из четырёх частей issue
уже влиты отдельными PR по #2686 J01-J03, четвёртая требует владельца и
публичного контракта в agent runtime. Issue остаётся OPEN, комментарий
фиксирует только хеш коммита этого ADR.

## Context

Issue #2666 выведен из `product-feature-gaps.md` разделами J01-J03. Он просит
builtin `watch_pull_request(n)` с polling `gh pr checks` и `SessionSteerEvent`
при failed check, конфликте или комментарии; первый адаптер GitHub через `gh`;
связи PR ↔ сессия в метаданных. Критерий приёмки: соблюдены лимиты wake и
ошибок, агент просыпается и чинит упавший check, есть hermetic-тест с mock gh.
Ограничения issue: база `origin/dev`, PR `--base dev`, локально не запускать
dotnet, тесты и гейты.

Инвентаризация `origin/dev` показывает, что большая часть запрошенного уже
существует. Все merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`.

| Часть #2666 | Состояние | След в дереве | PR (merge) |
|---|---|---|---|
| Минимальный `IForgeClient` с адаптером GitHub через `gh` | влито | `IForgeClient` и `GitHubForgeClient` в `apps/Harbor.App.Cli/Commands/ForgeClient.cs`: `ListOpenPullRequestsAsync`, `GetPullRequestAsync`, `GetStatusAsync`; process I/O изолировано в `GhProcessRunner` | [#2919](https://github.com/refusedguy/Harbor-Harness/pull/2919), `22dd6e9a0` |
| PR watch с wake-лимитами и hermetic-тестом | влито | `apps/Harbor.App.Cli/Commands/ForgeWatch.cs` и `PrWatchVerb.cs`: `PullRequestWatchProjection.Decide`, `MaxWakes = 5`, `MaxChecks = 100`, dedupe по сигнатуре, причины `conflict`, `failed-checks`, `wake-limit`, `unchanged`; тесты `tests/Harbor.App.Cli.Tests/ForgeWatchProjectionTests.cs` не запускают процесс | [#2925](https://github.com/refusedguy/Harbor-Harness/pull/2925), `ff39057e` |
| Связи PR ↔ сессия с обратным поиском | влито | `apps/Harbor.App.Cli/Commands/PullRequestLinkVerb.cs` | [#2935](https://github.com/refusedguy/Harbor-Harness/pull/2935), `b537183d` |
| Направление сообщения сессии | влито | approval-gated `session_steer` в `src/Harbor.Tools.Builtin/Tools/Session/SessionSteerTool.cs` | в дереве до этой проверки |

Остаток issue — сам builtin `watch_pull_request(n)`, который живёт в agent
runtime, сам крутит polling и испускает `SessionSteerEvent`. Сейчас J01
реализован как CLI-verb `harbor pr watch`, который печатает JSON решения и
возвращает управление; он не подписан на жизненный цикл сессии и не будит
агента. Триггер «комментарий» в проекции также отсутствует: `Decide` реагирует
только на упавшие check и конфликт.

## Decision

Код не писать. Конкретно:

- Влитые J01-J03 не перевыводятся. Повторная правка `IForgeClient`, wake-политики
  и связей PR ↔ сессия создала бы второй источник истины там, где отчёт требует
  одного владельца.
- Builtin `watch_pull_request` не открывается «заодно» с CLI-verb. Ему нужны
  владелец жизненного цикла, реестр долгоживущих watch, отмена по завершении
  сессии и контракт на `SessionSteerEvent`. Без этого получится ещё одна копия
  projection-логики поверх уже влитой.
- Триггер по комментарию не добавляется в существующую проекцию без владельца:
  это меняет сигнатуру dedupe и criterion приёмки J01, у которого свои тесты.
- Acceptance «агент просыпается и чинит check» сегодня нечем проверить: в дереве
  есть чистые тесты проекции, но нет E2E-поверхности с mock gh внутри agent loop.
  Такой тест открывается отдельной issue.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть, ослаблять гейты запрещено.
- Issue #2666 не закрывается. Это каталог разрыва, а не трекер одной задачи.
- Новый guard не вводится: на дереве, где проекция и wake-лимиты уже влиты и
  покрыты тестами, проверка «есть wake-лимит» была бы вакуумной.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`, их прогоняет CI.
- Пересмотр возможен по новому факту: владелец называет контракт для
  долгоживущего watch в agent runtime, потребителя `SessionSteerEvent` и
  измеримую цель. Тогда открывается отдельный PR, а не общий slice по #2666.

## References

- Issue [#2666](https://github.com/refusedguy/Harbor-Harness/issues/2666).
- Issue [#2686](https://github.com/refusedguy/Harbor-Harness/issues/2686) и его
  item J01-J03 — траектория влитых следов.
- Merged PR: [#2919](https://github.com/refusedguy/Harbor-Harness/pull/2919),
  [#2925](https://github.com/refusedguy/Harbor-Harness/pull/2925),
  [#2935](https://github.com/refusedguy/Harbor-Harness/pull/2935).
- [ADR-067](ADR-067-tool-search-deferred-tools-verdict.md): предыдущий номер.
- [DECISIONS.md](DECISIONS.md).
