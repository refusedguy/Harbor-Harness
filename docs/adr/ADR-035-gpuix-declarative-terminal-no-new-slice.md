# ADR-035: отчёт GPUIX (#2703), декларативный срез уже в дереве

> **Status (2026-11-18):** dated record, not normative. Источник: issue
> [#2703](https://github.com/refusedguy/Harbor-Harness/issues/2703), part 1/1,
> competitor research по декларативному терминальному UI GPUIX. Отчёт прочитан
> полностью. Дубли проверены через `gh pr list --search 2703`, отдельного PR
> под этот отчёт нет. Факты о Harbor сняты с `origin/dev` = `f1dbd3a1`.
> Код, тесты, гейты и пороги этим PR не меняются.

## Status

**Decided:** новый code slice по #2703 не открывается. Все шесть заимствований
из итогового списка отчёта уже влиты в `dev`. Terminal-side механика GPUIX
принята не будет, потому что заменяет cell-diff Harbor на полноэкранный
растровый кадр. Issue остаётся OPEN как отчёт.

## Context

Issue #2703 исследовал GPUIX по локальному клону на коммите `0ce4f41`. Отчёт
называет шесть идей, которые стоит заимствовать для Harbor DSL и CellForge, и
отдельно перечисляет то, что переносить нельзя. Проверка по дереву и по PR
показывает полное покрытие шести пунктов:

| # | Идея отчёта | Уже в Harbor на `dev` | PR |
|---|---|---|---|
| 1 | Mutation batch как IR между sourcegen и `BuildRoot`, трёхфазный atomic apply | `SpecArenaMutationLowering`, `MutationQueue`, `MutationBatch`, `MutationBatchCodec` (`Utf8JsonWriter`/`Utf8JsonReader`, без reflection), `MutationBatchApplier`: validate, intern styles, apply | [#2530](https://github.com/refusedguy/Harbor-Harness/pull/2530), issue #2439 |
| 2 | `dirty`/revision и подавление no-op батчей | `MutationBatchApplier.TakeDirty()`, `ApplyOutcome.Changed`, `Revision`; операции дерева возвращают `changed`, неизменённый батч не инвалидирует кадр | [#2530](https://github.com/refusedguy/Harbor-Harness/pull/2530) |
| 3 | Event id-реестр `(id, type)` и `hasListener` | `MutationOp.SetEventListener`, `RetainedElementTree.HasListener`/`SetListener`; обработчик живёт у владельца дерева, в IR уходит только булево | [#2530](https://github.com/refusedguy/Harbor-Harness/pull/2530) |
| 4 | Automation-протокол: `data:`-строки, id-корреляция, таблица методов, `getTree` с `testId`, clock | `AutomationSession` (`ProtocolVersion = "1.0"`, методы `getTree`/`testId`/`clock`), `HeadlessTree`, `AutomationTransport`, `AutomationClock`; дисциплину `data:` держит `SsePump.DecodeDataLine` | [#2761](https://github.com/refusedguy/Harbor-Harness/pull/2761), issue #2438 |
| 5 | Структурное сравнение полезной нагрузки до enqueue | `MutationCommand.IsEquivalentTo` и `MutationStyle.StructurallyEquals` в `MutationQueue.Enqueue`: повторный `setStyle` не доходит до апплайера и кодека | [#2530](https://github.com/refusedguy/Harbor-Harness/pull/2530) |
| 6 | Разделение времени жизни хоста и дерева для dev reload, явный список сбрасываемого | `BuildRootOverrideCoordinator` с `LiveHostResources`, `OverrideResetEffects` и `OverrideSwapReceipt`; `DevReloadInterpreter` различает patch по сигнатуре шаблона и rebuild; граница `HarborWithDslDev` / `HARBOR_WITH_DSL_DEV` держит интерпретатор вне AOT | [#2789](https://github.com/refusedguy/Harbor-Harness/pull/2789), [#2727](https://github.com/refusedguy/Harbor-Harness/pull/2727) |

Все шесть строк таблицы дают полное покрытие итогового списка отчёта.
Дополнительно отчёт предлагает probe-дисциплину GPUIX: один запрос на старте,
записанный ответ, явная деградация и ограничение in-flight. Она уже реализована
в `CapabilityProber` с kitty keyboard probe, legacy fallback и guardrail для
tmux/screen. Отдельного slice эта идея в отчёте не образует.

## Decision

Product-код не меняется. Повторная реализация любого из шести пунктов создала бы
второго владельца там, где отчёт требует одного: второй mutation applier, второй
event registry, второй automation-протокол. Каждый из шести уже имеет след в
дереве и влитый PR, который ссылается на соответствующий пункт.

`keystrokeId` из раздела про event registry не выделяется в отдельный slice.
Это деталь внутреннего механизма GPUIX; в отчёте нет ни критерия приёмки, ни
измеримой цели, а `KeyEvent` в CellForge не несёт идентификатора физической
клавиши. Harbor уже маршрутизирует ввод через `KittyKeyDecoder`, `KeyContext`,
`ChatKeyMap` и `LeaderKeyRouter`. Добавить параллельный id означало бы завести
второй конвейер событий рядом с рабочим. Границы такого механизма остаются за
владельцем и требуют нового issue, а не переоткрытия #2703.

Terminal-side часть отчёта не принимается. Whole-surface kitty shm (`t=s`),
inline RGBA (`t=d,o=z`), frame edits по плиткам (`a=f,r=1`), fences и
ack/backpressure (`a=q`) нужны только полноэкранному растровому презентеру.
Harbor рисует cell-diff через `DiffEngine` и `DirtyRect`, вставляет отдельные
изображения через `Graphics.KittyPngInline` и остаётся рабочим в любом
терминале. Сам отчёт прямо рекомендует не заменять cell-diff картинкой.
Металл, Vulkan, wgpu, Taffy, 82 МБ bundle, `bun --hot`, Node-API addon,
wasm-путь и kitty/Ghostty-only таргет не переносятся по разнице runtime.

## Consequences

- Меняются только два markdown-файла: этот ADR и [DECISIONS.md](DECISIONS.md).
  Единственный риск это документационные гейты `docs.yml`: `check-md-links` и
  `md-lint` прогоняются локально до PR, остальные проверки оставляем CI.
- Гейты не ослабляются и не добавляются. Guard, запрещающий GPU-паттерны в
  `src/`, был бы вакуумным: на дереве нет ни одного нарушения, которое он
  поймал бы.
- Issue #2703 остаётся OPEN: это отчёт, а не трекер одной задачи.
- Пересмотр возможен по новому факту: владелец называет измеримую цель и
  критерии приёмки для `keystrokeId` или для полноэкранного image path. Тогда
  открывается новый issue и новый ADR, а не переоткрытие #2703.

## Alternatives

Реализовать пункт 1 или 4 заново. Отклонено: оба уже влиты, дублирующий
апплайер или протокол создаст второго владельца там, где отчёт требует одного.

Заменить cell-diff на полноэкранный RGBA-кадр с shm и frame edits. Отклонено:
Harbor теряет terminal-agnostic fallback и дешёвую память, а отчёт сам не
рекомендует эту замену.

## References

- Issue [#2703](https://github.com/refusedguy/Harbor-Harness/issues/2703), полный текст отчёта.
- Merged PR: [#2530](https://github.com/refusedguy/Harbor-Harness/pull/2530),
  [#2727](https://github.com/refusedguy/Harbor-Harness/pull/2727),
  [#2761](https://github.com/refusedguy/Harbor-Harness/pull/2761),
  [#2789](https://github.com/refusedguy/Harbor-Harness/pull/2789).
- [DECISIONS.md](DECISIONS.md).
