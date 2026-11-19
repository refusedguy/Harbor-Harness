# ADR-086: отчёт avalonia-product-gaps (#2681) после вердикта, влит G24

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2681](https://github.com/refusedguy/Harbor-Harness/issues/2681) прочитан
> полностью, вместе с комментариями. Проверены `gh pr list --search 2681 --state
> all`, `git log --grep=2681`, `git merge-base --is-ancestor` и
> `docs/adr/DECISIONS.md` на базе `origin/dev` (`3e3b32e8`). Номер 086 следующий
> свободный после 085, закреплённого PR #3031 (`3e3b32e8`). Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** вердикт
[ADR-063](ADR-063-avalonia-product-gaps-report-verdict.md) сохраняется: одного
общего slice по #2681 нет. Единственный item, который тот ADR оставлял в
открытом PR, G24, теперь влит и не перевыводится. Issue остаётся OPEN как
источник сравнения; комментарий фиксирует только хеш коммита этого ADR.

## Context

ADR-063 на базе `204f4f44` (merge `50011c23`) учёл одиннадцать влитых item и
отдельно назвал G24 (переиспользование `SessionRow` в `SessionsFlyoutView`) как
находящийся в открытом PR
[#2980](https://github.com/refusedguy/Harbor-Harness/pull/2980). Новый факт:
PR #2980 влит в `dev` merge commit'ом `74060665` (2026-11-19T00:16:29Z, base
`dev`). Коммит ветки `569f57bc` зафиксирован в комментарии issue в 00:16:44Z.
Проверено через `git merge-base --is-ancestor`: `50011c23` является предком
`74060665`, а `74060665` является предком базы `3e3b32e8`.

| Item | Что требовалось | След в дереве | PR | Merge commit |
|---|---|---|---|---|
| G24, строка сессии | переиспользовать готовый `SessionRow` вместо инлайнового `DataTemplate` в `SessionsFlyoutView` | `apps/Harbor.App.Avalonia/Views/Converters.cs`, `apps/Harbor.App.Avalonia/Views/Shell/SessionsFlyoutView.axaml`, `tools/accessible-names-baseline.txt` | [#2980](https://github.com/refusedguy/Harbor-Harness/pull/2980) | `74060665` |

Других влитых slice и комментариев после ADR-063 нет. Открытых PR по #2681 нет:
три открытых PR в репозитории относятся к #2680 и #2659 и в эту таблицу не
входят.

## Decision

Код не писать. Конкретно:

- G24 не перевыводится. У него свой PR, свой merge commit и три файла в дереве,
  все подтверждены как предки базы. Повторная правка создала бы второй источник
  истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. ADR-063 разделил их по владельцам
  и поверхностям: human-in-the-loop, CI/golden, платформа, конкретная вью.
  Новый slice открывается отдельной issue и PR, как это сделано для G24.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документные гейты `docs.yml` остаются на стороне CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или отправлены
  к владельцу, проверка «все item закрыты» была бы вакуумной.
- Issue #2681 не закрывается: это self-contained отчёт на 100 item, а не трекер
  одной задачи.

## Consequences

- Меняются два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск, документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), их прогоняет CI.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный slice, а не
  переоткрытие всего отчёта.

## References

- Issue [#2681](https://github.com/refusedguy/Harbor-Harness/issues/2681),
  полный текст аудита `avalonia-product-gaps`.
- [ADR-063](ADR-063-avalonia-product-gaps-report-verdict.md), исходный вердикт,
  в котором G24 ещё был в открытом PR.
- Merged PR
  [#2980](https://github.com/refusedguy/Harbor-Harness/pull/2980).
- [ADR-085](ADR-085-kilocode-part2-six-slices-post-verdict.md), та же форма
  post-verdict записи по влитому slice.
- [DECISIONS.md](DECISIONS.md).
