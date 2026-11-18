# ADR-044: отчёт каталога UI-примитивов CellForge (#2706) — нового общего slice нет

> **Status (2026-11-19):** dated record, not normative. Отчёт issue
> [#2706](https://github.com/refusedguy/Harbor-Harness/issues/2706) (part 1/1)
> прочитан полностью. Дубли проверены через `gh pr list --search 2706` и
> инвентаризацию `origin/dev` = `f700d122`. Код, тесты, гейты, пороги, golden и
> AOT-профиль этим PR не меняются.

## Status

**Decided:** нового общего slice по #2706 не открывается. Каталог описывает 15
примитивов; три из них требовали тонкого контракта, и все три уже влиты или
имеют открытый PR. Остальные в каталоге помечены как shipped. Issue остаётся
OPEN как источник сравнения; комментарий содержит только хеш коммита этого ADR.

## Context

Issue #2706 — self-contained отчёт `tui-ui-primitives-catalog` (исследование
16 README и knowledge-based деталей по 22 терминальным приложениям). Он сводит
фичи lazygit, k9s, fzf, broot, ranger, bottom, helix, gitui, glow, atuin и
других к 15 доменно-независимым примитивам P01-P15 и прямо отмечает, что только
три из них нуждаются в отдельном контракте: P05 `ChangeBlock`, P13 faceted
record и P04 numeric series.

`docs/CELLFORGE_UI_PRIMITIVES.md` (добавлен PR [#2771](https://github.com/refusedguy/Harbor-Harness/pull/2771))
фиксирует состояние дерева на `b1239b7b`: открытыми были P02, P05 и P13.
Проверка более свежей базы `f700d122` и истории PR даёт такую картину:

| Пункт | Что требовалось | След в дереве | PR |
|---|---|---|---|
| P02 | two-pane explorer с общим фокусом | `TwoPaneState` в `src/Harbor.Tui.CellForge.Engine/Rendering/TwoPaneExplorer.cs` | [#2884](https://github.com/refusedguy/Harbor-Harness/pull/2884), влит |
| P13 | фасеты поверх строки истории | `FacetedHistoryState` в `src/Harbor.Tui.CellForge.Engine/Rendering/FacetedHistory.cs` | [#2903](https://github.com/refusedguy/Harbor-Harness/pull/2903), влит |
| P05 | navigable change blocks и двухсторонний выбор | state primitive в работе | [#2906](https://github.com/refusedguy/Harbor-Harness/pull/2906), OPEN |

P01, P03, P04, P06-P12 и P14-P15 в каталоге помечены как shipped: у каждого
указан существующий widget, view или engine-поддержка с точкой входа в
CellForge. Отчёт не называет для них конкретного потребителя в Harbor и
измеримой цели, поэтому повторная вычитка и перевывод были бы вторым владельцем
там, где отчёт требует одного.

## Decision

Код не писать. Конкретно:

- P02 и P13 не перевыводятся: оба имеют влитый PR и файл в
  `src/Harbor.Tui.CellForge.Engine/Rendering/`.
- P05 не дублируется: открытый PR #2906 уже владеет контрактом `ChangeBlock`.
  Этот ADR не предрешает его исход.
- Остальные 12 примитивов не открываются заново: в отчёте нет ни владельца, ни
  критерия приёмки для нового контракта поверх уже shipped виджета.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть.
- Issue #2706 не закрывается: это каталог, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются до PR.
- Новый guard не вводится: на дереве, где исполняемые контракты влиты или
  имеют владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет реального потребителя и
  измеримую цель для примитива, который ещё не shipped. Тогда открывается
  отдельный issue, а не переоткрытие #2706.
