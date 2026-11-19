# ADR-081: отчёт dx-docs-gaps (#2687) после вердикта — влита идея 3

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2687](https://github.com/refusedguy/Harbor-Harness/issues/2687) прочитан
> полностью. [ADR-043](ADR-043-dx-docs-gaps-report-verdict.md) зафиксировал
> вердикт на базе `8561cd87`, [ADR-070](ADR-070-dx-docs-gaps-post-verdict.md)
> дополнил его на базе `56608667` и прямо назвал таблицу путей установки
> (идея 3) отсутствующей. После этого в `dev` влит slice, которого тот ADR не
> знал. Факты проверены через `gh issue view 2687 --comments`, `gh pr view` и
> `git merge-base --is-ancestor` на базе `origin/dev` (`36fcd2a1`). Номер 079
> оказался занят ADR по issue #1435 (PR #3010), поэтому эта запись
> перенумерована в 081; 078 закреплён за открытым PR #3008. Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** вердикты ADR-043 и ADR-070 сохраняются: одного общего slice по
issue #2687 нет. Новая влитая идея не перевыводится. Issue остаётся OPEN как
источник сравнения; комментарий фиксирует только хеш коммита этого ADR.

## Context

ADR-070 на базе `56608667` нашёл три slice после вердикта ADR-043 (идеи 1, 4 и
14) и отдельно указал, что `docs/CLI_REFERENCE.md`, `docs/MIGRATION.md` и
таблица путей установки в `docs/GETTING_STARTED.md` по-прежнему отсутствуют.
Свежая проверка `origin/dev` (`36fcd2a1`) закрывает одну из этих трёх позиций:

| Идея отчёта | Что требовалось | След в дереве | Slice | Merge commit | PR |
|---|---|---|---|---|---|
| 3: таблица путей установки | таблица реальных каналов установки с командой проверки версии в `docs/GETTING_STARTED.md` | `docs/GETTING_STARTED.md`, раздел `Install paths` (`GETTING_STARTED.md:138`) | `8cdb0082` | `8f38b89c` | [#3006](https://github.com/refusedguy/Harbor-Harness/pull/3006) |

PR #3006 добавил три строки для tagged release archive, source build и beta
artifact; канал `nightly` помечен как verification-only со ссылками на
`DAILY_BUILDS.md` и `UPGRADE.md`. Merge commit `8f38b89c` подтверждён как
предок `origin/dev` через `git merge-base --is-ancestor`. Комментарий в issue
от `2026-11-19T01:06:10Z` фиксировал тот же slice `8cdb0082`, когда PR ещё был
открыт; слияние — новый факт относительно ADR-070.

Остальные две позиции из ADR-070 не закрыты. `docs/MIGRATION.md` (идея 34)
находится в открытом PR [#3007](https://github.com/refusedguy/Harbor-Harness/pull/3007)
и в `dev` не влит. `docs/CLI_REFERENCE.md` не имеет ни PR, ни владельца.
Нового решения владельца issue не содержит: комментарии фиксируют только
хеши slice.

## Decision

Код не писать. Конкретно:

- Идея 3 не перевыводится. Она влита PR #3006, файл присутствует в дереве,
  merge commit подтверждён как предок базы. Повторная правка создала бы второй
  источник истины там, где отчёт требует одного владельца.
- Идея 34 не вливается «заодно». У неё открытый PR #3007 со своим владельцем
  и своим критерием приёмки; включение её сюда создало бы два PR на один
  файл. `docs/CLI_REFERENCE.md` не открывается без владельца и потребителя.
- Оставшиеся идеи отчёта не объединяются в meta-PR. У каждой свой контракт
  и своя цель; новый slice открывается отдельной issue и PR, как это сделано
  для #2986, #2989, #2991 и #3006.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть, ослаблять гейты запрещено.
- Новый guard не вводится: на дереве, где часть идей влита, а часть ждёт
  владельца, проверка «все идеи закрыты» была бы вакуумной.
- Issue #2687 не закрывается. Это self-contained отчёт, а не трекер одной
  задачи. Комментарий содержит только хеш коммита этого ADR, без шаблонного
  текста о прогрессе.

## Consequences

- Меняются два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), их прогоняет CI.
- Issue остаётся OPEN. Пересмотр возможен по новому факту: владелец называет
  конкретную идею, потребителя и измеримую цель. Тогда открывается отдельный
  slice, а не переоткрытие всего отчёта.

## References

- Issue [#2687](https://github.com/refusedguy/Harbor-Harness/issues/2687) —
  полный текст отчёта `dx-docs-gaps`.
- [ADR-043](ADR-043-dx-docs-gaps-report-verdict.md) — исходный вердикт по
  этому отчёту.
- [ADR-070](ADR-070-dx-docs-gaps-post-verdict.md) — предыдущая post-verdict
  запись по этому отчёту; в ней идея 3 ещё значилась отсутствующей.
- Merged PR [#3006](https://github.com/refusedguy/Harbor-Harness/pull/3006)
  (`8cdb0082`, merge `8f38b89c`, идея 3).
- Open PR [#3007](https://github.com/refusedguy/Harbor-Harness/pull/3007)
  (`docs/MIGRATION.md`, идея 34) — не влит на момент этого ADR.
- [ADR-077](ADR-077-kilocode-part2-post-verdict.md) — та же форма post-verdict
  записи по влитым slice.
- [DECISIONS.md](DECISIONS.md).
