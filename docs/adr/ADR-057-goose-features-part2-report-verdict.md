# ADR-057: отчёт goose-features part 2/2 (#2678) — нового slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2678](https://github.com/refusedguy/Harbor-Harness/issues/2678) прочитан
> полностью: concrete item 87–117 и безымянный первый блок (item 86) плюс
> итоговая сводка по применимости. Дубли проверены через `gh pr list --search
> 2678`, комментарии issue, `git log --all --grep=2678` и
> `docs/adr/DECISIONS.md` на базе `origin/dev` (`d8e4856c`). Номер 057 выбран,
> потому что 055 занят ADR-055 для #2683, а 056 влит PR #2969 для #2705.
> Код, тесты, гейты, пороги, golden и AOT-профиль этим PR не
> меняются.

## Status

**Decided:** по отчёту #2678 не открывается новый общий slice. Шесть
применимых item уже влиты отдельными PR. Остальные либо имеют готовый аналог
в Harbor, либо требуют собственного владельца и публичного контракта, либо
прямо помечены отчётом как неприменимые к терминальному .NET-агенту. Issue
остаётся OPEN как источник сравнения; комментарий фиксирует только хеш коммита
этого ADR.

## Context

Issue #2678 — self-contained отчёт `goose-features` part 2/2. Инвентаризация
`origin/dev` (`d8e4856c`) и истории PR показала шесть влитых item. Все merge
commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) | Slice |
|---|---|---|---|
| 92, эмуляция tool calls | разбор `$ cmd` и fenced-блоков для моделей без native tools | [#2894](https://github.com/refusedguy/Harbor-Harness/pull/2894), `d08f09bd` | `2f9fd06c` |
| 93, компактный каталог инструментов | `name + description` вместо полной схемы для маленьких моделей | [#2928](https://github.com/refusedguy/Harbor-Harness/pull/2928), `e6793a36` | `fbd7e1bd` |
| 99, потоковый Markdown-буфер | удержание незакрытых fences/bold/link в streaming | [#2864](https://github.com/refusedguy/Harbor-Harness/pull/2864), `6355f2e1` | `2f328a83` |
| 100, санитизация терминала | вырезка ESC-последовательностей и bidi перед рендером | [#2796](https://github.com/refusedguy/Harbor-Harness/pull/2796), `57f55b03` | `729885a3` |
| 113, traceparent в MCP | проброс trace context в `tools/call` | [#2939](https://github.com/refusedguy/Harbor-Harness/pull/2939), `515c961e` | `a3de9d93` |
| 114, приватный каталог логов | каталог 0700 и проверка symlink до записи | [#2921](https://github.com/refusedguy/Harbor-Harness/pull/2921), `bbd559c2` | `68b29089`, `d142876e` |

Комментарии в issue фиксируют slice-коммиты `2f328a83` (item 99),
`a22dcb75` (follow-up), `d142876e` (item 114), `fbd7e1bd` (item 93) и
`a3de9d93` (item 113).

Оставшиеся item не образуют одного владельца:

- Готовый аналог в Harbor, новый slice не нужен: 88 (usage/cost —
  `TokenTracker` и `SessionStatsEvent`), 95 (declarative providers —
  `providers/*.json` и `ProviderPresets`), 97 (единое ядро — CLI, Avalonia и
  IPC уже разделены по тому же принципу), 105 (thinking indicator — spinner
  в CellForge уже есть, меняются только тексты).
- Требуют своего владельца и измеримой цели: 86 (`ExtensionState` в сессии),
  87 (per-session system prompt extras), 89 (`ProviderCapabilities` с флагами
  inventory), 96 (OAuth device flow), 101 (OSC 7501), 102 (bell по events),
  104 (внешний редактор промпта), 106 (cron-планировщик; часть пересекается с
  открытым PR #2957 по #2674 G11), 107 (`harbor doctor`), 108
  (`.agents/checks/*.md`), 109 (шаблоны системного промпта), 110
  (иерархические hints), 115 (read-only config migrations), 116 (реестр
  feature flags), 117 (explicit telemetry opt-in и installation id).
- Отчёт помечает как неприменимые или слишком крупные: 90 (ACP CLI-агенты),
  91 (in-process inference на нативных биндингах), 94 (HF search/download —
  только обёртка над Ollama), 98 (WebSocket ACP origin policy), 103 (Windows
  paste), 111 (messaging gateway), 112 (roaming).

Итоговая сводка issue называет приоритетные кластеры 1–6; пять из них
(пункты 1, 19–26, 28, 32, 34–38, 59–62, 68–77) относятся к part 1/2 (#2677) и
этим решением не перевыводятся. Кластер 6 (пункты 99–105) частично закрыт
здесь: 99 и 100 влиты, 101–105 требуют владельца.

## Decision

Код не писать. Конкретно:

- Шесть влитых item не перевыводятся. Повторная правка разбора tool calls,
  компактного каталога, streaming-буфера, санитизации, traceparent и
  приватного каталога логов создала бы второй источник истины там, где отчёт
  требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный
  контракт и критерий приёмки; часть прямо помечена как не переносимая.
  Новый slice открывается отдельной issue, когда владелец назовёт потребителя
  и измеримую цель.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть; документационные гейты `docs.yml` остаются на
  стороне CI.
- Issue #2678 не закрывается: это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2678.

## References

- Issue [#2678](https://github.com/refusedguy/Harbor-Harness/issues/2678) —
  полный текст отчёта `goose-features` part 2/2.
- Merged PR: [#2796](https://github.com/refusedguy/Harbor-Harness/pull/2796),
  [#2864](https://github.com/refusedguy/Harbor-Harness/pull/2864),
  [#2894](https://github.com/refusedguy/Harbor-Harness/pull/2894),
  [#2921](https://github.com/refusedguy/Harbor-Harness/pull/2921),
  [#2928](https://github.com/refusedguy/Harbor-Harness/pull/2928),
  [#2939](https://github.com/refusedguy/Harbor-Harness/pull/2939).
- Part 1/2 того же отчёта: issue
  [#2677](https://github.com/refusedguy/Harbor-Harness/issues/2677) — этим
  решением не перевыводится.
- [DECISIONS.md](DECISIONS.md).
