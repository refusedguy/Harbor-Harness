# ADR-075: issue #2664 (изолированные worktree для subagent) без общего slice

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2664](https://github.com/refusedguy/Harbor-Harness/issues/2664) прочитан
> полностью; до этого slice комментариев не было. Проверены
> `gh pr list --search 2664` (PR по issue нет),
> `git log origin/dev --grep 2664` (ноль коммитов),
> `docs/adr/DECISIONS.md` и дерево `origin/dev` (`56608667`). Инвентаризованные
> файлы не изменились и на перебазированном `8f38b89c`. Номер 075 закреплён
> за этим slice открытым PR #2999: пока шла подготовка, 071-074 заняли
> параллельные ADR по смежным issue. Код, тесты, гейты, пороги, golden и
> AOT-профиль этим PR не меняются.

## Status

**Decided:** по #2664 не открывается общий slice. Изоляция рабочей копии,
владение worktree с handoff и аренда, блокирующая rewind, имеют разные
интерфейсы и разные критерии приёмки. Issue остаётся OPEN; комментарий
фиксирует только хеш коммита этого ADR.

## Context

Issue #2664 выведен из `product-feature-gaps.md` (W03-W06). Он просит три
вещи: `delegate_task(isolated: true)` создаёт worktree через
`WorkspaceMaterializer`; `worktree_status/handoff` хранит владельца и запрещает
кражу; `IWorkspaceLease` блокирует rewind, пока worktree занят другой
сессией. Критерий приёмки: два параллельных child'а не конфликтуют, а
`git worktree remove` отказывается при uncommitted/unmerged. Ограничение
issue: база `origin/dev`, PR `--base dev`, локально не запускать dotnet,
тесты и гейты.

Инвентаризация `origin/dev` (`56608667`) показывает, что готового целого здесь
нет, а предпосылки разрознены.

| Часть #2664 | Что есть в дереве | Что отсутствует |
|---|---|---|
| Изоляция через `WorkspaceMaterializer` | `src/Harbor.ChangeTracking/WorkspaceMaterializer.cs` — статический материализатор из epic #42 stage S2: `git worktree add --detach`, run manifest, fail-closed очистка при ошибке. | Инструмента `delegate_task` и флага `isolated` в дереве нет. Нет моста от вызова subagent к контракту `WorkspaceContract`. |
| Владение и handoff | `harbor handoff` (`apps/Harbor.App.Cli/Commands/HandoffVerb.cs`) пакетит PR для другой сессии через `IForgeClient`. В `Harbor.ChangeTracking` есть `FileClaimRegistry`. | `worktree_status` и владельца worktree нет. Правила «некражи» нет. Существующий `handoff` решает другую задачу и не должен перегружаться семантикой владения рабочей копией. |
| Аренда против rewind | Checkpoint и правки сессии живут в `SessionForkService`, `ISessionCheckpointStore` и командах сессий. | Интерфейса `IWorkspaceLease` нет. Нет guard'а, который связывает занятый worktree с запретом rewind. Критерий issue сегодня нечем проверить. |

Ограничения issue задают три разных публичных поверхности: инструмент с
опцией, CLI-проекцию с владельцем и контракт аренды в слое сессий. Ни одна из
них не является частью другой по умолчанию: материализатор уже существует и
не знает про subagent, `harbor handoff` уже пакетит PR и не знает про
worktree, а rewind не знает про аренду.

## Decision

Код не писать. Конкретно:

- Флаг `isolated` и вызов материализатора не объединяются с проекцией владения
  в один PR. У инструмента свой интерфейс и свой путь через
  `WorkspaceContract`, у владения свой CLI-контракт.
- Существующий `harbor handoff` не расширяется семантикой worktree: это другой
  поток данных, и перегрузка сломала бы его текущий критерий приёмки.
- `IWorkspaceLease` не добавляется «заодно». Пока нет аренды, проверка
  «rewind заблокирован» была бы вакуумной, а тест на конфликтующие правки
  не смог бы проверить запрет.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть, ослаблять гейты запрещено.
- Issue #2664 не закрывается. Это каталог разрыва, а не трекер одной задачи.
  Каждая из трёх частей открывается отдельным PR с владельцем и измеримой
  целью.
- Новый guard не вводится: на дереве без контракта аренды и владельца
  worktree проверка «два child'а не конфликтуют» не имеет наблюдаемого
  состояния.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`, их прогоняет CI.
- Пересмотр возможен по новому факту: владелец называет одну из трёх частей,
  потребителя и измеримую цель. Тогда открывается отдельный PR по этой части,
  а не общий slice по #2664.

## References

- Issue [#2664](https://github.com/refusedguy/Harbor-Harness/issues/2664).
- `src/Harbor.ChangeTracking/WorkspaceMaterializer.cs`,
  `apps/Harbor.App.Cli/Commands/HandoffVerb.cs`.
- [ADR-074](ADR-074-permission-modes-session-overlay-verdict.md): предыдущий
  номер в `DECISIONS.md`.
- [DECISIONS.md](DECISIONS.md).
