# ADR-030: отчёт GPUI (#2707) — все применимые паттерны уже в дереве, нового slice нет

> **Status (2026-11-18):** dated record, not normative. Решение зафиксировано
> без product-кода: единственный выход issue #2707 — закрыть research-отчёт
> как уже реализованный. Документ не несёт `Status: normative`, поэтому
> `file:line`-фенсы в нём не являются проверяемыми утверждениями; факты ниже
> подтверждены `gh pr list --search` и инвентаризацией `origin/dev` (`fc9aabde`).

## Status

**Decided:** новый code slice по #2707 не открывается. Отчёт закрывается как
исследование, чьи исполняемые пункты уже влиты.

## Context

Issue #2707 — self-contained competitor research по движку Zed GPUI (локальный
клон `6983685`, чтение 2026-11-17). Отчёт называет шесть паттернов,
применимых к CellForge. Проверка по `gh pr list --search` и по файлам на
`origin/dev` (`fc9aabde`) показывает, что каждый из шести уже влит:

| # | Паттерн из отчёта | PR | След в дереве |
|---|---|---|---|
| 1 | трёхфазный конвейер + стабильные identity | #2396 | `ElementId`, `ElementIdStack`, `PrepaintContext`, `PrepaintNode`, `PrepaintRecord` |
| 2 | display list с replay диапазонов | #2384 | `Scene` |
| 3 | адресная инвалидация подграфов | #2389 | `ViewDamageTracker`, `CellDamageContract`, `UiDamageLedger` |
| 4 | стек ключевых контекстов capture/bubble | #2395 | `KeyContext` |
| 5 | кэш готовых cell-паттернов | #2386 | `GlyphCache`, `ScreenBuffer.GlyphCache` |
| 6 | measured layout leaves | #2394 | `MeasuredLeaf` |

Паттерн 1 дополнительно усилен PR #2442 (prefix hashes + typed element state),
PR #2523 (ReadEntities в инвалидацию prepaint), PR #2526 (INode root в
GenericTuiRenderer) и PR #2722 (keyed children для DSL). Список «что честно
неприменимо» из отчёта (texture atlas, subpixel-варианты глифов, pixel
snapping, GPU batching, accessibility tree, Taffy как зависимость) остаётся
отвергнутым без изменений.

Единственное в отчёте без отдельного общего контракта — «per-frame line layout
cache по образцу LineLayoutCache». Он не входит в топ-6 и не добавляется:
per-frame переиспользование уже дают prepaint + ElementId-состояние, а
`TimelineLayoutCache` покрывает ленту. Новый общий слой дублировал бы два
существующих механизма и не имеет в отчёте измеримой цели.

## Decision

Product-код не меняется. #2707 — архивное исследование, все исполняемые пункты
которого уже реализованы; новый slice не открывается, issue закрывается.
Будущие работы ссылаются на перечисленные PR, а не перевыводят отчёт.

## Consequences

- Меняются только два markdown-файла; единственный риск — линтер
  документации, который прогоняется в CI (`docs.yml`).
- Гейты не ослабляются и не добавляются: проверять нечего, а новый guard был
  бы красным или вакуумным на дереве, где все шесть паттернов уже влиты.
- Условие пересмотра: появляется новый механизм GPUI, не покрытый этими
  шестью пунктами и имеющий конкретный терминальный аналог. Тогда — новый
  issue и новый ADR, а не переоткрытие #2707.

## References

- Issue #2707 (полный отчёт).
- Merged PR: #2384, #2386, #2389, #2394, #2395, #2396; follow-up #2442, #2523, #2526, #2722.
- [DECISIONS.md](DECISIONS.md).
