# ADR-059: отчёт goose-features part 1/2 (#2677) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2677](https://github.com/refusedguy/Harbor-Harness/issues/2677) прочитан
> полностью: concrete item 1–86 в семи группах A–G (в теле issue пункт 86
> обрывается на строке источника). Дубли проверены через
> `gh pr list --search 2677`, комментарии issue, `git log --all --grep=2677`,
> `git merge-base --is-ancestor` и `docs/adr/DECISIONS.md` на базе
> `origin/dev` (`b84a80d1`). Код, тесты, гейты, пороги, golden и AOT-профиль
> этим PR не меняются.

## Status

**Decided:** по отчёту #2677 не открывается новый общий slice. Четыре
применимых item уже влиты отдельными PR. Оставшиеся либо требуют собственного
владельца, поверхности и критерия приёмки, либо прямо не применимы к
терминальному .NET-агенту. Issue остаётся OPEN как источник сравнения;
комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2677 — self-contained отчёт `goose-features` part 1/2. Он сравнивает
Harbor с Goose (Block / aaif-goose) по расширениям, хукам, разрешениям, agent
loop, контексту, рецептам и сессиям. Всего заявлено 117 фич; в этой части
опубликованы item 1–86, item 86 обрывается на строке источника. Большинство
«аналогов» в отчёте помечены как частичные, то есть отчёт описывает разрывы, а
не пустые подсистемы Harbor.

Инвентаризация `origin/dev` и истории PR показала четыре влитых item. Все merge
commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) |
|---|---|---|
| 002, provider-safe имена MCP-инструментов | санитизация имени по `^[a-zA-Z0-9_-]{1,64}$` и хеш коллизий | [#2914](https://github.com/refusedguy/Harbor-Harness/pull/2914), `722684ec` |
| 007, видимость инструментов MCP Apps | скрытие app-only инструментов от модели | [#2774](https://github.com/refusedguy/Harbor-Harness/pull/2774), `eb33bc33` |
| 059, структурированное резюме компактации | `CompactionSummary` с 10 полями и lenient-парсингом | [#2908](https://github.com/refusedguy/Harbor-Harness/pull/2908), `94e17e0e` |
| 060, извлечение JSON из ответа модели | `ModelJsonExtractor` с подсчётом скобок без «починки» обрезанного JSON | [#2860](https://github.com/refusedguy/Harbor-Harness/pull/2860), `7c4cea70` |

Комментарии в issue фиксируют те же slice-коммиты. Поиск по `2677` в PR не
дал других влитых item этой части: PR
[#2796](https://github.com/refusedguy/Harbor-Harness/pull/2796) относится к
issue [#2678](https://github.com/refusedguy/Harbor-Harness/issues/2678) item
100 и этим решением не перевыводится.

Оставшиеся item не образуют одного владельца:

- Прямо не применимы к терминальному агенту или конфликтуют с ограничениями
  Harbor: 033 и 067 требуют ACP-транспорта, которого нет; 079 предлагает SQLite
  вместо сознательного JSONL-first; 073 предполагает Desktop; 064 BPE-таблицы
  раздувают AOT-бинарник; 063 — observation-only ML-эксперимент; 029 добавляет
  дополнительный вызов модели на каждый tools batch.
- Имеют готовый аналог и требуют только сверки: 012 (два MCP-транспорта), 018
  (валидация bundled extensions), 031–032 (ответы и координатор
  подтверждений), 057 (трёхслойная компактация), 066 (разбивка токенов уже
  есть в CellForge). Новый slice создал бы второй источник истины.
- Требуют собственного контракта и владельца: A — лизы и capabilities MCP
  (1, 3–6, 8–11), in-process memory, OSV-проверка пакетов и манифесты плагинов
  (13–17); B — 12 событий хуков, regex-matcher, structured decision и stop-hook
  (19–28); C — режимы одобрения, LLM-классификатор, инспекторы и egress
  (27–38); D — конвейер операций, отделка незакрытых tool calls, steer, bang,
  recovery, сабагенты и MoIM (39–56); E — потолок токенов компактации,
  proactive compaction, tool-pair summarization и large-response spill
  (58, 61–65); F — система рецептов целиком (68–78); G — авто-именование,
  chat recall, импорт чужих форматов, HTML/Markdown-экспорт и версионированное
  состояние расширений (80–86).

Part 2/2 того же отчёта (#2678) зафиксирован
[ADR-057](ADR-057-goose-features-part2-report-verdict.md) и не перевыводится.

## Decision

Код не писать. Конкретно:

- Четыре влитых item не перевыводятся. Повторная правка MCP-имен, видимости
  инструментов, резюме компактации и извлечения JSON создала бы второй источник
  истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. Точечные порты открываются
  отдельными PR по конкретному item; не применимые не портируются; новые
  публичные контракты требуют владельца и измеримой цели.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне
  CI.
- Issue #2677 не закрывается. Это каталог разрывов, а не трекер одной задачи.

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
- Merged PR: [#2914](https://github.com/refusedguy/Harbor-Harness/pull/2914),
  [#2774](https://github.com/refusedguy/Harbor-Harness/pull/2774),
  [#2908](https://github.com/refusedguy/Harbor-Harness/pull/2908),
  [#2860](https://github.com/refusedguy/Harbor-Harness/pull/2860).
- Part 2/2 того же отчёта: issue
  [#2678](https://github.com/refusedguy/Harbor-Harness/issues/2678), вердикт
  [ADR-057](ADR-057-goose-features-part2-report-verdict.md).
- [DECISIONS.md](DECISIONS.md).
