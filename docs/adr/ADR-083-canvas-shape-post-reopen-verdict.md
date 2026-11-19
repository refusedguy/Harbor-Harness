# ADR-083: отчёт canvas-shape-decision (#2689) после переоткрытия — ADR-027 в силе

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2689](https://github.com/refusedguy/Harbor-Harness/issues/2689) прочитан
> полностью: тело отчёта и все три комментария. Проверены
> `gh pr list --search 2689 --state all`, `git log` и
> `git merge-base --is-ancestor` на базе `origin/dev` (`6691dd96`). Номер 083 —
> следующий свободный после 082, закреплённого PR #3014 (`c5e9182c`). Код,
> тесты, гейты, пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** вердикт [ADR-027](ADR-027-canvas-shape-context-stays-deferred.md)
сохраняется. Переоткрытие #2689 не добавило нового факта: выбранный пункт
отчёта был owner-decision, а не кодовый slice. Issue остаётся OPEN как
self-contained отчёт; комментарий фиксирует только хеш коммита этого ADR.

## Context

ADR-027 на базе `91a66022` зафиксировал «оставить DEFERRED» для
[#1168](https://github.com/refusedguy/Harbor-Harness/issues/1168). Единственное
изменение по #2689 — PR [#2782](https://github.com/refusedguy/Harbor-Harness/pull/2782),
merge commit `2f55d7ea`. Он влит 2026-11-18 и подтверждён как предок базы.
Владелец затем закрыл issue вручную и переоткрыл его с формулировкой «в dev влит
только ОДИН slice». Эта предпосылка неверна: PR #2782 был самим ADR-вердиктом,
а не реализацией пункта отчёта. Открытых PR по #2689 после переоткрытия нет.

Содержательные условия ADR-027 не изменились:

- [#1168](https://github.com/refusedguy/Harbor-Harness/issues/1168) остаётся
  track-only, p3/low, с прямой формулировкой «Track only, do not implement».
- [#1155](https://github.com/refusedguy/Harbor-Harness/issues/1155) по-прежнему
  исключает Canvas charts, [#2382](https://github.com/refusedguy/Harbor-Harness/issues/2382)
  закрыт и исключает 2D-геометрию как L-сложность.
- Непокрытый кластер из §4 отчёта (`SubcellGrid`, `CanvasPainter`, `Line` и
  `Points`) не получил потребителя. Спроса на braille, quadrant и world-
  координаты в issue нет; новый факт в комментариях не назван.

## Decision

Код не писать. Конкретно:

- ADR-027 не перевыводится. Повторный вердикт в том же виде создал бы второй
  источник истины; этот ADR лишь фиксирует, что после переоткрытия решение не
  изменилось.
- Срез из §4 отчёта не открывается «заодно». `SubcellGrid`, `CanvasPainter`,
  `Line` и `Points` остаются гипотетическим L-слоем без потребителя.
- [#1168](https://github.com/refusedguy/Harbor-Harness/issues/1168) не
  переоткрывается и не снимается с DEFERRED без решения владельца.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Новый guard
  не вводится: на дереве без потребителя subcell-растеризации проверка была бы
  вакуумной.
- Issue #2689 не закрывается: это self-contained отчёт, а не трекер одной
  задачи. Комментарий содержит только хеш коммита этого ADR.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), их прогоняет CI.
- Пересмотр возможен по новому факту: владелец снимает DEFERRED с
  [#1168](https://github.com/refusedguy/Harbor-Harness/issues/1168), называет
  потребителя subcell-растеризации и измеримую цель. Тогда открывается
  отдельный issue и slice, а не новое переоткрытие #2689.

## References

- Issue [#2689](https://github.com/refusedguy/Harbor-Harness/issues/2689) —
  полный текст отчёта `canvas-shape-decision` и три комментария.
- [ADR-027](ADR-027-canvas-shape-context-stays-deferred.md) — вердикт, который
  сохраняет силу.
- Merged PR [#2782](https://github.com/refusedguy/Harbor-Harness/pull/2782)
  (`2f55d7ea`) — единственное изменение по #2689, сам ADR-вердикт.
- [ADR-080](ADR-080-zed-gpui-post-reopen-verdict.md) — та же форма
  post-verdict записи после переоткрытия отчёта.
- [DECISIONS.md](DECISIONS.md).
