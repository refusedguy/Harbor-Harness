# ADR-084: отчёт dx-docs-gaps (#2687) после вердикта — влиты идеи 8, 27, 29 и 34

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2687](https://github.com/refusedguy/Harbor-Harness/issues/2687) прочитан
> полностью, вместе с 16 комментариями. Три предыдущих вердикта по этому
> отчёту ([ADR-043](ADR-043-dx-docs-gaps-report-verdict.md),
> [ADR-070](ADR-070-dx-docs-gaps-post-verdict.md) и
> [ADR-081](ADR-081-dx-docs-gaps-idea3-post-verdict.md)) сохраняются. После
> ADR-081 в `dev` влиты четыре slice, которых тот ADR не знал. Факты проверены
> через `gh pr view`, `gh issue view 2687 --comments` и
> `git merge-base --is-ancestor` на базе `origin/dev` (`654b1777`). Номер 084 —
> следующий свободный после 083, закреплённого PR #3015. Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** вердикты ADR-043, ADR-070 и ADR-081 сохраняются: одного общего
slice по issue #2687 нет. Четыре влитые идеи не перевыводятся. Issue остаётся
OPEN как источник сравнения; комментарий фиксирует только хеш коммита этого
ADR.

## Context

ADR-081 на базе `36fcd2a1` закрыл идею 3 (таблица путей установки) и отдельно
сказал, что идея 34 (`docs/MIGRATION.md`) находится в открытом PR #3007, а
`docs/CLI_REFERENCE.md` не имеет владельца. Свежая проверка `origin/dev`
(`654b1777`) показывает, что PR #3007 влит и что после ADR-081 влиты ещё три
docs-slice по тому же отчёту:

| Идея отчёта | Что требовалось | След в дереве | Slice | Merge commit | PR |
|---|---|---|---|---|---|
| 34: версионированные migration notes | вынести миграцию плагинов в отдельный файл с таблицей version → action | `docs/MIGRATION.md` | `4cf82090` | `47901ee0` | [#3007](https://github.com/refusedguy/Harbor-Harness/pull/3007) |
| 27: индекс рецептов плагинов | таблица «рецепт → время → интерфейсы → результат» в начале plugin-руководства | `docs/PLUGIN_DEVELOPMENT.md` | `6c3dd7e9` | `e6a7e1d2` | [#3018](https://github.com/refusedguy/Harbor-Harness/pull/3018) |
| 29: FAQ по дизайнерским решениям | короткий FAQ на 10 вопросов из `docs/specs/` и ADR с ссылками на источники | `docs/DESIGN_FAQ.md` | `75c39749` | `06f2f0e9` | [#3020](https://github.com/refusedguy/Harbor-Harness/pull/3020) |
| 8: индекс запускаемых примеров | один индекс «файл → команда → ожидаемый вывод» для demo-сцен | `examples/README.md` | `08a8de53` | `8a4aa2a0` | [#3023](https://github.com/refusedguy/Harbor-Harness/pull/3023) |

Все четыре merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`, все четыре файла присутствуют в дереве на базе
`654b1777`. Merge commit `0407c2ea` (PR #3012, ADR-081) является предком
`47901ee0`, то есть идея 34 влита после предыдущей post-verdict записи. Две
другие позиции из ADR-081 по-прежнему открыты: `docs/CLI_REFERENCE.md` не имеет
ни PR, ни владельца, а остальные идеи отчёта (например, `harbor doctor` и
`harbor completions`) требуют собственного контракта и критерия приёмки.
Нового решения владельца issue не содержит: комментарии фиксируют только хеши
slice.

## Decision

Код не писать. Конкретно:

- Четыре влитые идеи не перевыводятся. Повторная правка `docs/MIGRATION.md`,
  `docs/PLUGIN_DEVELOPMENT.md`, `docs/DESIGN_FAQ.md` или `examples/README.md`
  создала бы второй источник истины там, где отчёт требует одного владельца.
- Идея 34 не «доливается» заодно: у неё был свой PR #3007 со своим критерием
  приёмки и он уже влит. То же относится к идеям 27, 29 и 8.
- Оставшиеся идеи не объединяются в meta-PR. `docs/CLI_REFERENCE.md` не
  открывается без владельца и потребителя; каждая другая идея открывается
  отдельной issue и PR, как это сделано для #3006, #3007, #3018, #3020 и #3023.
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
- [ADR-043](ADR-043-dx-docs-gaps-report-verdict.md) — исходный вердикт по этому
  отчёту.
- [ADR-070](ADR-070-dx-docs-gaps-post-verdict.md) — первая post-verdict запись.
- [ADR-081](ADR-081-dx-docs-gaps-idea3-post-verdict.md) — предыдущая
  post-verdict запись; в ней идея 34 ещё значилась открытой.
- Merged PR [#3007](https://github.com/refusedguy/Harbor-Harness/pull/3007),
  [#3018](https://github.com/refusedguy/Harbor-Harness/pull/3018),
  [#3020](https://github.com/refusedguy/Harbor-Harness/pull/3020) и
  [#3023](https://github.com/refusedguy/Harbor-Harness/pull/3023) — четыре
  slice, закрытые этим ADR.
- [ADR-077](ADR-077-kilocode-part2-post-verdict.md) — та же форма post-verdict
  записи по влитым slice.
- [DECISIONS.md](DECISIONS.md).
