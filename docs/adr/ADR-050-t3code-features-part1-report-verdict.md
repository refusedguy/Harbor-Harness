# ADR-050: отчёт t3code-features part 1/2 (#2673) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2673](https://github.com/refusedguy/Harbor-Harness/issues/2673) прочитан
> полностью: 90 concrete item A1-F11 в шести группах. Дубли проверены через
> `gh pr list --search 2673` и `git log --all --grep=2673` на базе `origin/dev`
> (`82b915c2`). Код, тесты, гейты, пороги, golden и AOT-профиль этим PR не
> меняются.

## Status

**Decided:** одного slice по всему отчёту #2673 не открывается. Три item уже
влиты отдельными PR, остальные требуют собственного владельца и критерия
приёмки. Issue остаётся OPEN как источник сравнения; комментарий фиксирует
только хеш коммита этого ADR.

## Context

Issue #2673 — self-contained отчёт `t3code-features` part 1/2. Он покрывает
agent loop и execution graph, инструменты и разрешения, сессии и чекпойнты,
провайдеров, UI/UX и плагины. Каждый item несёт исходный `file:line`, суть
разрыва, аналог в Harbor и минимальный slice. Пункты помечены применимостью
[A]/[B]/[C]/[X], часть прямо названа `[spec]` — target design, а не ship-код.

Проверка базы `82b915c2` и истории PR показывает три влитых item. Все три merge
commit подтверждены как предки `origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | След | PR |
|---|---|---|---|
| A17, DrainableWorker | drainable worker как тестовая замена «queue empty == idle» | `DrainableWorker` в TestKit | [#2780](https://github.com/refusedguy/Harbor-Harness/pull/2780), `822c2312` |
| A3, Execution graph | `ExecutionNodeId`/`ExecutionNode` + проекция `NodesByRun` поверх AgentEvent | проекция графа в tools | [#2863](https://github.com/refusedguy/Harbor-Harness/pull/2863), `66046b72` |
| A9, Activity feed | `ActivityEntry`/`ActivityProjection` с RunId/NodeId, группировка по run | лента активностей | [#2871](https://github.com/refusedguy/Harbor-Harness/pull/2871), slice `1006894c`, merge `9f9e5b02` |

Комментарии в issue фиксируют те же slice-хеши: `822c2312` для A17, `66046b72`
для A3 и `1006894c` для A9.

Оставшиеся 87 item не образуют одного владельца:

- Item с прямым отказом в применимости: E16 voice input, E17 HTML/PDF preview,
  E18 i18n и F6 device tools привязаны к web/Electron/мобильному клиенту,
  которого в терминальном агенте нет.
- Item «только сверить»: B12 tool result truncation, E10 inline editor, E14
  context window usage, E15 status line, F8 hook compatibility, F11 telemetry
  consent уже имеют аналог в Harbor; новый slice создал бы второй источник
  истины.
- Item `[spec]` из target design (A4-A7, C1-C5, C8, C13) описывают шину,
  которую в этом дереве ещё нет. Реализация любого из них меняет контракт
  `AgentEvent`/сессии и требует отдельного критерия приёмки.
- Item, меняющие публичный контракт или новую поверхность: A1 command receipt
  store, A2 effect outbox, A18 managed policy, B1 permission modes, C6 git-ref
  checkpoints, D1 provider capabilities, D3 `ILlmClient` resume cursor,
  F1 orchestrator MCP toolkit. У каждого свой риск для AOT и свой владелец.

## Decision

Код не писать. Конкретно:

- Три влитых item не перевыводятся. Повторная правка DrainableWorker, проекции
  execution graph и ленты активностей создала бы второй источник истины там,
  где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный контракт
  и свой критерий приёмки. Новый slice открывается отдельной issue и PR, как это
  уже сделано для A17, A3 и A9.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть.
- Issue #2673 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются до PR.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2673.
