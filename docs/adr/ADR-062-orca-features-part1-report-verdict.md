# ADR-062: отчёт orca-features part 1/2 (#2671) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2671](https://github.com/refusedguy/Harbor-Harness/issues/2671) прочитан
> полностью: concrete item A01–A20, B01–B22, C01–C15, D01–D13, E01–E27 и
> F01–F02; тело обрывается на F02. Дубли проверены через `gh pr list --search
> 2671`, комментарии issue, `git log --all --grep=2671` и
> `docs/adr/DECISIONS.md` на базе `origin/dev` (`01e2f3f7`). Номер 062 —
> следующий свободный после ADR-061 (`testing-qa` part 1/2). Код, тесты,
> гейты, пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2671 не открывается новый общий slice. Три item уже
влиты отдельными PR. Остальные либо имеют готовый аналог в Harbor, либо
требуют собственного владельца и публичного контракта, либо привязаны к
графической/десктопной поверхности, которой у терминального агента нет. Issue
остаётся OPEN как источник сравнения; комментарий фиксирует только хеш коммита
этого ADR.

## Context

Issue #2671 — self-contained отчёт `orca-features` part 1/2. Он объединяет два
разных проекта: `echoVic/orca-agent` (Rust, terminal agent) и `stablyai/orca`
(TypeScript/Electron, desktop orchestrator); источник помечен префиксом `[A]`
или `[D]` у каждого item. Инвентаризация `origin/dev` (`01e2f3f7`) и истории
PR показала три влитых item. Все merge commit подтверждены как предки
`origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) | Slice |
|---|---|---|---|
| E27, structured events schema | `SchemaVersion` и `IsJournaled` на `AgentEvent` | [#2775](https://github.com/refusedguy/Harbor-Harness/pull/2775), `cbadef6d` | `07906b5d` |
| D07, service usage | `ServiceUsage` для billable web tools | [#2857](https://github.com/refusedguy/Harbor-Harness/pull/2857), `cf9acbe5` | `5ddc03a0` |
| D04, prefix-cache reuse | `CacheReuseMetrics` в session stats | [#2893](https://github.com/refusedguy/Harbor-Harness/pull/2893), `0010475f` | `0dd4dca4` |

Комментарии в issue фиксируют slice-коммиты `5ddc03a0` (D07) и `0dd4dca4`
(D04); для E27 там указан merge `cbadef6d`, а slice `07906b5d` восстановлен
из истории PR.

Оставшиеся item не образуют одного владельца:

- Готовый аналог в Harbor, новый slice не нужен: A04 (`PermissionRuleset` +
  `BashArgMatcher`), A05 (`IPermissionService`), A12 (`AgentDefinition` +
  `ToolsCatalog`), A13 (`ToolDispatcher`), A14 (`RunBudget`), A19
  (`AskUserQuestionTool`), B03 (`SessionForkService`), B11 (`session_steer`),
  C02 (builtin roles), D10 (`models <provider>`), D11 (`ICompactionService`),
  E06 (`Harbor.Tui.CellForge.Syntax`), E09 (`VimComposerMode`), E18
  (XAML `ResourceDictionary` как источник истины), E24 (`SplitPane`), F01
  (`SkillTool`). Второй источник истины здесь создал бы рассогласование.
- Привязаны к Surface, которой нет в терминале: E01 (Kitty/Ghostty/iTerm2
  протоколы инлайн-изображений), E02 (буфер обмена), E25 (drag-and-drop), E26
  (PDF/pan-zoom preview), B22 (image fallback в true-color cells), C12–C15
  (coordinator/worker и fleet worktree из Electron-клиента). Их терминальные
  аналоги, где они есть (`/attach`, `@path`), уже описаны в отчёте как
  достаточные.
- Требуют своего владельца и измеримой цели: A01–A03 (`ApprovalMode` и cycle
  в hotkey), A16 (четыре новых hook event), B01 (folder trust store),
  B06–B08 (pre-image rewind, dry-run и journal), C07–C09 (workflow script,
  preview, verifier), D02 (`IVisionRouter`), D12 (`IModelRouter`), E05
  (`IDiffRenderer`), E10 (in-view transcript search), E13 (`IShortcutCatalog`),
  E14 (plan panel), E19 (session picker), F02 (trust-linked project config).
  У каждого свой публичный контракт и свой критерий приёмки.

Отчёт part 1/2 соседствует с part 2/2 (#2672), у которого тоже влиты отдельные
slice. Этот ADR не инвентаризует #2672 и не покрывает item из соседнего issue.

## Decision

Код не писать. Конкретно:

- Три влитых item не перевыводятся. Повторная правка `AgentEvent`,
  `ServiceUsage` и `CacheReuseMetrics` создала бы второй источник истины там,
  где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный
  контракт и критерий приёмки; часть имеет готовый аналог, часть не
  применима к терминальному агенту. Новый slice открывается отдельным PR,
  как это уже сделано для E27, D07 и D04.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть; документационные гейты `docs.yml` остаются на
  стороне CI.
- Issue #2671 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2671.

## References

- Issue [#2671](https://github.com/refusedguy/Harbor-Harness/issues/2671) —
  полный текст отчёта `orca-features` part 1/2.
- Merged PR: [#2775](https://github.com/refusedguy/Harbor-Harness/pull/2775),
  [#2857](https://github.com/refusedguy/Harbor-Harness/pull/2857),
  [#2893](https://github.com/refusedguy/Harbor-Harness/pull/2893).
- Part 2/2 того же отчёта: issue
  [#2672](https://github.com/refusedguy/Harbor-Harness/issues/2672) — этим
  решением не перевыводится.
- [DECISIONS.md](DECISIONS.md).
