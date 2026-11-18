# ADR-058: отчёт desktop-ux-gaps part 1/2 (#2682) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2682](https://github.com/refusedguy/Harbor-Harness/issues/2682) прочитан
> полностью: concrete item Z01–Z68, G01–G20 и K01–K12, итого 100 пунктов. Дубли
> проверены через `gh pr list --search 2682`, комментарии issue,
> `git log --all --grep=2682`, `git merge-base --is-ancestor` и
> `docs/adr/DECISIONS.md` на базе `origin/dev` (`0068e8e9`). Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2682 не открывается новый общий slice. Семь применимых
item уже влиты отдельными PR, восьмой (Z10) реализован внутри Z01. Оставшиеся
пункты требуют собственного владельца, поверхности и критерия приёмки либо
прямо отнесены отчётом к не-MVP или к multi-user среде. Issue
остаётся OPEN как источник сравнения; комментарий фиксирует только хеш коммита
этого ADR.

## Context

Issue #2682 — self-contained отчёт `desktop-ux-gaps` part 1/2. Он сравнивает
Avalonia-оболочку и CellForge TUI Harbor с Zed, GPUIX и gpui-kit и содержит 100
concrete item. Сам отчёт прямо поправляет посылку: оболочки в Harbor не пустые
(`CommandPaletteViewModel`, `Views/DiffView.axaml`, `Views/CodeEditorView.axaml`,
`ProjectFileTreeScanner`, `CellForgeFileTreePanel`, `SelectionEngine` и другие
уже есть), разрыв в глубине и поведении.

Инвентаризация `origin/dev` и истории PR показала семь влитых item. Все merge
commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) |
|---|---|---|
| Z01, история запросов с префиксной навигацией | `QueryHistory` для палитры, общий для Avalonia и TUI | [#2873](https://github.com/refusedguy/Harbor-Harness/pull/2873), `8de31c8e` |
| Z02, ранг по частоте использования | счётчик запусков и сортировка кандидатов | [#2783](https://github.com/refusedguy/Harbor-Harness/pull/2783), `07ae68e0` |
| Z03, псевдонимы команд | `CommandAliases` в конфиге, подмена до поиска | [#2872](https://github.com/refusedguy/Harbor-Harness/pull/2872), `09a673bc` |
| Z06, биндинг в строке палитры | `KeyText` из `IKeyboardShortcutService` в строке выдачи | [#2948](https://github.com/refusedguy/Harbor-Harness/pull/2948), `ff148bb8` |
| Z09, humanize имени действия | читаемые названия команд в палитре | [#2954](https://github.com/refusedguy/Harbor-Harness/pull/2954), `8db8393f` |
| Z15, expand/collapse all в дереве | команды сворачивания для `FileTreeNode` | [#2959](https://github.com/refusedguy/Harbor-Harness/pull/2959), `7752e267` |
| Z20, варианты копирования пути | абсолютный, относительный путь и permalink | [#2965](https://github.com/refusedguy/Harbor-Harness/pull/2965), `383a7139` |

Комментарии в issue фиксируют те же slice-коммиты. Z10 (сброс курсора истории
при ручном вводе) не оформлен отдельным PR, но влит внутри Z01:
`src/Harbor.Desktop.ViewModels/ViewModels/QueryHistory.cs` содержит
`ResetNavigation()` и явную ссылку на Z10 в контракте.

Оставшиеся пункты не образуют одного владельца:

- Палитра: Z04 (асинхронный fuzzy и предел выдачи) имеет смысл только при росте
  каталога; Z05 требует плагинного `IPaletteInterceptor` и нового видимого
  UX-контракта; Z07 и Z08 — footer и удаление из истории — изолированные правки
  с собственной поверхностью, но без измеримой цели в отчёте.
- Дерево файлов: Z11 может опереться на уже существующее
  `FileTreeNode.GitStatus`, но цветовой легенды и фильтра нет; Z12 требует
  новых `ErrorCount`/`WarningCount` и источника; Z16–Z19, Z32, Z36 задают
  отдельные boundary-контракты drag/drop, внутреннего clipboard и undo-стека;
  Z37 — crash-safety диалог и toast для FS-операций; Z13, Z14, Z21–Z31,
  Z33–Z35 и Z38 — точечные порты с разными владельцами в Avalonia и TUI.
- Редактор и diff: Z39–Z58 опираются на существующие
  `SideBySideDiffViewModel`, `DiagnosticsSquiggleRenderer`, `ILspService` и
  `SelectionEngine`, но модель hunk, stage/revert, git blame, review comments,
  code actions и split diff требуют собственных публичных контрактов.
  Часть смежных K-пунктов закрыта part 2/2 (ADR-055) и этим решением не
  перевыводится.
- Collaboration: Z59–Z68 предполагают IPC/daemon с несколькими участниками.
  Z61–Z65 и Z68 переносятся как паттерны toast, бейджей, ссылок и realtime
  следования без multi-user backend, но у каждого свой потребитель.
- GPUIX (G01–G20) и gpui-kit (K01–K12): automation-протокол, AX-локаторы,
  error overlay, frame stats, виртуальные списки, Sheet, Popover, Palette,
  Table и Form. Отчёт часть из них прямо помечает как среднюю/низкую
  применимость или «не в MVP»; каждый новый control или протокол нужен отдельным
  slice с владельцем и критерием приёмки.

## Decision

Код не писать. Конкретно:

- Семь влитых item не перевыводятся. Повторная правка истории палитры, ранжирования,
  псевдонимов, биндингов, humanize, сворачивания дерева и копирования путей
  создала бы второй источник истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. Точечные порты открываются
  отдельными PR по конкретному item; частично неприменимые не портируются;
  новые контракты требуют владельца и измеримой цели.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне
  CI.
- Issue #2682 не закрывается. Это каталог разрывов, а не трекер одной задачи.
  Part 2/2 того же отчёта (#2683) уже зафиксирован ADR-055 и не перевыводится.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2682.

## References

- Issue [#2682](https://github.com/refusedguy/Harbor-Harness/issues/2682) —
  полный текст отчёта `desktop-ux-gaps` part 1/2.
- Merged PR: [#2873](https://github.com/refusedguy/Harbor-Harness/pull/2873),
  [#2783](https://github.com/refusedguy/Harbor-Harness/pull/2783),
  [#2872](https://github.com/refusedguy/Harbor-Harness/pull/2872),
  [#2948](https://github.com/refusedguy/Harbor-Harness/pull/2948),
  [#2954](https://github.com/refusedguy/Harbor-Harness/pull/2954),
  [#2959](https://github.com/refusedguy/Harbor-Harness/pull/2959),
  [#2965](https://github.com/refusedguy/Harbor-Harness/pull/2965).
- Part 2/2 того же отчёта: issue
  [#2683](https://github.com/refusedguy/Harbor-Harness/issues/2683), вердикт
  [ADR-055](ADR-055-desktop-ux-gaps-part2-report-verdict.md).
- [DECISIONS.md](DECISIONS.md).
