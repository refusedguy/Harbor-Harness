# ADR-070: отчёт dx-docs-gaps (#2687) после вердикта — влиты item 1, 4 и 14

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2687](https://github.com/refusedguy/Harbor-Harness/issues/2687) прочитан
> полностью. [ADR-043](ADR-043-dx-docs-gaps-report-verdict.md) зафиксировал
> вердикт на базе `8561cd87`. После него влиты три документальных slice,
> которые тот ADR не учитывал. Код, тесты, гейты, пороги, golden и
> AOT-профиль этим PR не меняются. Номер 070 выбран потому, что 067 и 068
> заняты другими отчётами, а 069 ожидается от открытого PR #2996.

## Status

**Decided:** вердикт ADR-043 сохраняется: одного общего slice по #2687 нет.
Три новых slice не перевыводятся. Issue остаётся OPEN как источник сравнения;
комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2687 — self-contained отчёт `dx-docs-gaps` с 60 идеями по установке,
документации, CLI и контрибутингу. ADR-043 проверил базу `8561cd87` и нашёл
пять влитых идей (18, 19, 22, 30 и 57). Проверка `origin/dev` (`6efb6df6`) и
`gh pr list --search 2687`, повторенная на `56608667`, показывает три slice,
влитых уже после вердикта:

| Идея отчёта | След | Merge commit |
|---|---|---|
| 1: маршрут онбординга с mental model и «where to go next» | два раздела в `docs/GETTING_STARTED.md` | PR [#2989](https://github.com/refusedguy/Harbor-Harness/pull/2989), `ff7f369a` |
| 4: канал daily и pre-release сборок | `docs/DAILY_BUILDS.md` и запись в `docs/toc.yml` | PR [#2986](https://github.com/refusedguy/Harbor-Harness/pull/2986), `d667014f` |
| 14: upgrade guide для каждого канала установки | `docs/UPGRADE.md` | PR [#2991](https://github.com/refusedguy/Harbor-Harness/pull/2991), `6e39897b` |

Все merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`. База `279ac626`, с которой начинался этот
slice, первые два из них уже содержала.

В ADR-043 есть и два устаревших наблюдения. Он называл `harbor completions` и
`harbor doctor` открытыми идеями, хотя к моменту вердикта они уже были в
дереве: completion влит PR #2763 (`a45bc7d6`, issue #2669), doctor — коммитом
`a3ba0fbe` (issue #2668). Это ошибка инвентаризации в том ADR, а не новая
работа: повторная правка создала бы второй источник истины.

На `56608667` по-прежнему отсутствуют `docs/CLI_REFERENCE.md`,
`docs/MIGRATION.md` и таблица путей установки (идея 3) в
`docs/GETTING_STARTED.md`. У каждой из этих идей нет владельца и измеримой
цели, поэтому они остаются вне этого решения.

## Decision

Код не писать. Конкретно:

- Items 1, 4 и 14 не перевыводятся: все влиты и проверены как предки текущей
  базы.
- Три устаревших наблюдения ADR-043 не исправляются правкой того ADR: он
  dated record, а не нормативный документ. Исправление зафиксировано здесь.
- Оставшиеся идеи не объединяются в meta-PR. Каждая требует отдельного
  владельца, потребителя и критерия приёмки, как это уже сделано для #2986 и
  #2989.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
PR не может их затронуть.
- Новый guard не вводится: на дереве, где часть идей влита, а часть ждёт
владельца, проверка «все идеи закрыты» была бы вакуумной.

## Consequences

- Меняются два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`).
- Issue #2687 остаётся OPEN. Комментарий содержит только хеш коммита этого
  ADR, без шаблонного текста о прогрессе.
- Пересмотр возможен по новому факту: владелец называет конкретную идею,
  потребителя и измеримую цель. Тогда открывается отдельный slice, а не
  переоткрытие всего отчёта.

## References

- Issue [#2687](https://github.com/refusedguy/Harbor-Harness/issues/2687) —
  полный текст отчёта `dx-docs-gaps`.
- [ADR-043](ADR-043-dx-docs-gaps-report-verdict.md) — исходный вердикт по
  этому отчёту.
- [ADR-065](ADR-065-tui-ui-primitives-catalog-post-verdict.md) — та же форма
  post-verdict записи.
- [DECISIONS.md](DECISIONS.md).
