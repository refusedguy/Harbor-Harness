# ADR-059: отчёт goose-features part 1/2 (#2677) — нового общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2677](https://github.com/refusedguy/Harbor-Harness/issues/2677) прочитан
> полностью: concrete item 1–86 (item 86 оборван на границе тела issue и уже
> учтён в [ADR-057](ADR-057-goose-features-part2-report-verdict.md)). Дубли
> проверены через `gh pr list --search 2677`, комментарии issue,
> `git log --all --grep=2677`, `git merge-base --is-ancestor` и
> `docs/adr/DECISIONS.md` на базе `origin/dev` (`b84a80d1`). Номер 059 выбран,
> потому что 058 — последний ADR в `DECISIONS.md`. Код, тесты, гейты, пороги,
> golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2677 не открывается новый общий slice. Четыре
применимых item уже влиты отдельными PR. Остальные либо имеют готовый аналог
в Harbor, либо требуют собственного владельца и публичного контракта, либо
прямо помечены отчётом как неприменимые. Issue остаётся OPEN как источник
сравнения; комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2677 — self-contained отчёт `goose-features` part 1/2 с concrete item
1–86 в семи разделах: A (Extensions/MCP), B (Hooks), C (Approval, права,
безопасность), D (Agent loop, subagents), E (Контекст, память, компактация),
F (Recipe system), G (Управление сессиями). Инвентаризация `origin/dev` и
истории PR показала четыре влитых item. Все merge commit подтверждены как
предки `origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) | Slice |
|---|---|---|---|
| 2, provider-safe имена MCP-инструментов с хешем коллизий | санитизация имени, обратный маппинг в адаптере | [#2914](https://github.com/refusedguy/Harbor-Harness/pull/2914), `722684ec` | `2fe38488` |
| 7, видимость инструментов MCP Apps | скрывать app-only инструменты от модели | [#2774](https://github.com/refusedguy/Harbor-Harness/pull/2774), `eb33bc33` | `9763b5a4` |
| 59, структурированное резюме компактации | 10 полей, lenient-парсинг, AOT-контекст | [#2908](https://github.com/refusedguy/Harbor-Harness/pull/2908), `94e17e0e` | `1512cdef` |
| 60, извлечение JSON из ответа модели | кандидаты и brace-balance без «чистки» хвоста | [#2860](https://github.com/refusedguy/Harbor-Harness/pull/2860), `7c4cea70` | `dc9892d6` |

Комментарии в issue фиксируют те же slice-коммиты: `9763b5a4` (item 7),
`dc9892d6` (item 60), `1512cdef` (item 59) и `2fe38488` (item 2).

Оставшиеся item не образуют одного владельца:

- A, Extensions/MCP: п. 1 меняет снимок каталога на границе turn; п. 3–6,
  8–11 и 13–17 вводят новые boundary-типы (`ExtensionScope`, capabilities,
  elicitation, manifest) и видимый UX. П. 12 уже реализован тремя
  transport'ами в Harbor, п. 18 закрыт тестами и гейтами.
- B, Hooks: п. 19–22 и 24–26 частично опираются на `HookEvents`/`HookRunner`,
  но требуют новых событий и структурированного контекста с `tool_call_id`.
  П. 21 (таймаут и `on_failure`), 23 (deny с причиной) и 25 (stop hook с капом
  попыток) — отдельные контракты с измеримой целью.
- C, Права и безопасность: п. 27–32, 34, 35, 37, 38 частично покрыты
  `PermissionRuleset`, `BuiltinToolSafetyProfiles` и `ApprovalCoordinator`.
  П. 29 (LLM-классификатор) и 34 (пользовательский файл правил) требуют
  opt-in по стоимости и доверию; п. 33 ACP-specific и терминальному агенту
  не нужен.
- D, Agent loop: п. 39–55 частично покрыты `AgentLoop`, `Pipeline/*Behavior`,
  `ISubAgentRunner` и `AgentSteeringQueue`. П. 40 (синтез ответов на отменённые
  tool calls) и 47 (необработанные вызовы) дешёвые, но каждый меняет конвейер;
  п. 42, 43–46 и 48–55 требуют своей поверхности и владельца.
- E, Контекст: п. 57, 58, 61, 64–66 имеют частичные аналоги в
  `CompactionService`, `TokenTracker` и `CellForgeTokenBreakdownPanel`. П. 62
  (tool-pair compaction) и 65 (spill больших ответов) ценны, но заводят новые
  публичные контракты; п. 63 и 67 — эксперимент и ACP-специфика. П. 59 и 60
  влиты и здесь не перевыводятся.
- F, Recipe system: п. 68–78 все требуют новой подсистемы (`HarborRecipe`,
  валидатор, runner, шаблонизация, sub-recipes, deeplink, CLI). Отчёт называет
  п. 68 ценным, но это один самостоятельный slice, а не часть общего.
- G, Сессии: п. 79 упирается в принятое решение JSONL-first. П. 80–86
  (автоименование, chat recall, импорт чужих форматов, HTML/Markdown-экспорт,
  сниппет, версионированное состояние расширений) — семь отдельных slice с
  разными потребителями. Часть п. 82 пересекается с импортом Claude/Codex из
  #2674 F13 (PR [#2923](https://github.com/refusedguy/Harbor-Harness/pull/2923)),
  но в этом отчёте прямо не помечена как влитая.

Итоговая сводка issue называет приоритетные кластеры; кластеры 1, 19–26, 28,
32, 34–38, 59–62 и 68–77 относятся к part 1/2. Здесь закрыты только 59 и 60;
остальные требуют владельца и критерия приёмки. Part 2/2 (#2678) зафиксирован
[ADR-057](ADR-057-goose-features-part2-report-verdict.md) и не перевыводится.

## Decision

Код не писать. Конкретно:

- Четыре влитых item не перевыводятся. Повторная правка санитизации имён,
  видимости инструментов, резюме компактации и извлечения JSON создала бы
  второй источник истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный
  контракт и критерий приёмки; часть прямо помечена как не переносимая или
  уже имеют аналог в Harbor. Новый slice открывается отдельным PR, когда
  владелец назовёт потребителя и измеримую цель.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть; документационные гейты `docs.yml` остаются на
  стороне CI.
- Issue #2677 не закрывается: это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2677.

## References

- Issue [#2677](https://github.com/refusedguy/Harbor-Harness/issues/2677) —
  полный текст отчёта `goose-features` part 1/2.
- Merged PR: [#2774](https://github.com/refusedguy/Harbor-Harness/pull/2774),
  [#2860](https://github.com/refusedguy/Harbor-Harness/pull/2860),
  [#2908](https://github.com/refusedguy/Harbor-Harness/pull/2908),
  [#2914](https://github.com/refusedguy/Harbor-Harness/pull/2914).
- Part 2/2 того же отчёта: issue
  [#2678](https://github.com/refusedguy/Harbor-Harness/issues/2678), вердикт
  [ADR-057](ADR-057-goose-features-part2-report-verdict.md).
- [DECISIONS.md](DECISIONS.md).
