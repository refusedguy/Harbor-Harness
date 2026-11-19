# ADR-080: issue #2707 (zed-gpui-engine) после переоткрытия — ADR-030 в силе, нового slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2707](https://github.com/refusedguy/Harbor-Harness/issues/2707) прочитан
> полностью: тело отчёта и оба комментария владельца. Проверены
> `gh pr list --search 2707 --state all`, `gh pr view` для шести PR,
> `gh issue view 2707 --comments` и `git merge-base --is-ancestor` на базе
> `origin/dev` (`1757a343`). Код, тесты, гейты, пороги, golden и AOT-профиль
> этим PR не меняются. Номер 080 — следующий свободный после 079 в
> [DECISIONS.md](DECISIONS.md); открытые PR его не занимают.

## Status

**Decided:** вердикт [ADR-030](ADR-030-gpui-report-patterns-already-landed.md)
сохраняется. Переоткрытие #2707 опирается на неверную предпосылку: «в dev влит
только один slice». Все шесть применимых паттернов отчёта влиты в `dev` до
переоткрытия. Issue остаётся OPEN как self-contained отчёт; комментарий
фиксирует только хеш коммита этого ADR.

## Context

ADR-030 на базе `fc9aabde` разобрал отчёт по GPUI как движку Zed и нашёл, что
каждый из шести применимых к CellForge паттернов уже влит. Проверка по свежей
базе подтверждает все шесть merge commit как предков `origin/dev`:

| Паттерн из отчёта | PR | Merge commit | След в дереве |
|---|---|---|---|
| Трёхфазный конвейер + стабильные identity | [#2396](https://github.com/refusedguy/Harbor-Harness/pull/2396) | `d52f4eda` | `ElementId.cs`, `PrepaintContext` |
| Display list с replay диапазонов | [#2384](https://github.com/refusedguy/Harbor-Harness/pull/2384) | `aea253f2` | `Scene.cs` |
| Адресная инвалидация подграфов | [#2389](https://github.com/refusedguy/Harbor-Harness/pull/2389) | `e5c17b2a` | `ViewDamageTracker.cs` |
| Стек ключевых контекстов capture/bubble | [#2395](https://github.com/refusedguy/Harbor-Harness/pull/2395) | `a53f8e39` | `KeyContext.cs` |
| Кэш готовых cell-паттернов | [#2386](https://github.com/refusedguy/Harbor-Harness/pull/2386) | `5fad6d0a` | `GlyphCache.cs`, `ScreenBuffer.GlyphCache.cs` |
| Measured layout leaves | [#2394](https://github.com/refusedguy/Harbor-Harness/pull/2394) | `666a5aac` | `MeasuredLeaf.cs` |

Все шесть PR влиты 2026-11-17. ADR-030 влит PR
[#2790](https://github.com/refusedguy/Harbor-Harness/pull/2790) с merge commit
`3dd84f3f` 2026-11-18 в 14:37 UTC. Владелец закрыл issue в 14:49 и
переоткрыл в 15:48 UTC с формулировкой «в dev влит только ОДИН slice (см.
комментарий выше с merge commit)»: комментарий выше фиксирует только ADR-PR и
не учитывает шесть code-PR от предыдущего дня. Открытых PR по #2707 на момент
записи нет; нового факта, который менял бы вердикт ADR-030, в issue не
появилось.

## Decision

Код не писать. Конкретно:

- Не перевыводить шесть влитых паттернов. У каждого есть PR, merge commit и
  файл в дереве; повторная правка создала бы второй источник истины.
- Не объединять остаток отчёта в общий slice. У пунктов из раздела «что честно
  неприменимо» свой мотив отказа, а у нереализованных идей — свой потребитель
  и критерий приёмки, которых владелец не называл.
- ADR-030 оставляет отвергнутыми texture atlas, subpixel-варианты, pixel
  snapping, GPU batching, accessibility tree и Taffy как зависимость; этот
  ADR решение не пересматривает.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Новый guard
  не вводится: на дереве, где шесть паттернов влиты, проверка «все пункты
  отчёта закрыты» была бы вакуумной.
- Issue #2707 не закрывается: это self-contained отчёт, а не трекер одной
  задачи. Комментарий содержит только хеш коммита этого ADR.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), их прогоняет CI.
- Пересмотр возможен по новому факту: владелец называет конкретный механизм
  GPUI, не покрытый шестью влитыми PR, его терминальный аналог и измеримую
  цель. Тогда открывается новый issue и отдельный slice, а не второе
  переоткрытие #2707.

## References

- Issue [#2707](https://github.com/refusedguy/Harbor-Harness/issues/2707) —
  полный текст отчёта и оба комментария владельца.
- [ADR-030](ADR-030-gpui-report-patterns-already-landed.md) — вердикт, который
  сохраняет силу.
- PR [#2790](https://github.com/refusedguy/Harbor-Harness/pull/2790) — ADR-030,
  merge `3dd84f3f`; code-PR #2384, #2386, #2389, #2394, #2395, #2396.
- [DECISIONS.md](DECISIONS.md).
