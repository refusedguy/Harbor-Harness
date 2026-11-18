# ADR-053: отчёт ampcode-features part 1/2 (#2675) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2675](https://github.com/refusedguy/Harbor-Harness/issues/2675) прочитан
> полностью: concrete item 001-127 в семи группах A-H. Дубли проверены через
> `gh pr list --search 2675`, комментарии issue и `git log --all --grep=2675`
> на базе `origin/dev` (`32fe88b9`). Код, тесты, гейты, пороги, golden и
> AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2675 не открывается новый общий slice. Десять
применимых item уже влиты отдельными PR. Оставшиеся либо прямо помечены как не
применимые к терминальному .NET-агенту, либо требуют собственного владельца и
критерия приёмки. Issue остаётся OPEN как источник сравнения; комментарий
фиксирует только хеш коммита этого ADR.

## Context

Issue #2675 — self-contained отчёт `ampcode-features` part 1/2 со 127 concrete
item (001-127): потоки, tool UX, разрешения, контекст, скиллы и плагины, MCP,
CLI/TUI и многоканальность. Инвентаризация `origin/dev` (`32fe88b9`) и истории
PR показывает десять влитых item. Все merge commit подтверждены как предки
`origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) |
|---|---|---|
| 002, one task per session | emitted-подсказка в system prompt | [#2952](https://github.com/refusedguy/Harbor-Harness/pull/2952), `90aa35a0` |
| 010, rich session search | фильтры id/label/project/repo/after/before в `harbor sessions search` | [#2938](https://github.com/refusedguy/Harbor-Harness/pull/2938), `b7763523` |
| 022, `--timings` | разбивка фаз one-shot `ask` | [#2941](https://github.com/refusedguy/Harbor-Harness/pull/2941), `1faa3561` |
| 030, `tools list/show --json` | самоописываемый реестр инструментов | [#2918](https://github.com/refusedguy/Harbor-Harness/pull/2918), `8a58d1ed` |
| 041, glob tool-disable | отключение инструментов с источником builtin/mcp | [#2951](https://github.com/refusedguy/Harbor-Harness/pull/2951), `69e2270a` |
| 049, export sessions | экспорт сессий в Markdown и JSON | [#2778](https://github.com/refusedguy/Harbor-Harness/pull/2778), `acff568d` |
| 070, ancestor guidance | родительские AGENTS.md с fallback-именами | [#2946](https://github.com/refusedguy/Harbor-Harness/pull/2946), `11d4cfc2` |
| 071, ignore guidance | `HARBOR_IGNORE_GUIDANCE_FILES` / `AMP_IGNORE_GUIDANCE_FILES` | [#2861](https://github.com/refusedguy/Harbor-Harness/pull/2861), `05e3c7ad` |
| 103, MCP `_meta` | `anthropic/requiresUserInteraction` → Ask First | [#2944](https://github.com/refusedguy/Harbor-Harness/pull/2944), `ad7b8b81` |
| 121, terminal bell | `HARBOR_FORCE_BEL` / `AMP_FORCE_BEL` fallback | [#2911](https://github.com/refusedguy/Harbor-Harness/pull/2911), `01a76c09` |

Комментарии в issue фиксируют те же хеши. Issue закрывался после первого
влитого slice (#2778) и был переоткрыт владельцем: он остаётся каталогом
разрывов, а не трекером одной задачи.

Оставшиеся item не образуют одного владельца:

- Item с прямым отказом в применимости: 033 painter и 076 image paste требуют
  image-модель или desktop GUI; 065 passkey, 067 аудируемую видимость сессий,
  075 dictation и 127 huddle — облачные или человеческие функции; 100-102
  workload identity, OIDC-token и MCP elicitation требуют удалённого сервера и
  callback-слушателя.
- Item «только сверить»: 001 thread id, 003 steering, 007 restore, 009
  `session_read`, 011 background subagent, 021 execute mode, 031 builtin tools,
  034 tool progress, 061 hook lifecycle, 081 BYOK, 083 lazy skills, 109 palette,
  112 prompt history и 124 cost в статусе уже имеют аналог в Harbor. Новый slice
  без потребителя создал бы второй источник истины.
- Item, меняющие публичный контракт или выполняемую поверхность: 004 очередь с
  бейджем, 006 edit-message, 008 handoff, 016-019 automations и webhooks, 032
  `undo_edit`, 035-037 мутация хуков, 040 MCP-политики, 047 commit trailer, 050
  artifacts, 070-072 слои guidance, 078-080 профили моделей, 084 отложенный MCP
  из скиллов, 092-098 плагинные агенты и скиллы, 104-108 structured MCP results
  и OAuth, 110-117 keymap и CLI UX. У каждого свой контракт, свой риск для AOT и
  свой критерий приёмки; их нельзя проверить одним прогоном.

Отчёт part 1/2 соседствует с part 2/2 (#2676), покрытым
[ADR-048](ADR-048-ampcode-features-part2-report-verdict.md). Этот ADR не
инвентаризует #2676 и не перевыводит его item.

## Decision

Код не писать. Конкретно:

- Десять влитых item не перевыводятся. Повторная правка system prompt, session
  search, `--timings`, tools list, tool-disable, export, guidance, MCP `_meta` и
  bell создала бы второй источник истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный контракт
  и свой критерий приёмки. Новый slice открывается отдельной issue и PR, как это
  уже сделано для десяти влитых item.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне CI.
- Issue #2675 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2675.
