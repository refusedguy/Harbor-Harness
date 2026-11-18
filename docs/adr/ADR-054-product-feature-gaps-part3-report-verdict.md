# ADR-054: отчёт product-feature-gaps part 3/3 (#2686) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2686](https://github.com/refusedguy/Harbor-Harness/issues/2686) прочитан
> полностью: concrete item Q05-Q08, J01-J08 и K01-K06. Дубли проверены через
> `gh pr list --search 2686`, комментарии issue, `git log --all --grep=2686` и
> `docs/adr/DECISIONS.md` на базе `origin/dev` (`788e3713`). Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2686 не открывается новый общий slice. Девять
применимых item уже влиты отдельными PR. Оставшиеся либо перенаправлены в
соседние slice отчёта, либо прямо помечены как неприменимые, либо требуют
собственного владельца и критерия приёмки. Issue остаётся OPEN как источник
сравнения; комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2686 — self-contained отчёт `product-feature-gaps` part 3/3 с тремя
группами: провайдеры и модели (Q), forge/PR/issues (J) и DX/диагностика (K).
Инвентаризация `origin/dev` (`788e3713`) и истории PR показывает девять влитых
item. Все merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) |
|---|---|---|
| Q06, source-tagged slash commands | источник `builtin|provider|skill` в `/help` | [#2795](https://github.com/refusedguy/Harbor-Harness/pull/2795), `e620a41f` |
| J01, PR watch | failed check / conflict / comment → wake с лимитами | [#2925](https://github.com/refusedguy/Harbor-Harness/pull/2925), `ff39057e` |
| J02, `IForgeClient` | первый адаптер GitHub через `gh` | [#2919](https://github.com/refusedguy/Harbor-Harness/pull/2919), `22dd6e9a` |
| J03, linked PRs | связь PR ↔ сессия и reverse lookup | [#2935](https://github.com/refusedguy/Harbor-Harness/pull/2935), `b537183d` |
| J04, review guide | `harbor pr guide` со списком точек ревью | [#2869](https://github.com/refusedguy/Harbor-Harness/pull/2869), `bd98e8aa` |
| J05, issues as tools | `issue create`/`state` через `gh` | [#2949](https://github.com/refusedguy/Harbor-Harness/pull/2949), `d0d38cf6` |
| J06, worktree handoff | `harbor handoff` с PR другой сессии | [#2916](https://github.com/refusedguy/Harbor-Harness/pull/2916), `a30bf9d` |
| K05, retention/cleanup | планирование очистки сессий в `sessions maintenance` | [#2943](https://github.com/refusedguy/Harbor-Harness/pull/2943), `540c0032` |
| Q07, option descriptors | валидация опций модели до turn | [#2931](https://github.com/refusedguy/Harbor-Harness/pull/2931), `295ae923` |

Комментарии в issue фиксируют те же хеши. Issue закрывался после первого
влитого slice (#2795) и был переоткрыт владельцем: он остаётся каталогом
разрывов, а не трекером одной задачи.

Оставшиеся item не образуют одного владельца:

- Прямой отказ в отчёте: J08 (source-control write scope на remote) и K02
  (облачный webhook relay) помечены `[X]`; Harbor — локальный терминальный
  агент, а K01 использует локальный daemon без relay.
- Уже покрыто соседними slice: J07 дублирует W07 из part 2/3 (#2685); K03
  перенаправлен в R12, K04 — в T05; K06 (`check:changed`-гейт) дублирует
  `docs/CI_GATES.md` и `tools/check-gate-policy.py`. K01 (cron + webhook proof)
  реализуется отдельным срезом [#2957](https://github.com/refusedguy/Harbor-Harness/pull/2957)
  по #2674 G11 (PR OPEN на дату решения); второй slice по тому же контракту
  не нужен.
- Требуют владельца и измеримой цели: Q05 (`minProviderVersion` и warning в
  `/models`), Q08 (маршрутизация по классу задачи в конфиге, приоритет `[C]`) и
  J07/W07 (импорт проекта из forge CLI). У каждого свой публичный контракт,
  свой риск для AOT и свой критерий приёмки; одним прогоном это не проверить.

Отчёт part 3/3 соседствует с part 1/3 (#2684) и part 2/3 (#2685), покрытыми
[ADR-046](ADR-046-product-feature-gaps-part1-report-verdict.md) и
[ADR-045](ADR-045-product-feature-gaps-part2-report-verdict.md). Этот ADR не
перевыводит их item.

## Decision

Код не писать. Конкретно:

- Девять влитых item не перевыводятся. Повторная правка `/help`, `pr watch`,
  `IForgeClient`, linked PR, `pr guide`, issue tools, handoff, retention и option
  validation создала бы второй источник истины там, где отчёт требует одного
  владельца.
- Оставшиеся item не объединяются в meta-PR. Прямо неприменимые не портируются;
  перенаправленные не дублируются; новые открываются отдельной issue и PR, как
  это уже сделано для девяти влитых item.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне CI.
- Issue #2686 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты, перенаправлены
  или требуют владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2686.
