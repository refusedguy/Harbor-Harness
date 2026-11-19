# ADR-087: отчёт kilocode-features part 2/2 (#2680) после вердикта — влиты восемь slice

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2680](https://github.com/refusedguy/Harbor-Harness/issues/2680) прочитан
> полностью, вместе с комментариями. Проверены `gh pr list --search 2680 --state
> all`, `git log` и `git merge-base --is-ancestor` на базе `origin/dev`
> (`d05e14f2`). Номер 087 — следующий свободный после 086, закреплённого PR
> [#3032](https://github.com/refusedguy/Harbor-Harness/pull/3032) (slice
> `55d11d5a`). Код, тесты, гейты, пороги, golden и AOT-профиль этим PR не
> меняются.

## Status

**Decided:** вердикты [ADR-049](ADR-049-kilocode-features-part2-report-verdict.md),
[ADR-077](ADR-077-kilocode-part2-post-verdict.md) и
[ADR-085](ADR-085-kilocode-part2-six-slices-post-verdict.md) сохраняются: одного
общего slice по #2680 нет. Восемь slice, влитых после ADR-085, не перевыводятся.
Issue остаётся OPEN как источник сравнения; комментарий фиксирует только хеш
коммита этого ADR.

## Context

ADR-085 на базе `6b68000a` (slice `66fa103d`) учёл шесть item и отдельно назвал
PR [#3027](https://github.com/refusedguy/Harbor-Harness/pull/3027) (F152) и
[#3030](https://github.com/refusedguy/Harbor-Harness/pull/3030) (F108) как
открытые. Новый факт: оба PR влиты, и после ADR-085 в `dev` влито ещё шесть item.
Каждый slice — отдельный PR со своим контрактом и тестом.

| Item | Что требовалось | След в дереве | Slice | PR | Merge commit |
|---|---|---|---|---|---|
| F108, wakeup | разбор относительных и ISO 8601 расписаний пробуждения | `src/Harbor.Tools.Builtin/Tools/WakeupSchedule.cs` | `c2e10b12` | [#3030](https://github.com/refusedguy/Harbor-Harness/pull/3030) | `65e56a8e` |
| F152, skill path | валидация сегментов пути discovery skill-ов | `src/Harbor.Tools.Builtin/Tools/Skill/SkillPathValidator.cs` | `471bf56f` | [#3027](https://github.com/refusedguy/Harbor-Harness/pull/3027) | `dc0b015b` |
| F174, glyph | иконка агента в заголовке терминала | `src/Harbor.Tui.CellForge.Engine/Rendering/AgentTitleGlyph.cs` | `9ae8f7e3` | [#3033](https://github.com/refusedguy/Harbor-Harness/pull/3033) | `23b4d0ff` |
| F167, JSONC | разбор конфига с комментариями и trailing comma | `src/Harbor.Tools.Builtin/JsoncConfigParser.cs` | `735f5b59` | [#3034](https://github.com/refusedguy/Harbor-Harness/pull/3034) | `4bc5f55e` |
| F121, history | история промптов с областью сессии и лимитом | `src/Harbor.Tools.Builtin/Tools/SessionPromptHistory.cs` | `4042c213` | [#3035](https://github.com/refusedguy/Harbor-Harness/pull/3035) | `f7bfe9ec` |
| F178, model search | поиск модели по имени/ID с ограниченной пагинацией | `src/Harbor.Tools.Builtin/Tools/ModelCatalogSearch.cs` | `9fe3b714` | [#3037](https://github.com/refusedguy/Harbor-Harness/pull/3037) | `0cafd5a8` |
| F114, title | чистая политика генерации названия сессии | `src/Harbor.Tools.Builtin/Tools/SessionTitlePolicy.cs` | `0a137dff` | [#3042](https://github.com/refusedguy/Harbor-Harness/pull/3042) | `425d5c06` |
| F109, cron | разбор пятиполевого cron-выражения | `src/Harbor.Tools.Builtin/Tools/CronSchedule.cs` | `721417f5` | [#3038](https://github.com/refusedguy/Harbor-Harness/pull/3038) | `290d0064` |

Проверено `git merge-base --is-ancestor`:
`66fa103d` является предком каждого из восьми merge commit, а каждый merge
commit является предком базы `d05e14f2`. Комментарии issue фиксируют те же хеши
slice. Открытый PR [#3039](https://github.com/refusedguy/Harbor-Harness/pull/3039)
(F149) в таблицу не входит: он не влит и в базе отсутствует. Нового решения
владелец issue не называл: комментарии фиксируют только хеши slice.

## Decision

Код не писать. Конкретно:

- Восемь влитых item не перевыводятся: у каждого свой PR, merge commit и файл в
  дереве, все подтверждены как предки базы. Повторная правка создала бы второй
  источник истины там, где отчёт требует одного владельца.
- F149 не «доливается» заодно: открытый PR #3039 имеет свой slice и свой
  критерий приёмки.
- Остальные item не объединяются в meta-PR. Каждый имеет свой контракт; новый
  slice открывается отдельной issue и PR, как это уже делалось для предыдущих
  item отчёта.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документные гейты `docs.yml` остаются на стороне CI.
- Новый guard не вводится: на дереве, где исполняемые item влиты, а часть ждёт
  владельца, проверка «все item закрыты» была бы вакуумной.
- Issue #2680 не закрывается: это self-contained отчёт на 180 item, а не трекер
  одной задачи.

## Consequences

- Меняются два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск, документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), их прогоняет CI.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный slice, а не
  переоткрытие всего отчёта.

## References

- Issue [#2680](https://github.com/refusedguy/Harbor-Harness/issues/2680),
  полный текст аудита `kilocode-features` part 2/2.
- [ADR-085](ADR-085-kilocode-part2-six-slices-post-verdict.md), предыдущий
  post-verdict по этому отчёту.
- Восемь влитых PR: #3027, #3030, #3033, #3034, #3035, #3037, #3038, #3042.
- [DECISIONS.md](DECISIONS.md).
