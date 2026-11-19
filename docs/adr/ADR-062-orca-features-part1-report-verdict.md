# ADR-062: отчёт orca-features part 1/2 (#2671) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2671](https://github.com/refusedguy/Harbor-Harness/issues/2671) прочитан
> полностью: разделы 1–6 (A01–A20, B01–B22, C01–C15, D01–D13, E01–E27,
> F01–F02), тело обрывается на F02. Дубли проверены через `gh pr list --search
> 2671`, комментарии issue, `git log --all --grep=2671`, `git merge-base
> --is-ancestor` и `docs/adr/DECISIONS.md` на базе `origin/dev` (`01e2f3f7`).
> Номер 062 выбран как следующий свободный после ADR-061. Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2671 не открывается новый общий slice. Три применимых
item уже влиты отдельными PR с явной меткой `#2671`. Ещё 16 item помечены в
самом отчёте как `не нужен`, потому что аналог уже есть в дереве. Оставшиеся требуют
собственного владельца, поверхности и измеримого критерия приёмки. Issue
остаётся OPEN как источник сравнения; комментарий фиксирует только хеш коммита
этого ADR.

## Context

Issue #2671 — self-contained отчёт `orca-features` part 1/2. Исходная гипотеза
о macOS-native Swift coding agent от Peter Steinberger не подтвердилась:
`steipete/Orca` отсутствует. В отчёт вошли два реальных проекта с явной
пометкой источника: `[A]` `echoVic/orca-agent` (Rust, terminal coding agent) и
`[D]` `stablyai/orca` (TypeScript/Electron desktop orchestrator). Инвентаризация
`origin/dev` (`01e2f3f7`) и истории PR показывает три влитых item. Все merge
commit подтверждены как предки `origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) | Slice |
|---|---|---|---|
| E27, версия схемы событий | `SchemaVersion` и `IsJournaled` на `AgentEvent` | [#2775](https://github.com/refusedguy/Harbor-Harness/pull/2775), `cbadef6d` | `07906b5d` |
| D07, учёт использования по сервисам | `ServiceUsage` для billable web tools | [#2857](https://github.com/refusedguy/Harbor-Harness/pull/2857), `cf9acbe5` | `5ddc03a0` |
| D04, метрики prefix-cache reuse | `CacheReuseMetrics` в session stats | [#2893](https://github.com/refusedguy/Harbor-Harness/pull/2893), `0010475f` | `0dd4dca4` |

Комментарии в issue фиксируют те же slice-коммиты. Часть формулировок
пересекается с part 2/2 (#2672), но там они отнесены к соседнему отчёту и
зафиксированы отдельным вердиктом; здесь они не перевыводятся.

Оставшиеся идеи не образуют одного владельца:

- Уже в дереве по самой таблице отчёта: A04 (компилируемые правила и
  `BashArgMatcher`), A05 (`IPermissionService`), A13 (`ToolDispatcher`), A14
  (`RunBudget`), A17 (fail-closed `PreToolUse` gate), A19
  (`AskUserQuestionTool`), B03 (`SessionForkService`), B11 (`session_steer`),
  C02 (`AgentDefinition` с capability), D10 (`models <provider>`), D11
  (`ICompactionService`), E06 (`Harbor.Tui.CellForge.Syntax`), E09
  (`VimComposerMode`), E18 (XAML `ResourceDictionary` как source of truth),
  E24 (`SplitPane`), F01 (`SkillTool`). Помечены `не нужен`, дублировать
  запрещено.
- Требуют нового публичного контракта: A01/A02/A03/A07/A08 (режимы одобрения и
  cycle hotkey), B01/B02 (folder trust), B06/B07/B08 (rewind файлов), B09/B15
  (execution journal и recovery), C01/C03/C04/C07 (agents, async subagents,
  continuation, workflows), D01/D02/D03/D12 (reasoning replay, vision route,
  effort, model route), E01/E05/E08/E22 (image protocol, diff renderer,
  selection, diff comments). Каждый заводит отдельный slice с измеримой целью.
- Требуют CI-поверхности и решения владельца: B18 (retention policy), D08/D09
  (pricing refresh и peak/off-peak), E16 (frame scheduler), E17 (terminal
  capabilities). Каждый меняет конфигурацию или пороги, а не только код.
- Требуют владельца конкретной TUI/CLI-поверхности: A18 (OSC 7501), A20
  (`doctor`), B04/B05/B12/B13/B16/B17/B19/B20/B21, C05/C06/C08/C09/C10/C11/C12/
  C13/C14/C15, D13, E02/E03/E04/E07/E10/E11/E12/E13/E14/E15/E18/E19/E20/E21/E23/
  E25/E26, F02. Детализация есть в issue, но без потребителя и измеримой цели
  эти пункты остаются каталогом, а не готовыми PR.

## Decision

Код не писать. Конкретно:

- Три влитых item не перевыводятся. Повторная правка `AgentEvent`,
  `ServiceUsage` и `CacheReuseMetrics` создала бы второй источник истины там,
  где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. Уже реализованные не дублируются;
  новые открываются отдельной issue и PR, как это уже сделано для трёх влитых
  item.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне
  CI.
- Issue #2671 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты, отправлены к
  владельцу или помечены `не нужен`, он был бы вакуумным, а гейты ослаблять
  запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2671. Part 2/2 (#2672) этим решением не перевыводится.

## References

- Issue [#2671](https://github.com/refusedguy/Harbor-Harness/issues/2671) —
  полный текст отчёта `orca-features` part 1/2.
- Merged PR: [#2775](https://github.com/refusedguy/Harbor-Harness/pull/2775),
  [#2857](https://github.com/refusedguy/Harbor-Harness/pull/2857),
  [#2893](https://github.com/refusedguy/Harbor-Harness/pull/2893).
- Part 2/2 того же отчёта: issue
  [#2672](https://github.com/refusedguy/Harbor-Harness/issues/2672).
- [DECISIONS.md](DECISIONS.md).
