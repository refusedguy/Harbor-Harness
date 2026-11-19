# ADR-085: отчёт kilocode-features part 2/2 (#2680) после вердикта — влиты шесть slice

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2680](https://github.com/refusedguy/Harbor-Harness/issues/2680) прочитан
> полностью, вместе с комментариями. Проверены `gh pr list --search 2680 --state
> all`, `git log` и `git merge-base --is-ancestor` на базе `origin/dev`
> (`6b68000a`). Номер 085 — следующий свободный после 084, закреплённого PR
> #3029 (`d0d05816`). Код, тесты, гейты, пороги, golden и AOT-профиль этим PR не
> меняются.

## Status

**Decided:** вердикты [ADR-049](ADR-049-kilocode-features-part2-report-verdict.md)
и [ADR-077](ADR-077-kilocode-part2-post-verdict.md) сохраняются: одного общего
slice по #2680 нет. Шесть slice, влитых после ADR-077, не перевыводятся. Issue
остаётся OPEN как источник сравнения; комментарий фиксирует только хеш коммита
этого ADR.

## Context

ADR-049 на базе `8db8393f` зафиксировал вердикт по 180 item (F001-F180) и учёл
F157, F159 и F179. ADR-077 на базе `abc83115` добавил F170 и F115. После ADR-077
в `dev` влиты ещё шесть item, которых тот ADR не знал:

| Item | Что требовалось | След в дереве | Slice | Merge commit | PR |
|---|---|---|---|---|---|
| F125, классификация активности | `TerminalActivityClassifier` на базе `SessionStatsEvent` | `src/Harbor.Tui.CellForge.Engine/Rendering/TerminalActivityClassifier.cs` | `17ec6f42` | `6691dd96` | [#3009](https://github.com/refusedguy/Harbor-Harness/pull/3009) |
| F124, заголовок терминала | `TerminalTitleWriter` (OSC 0/2) и индикатор состояния | `src/Harbor.Tui.CellForge.Engine/Rendering/TerminalTitleWriter.cs` | `7382436d` | `5081d232` | [#3016](https://github.com/refusedguy/Harbor-Harness/pull/3016) |
| F132, тема `system` | проба палитры терминала из `TERM`/`COLORTERM` | `src/Harbor.Tui.CellForge.Engine/Capabilities/SystemThemeProbe.cs` | `a3e54bba` | `dee8ae13` | [#3019](https://github.com/refusedguy/Harbor-Harness/pull/3019) |
| F141, conventional commit | разбор и рендеринг сообщения из git diff | `src/Harbor.Tools.Builtin/Tools/Bash/ConventionalCommitMessage.cs` | `c2ce735a` | `1a98ac61` | [#3021](https://github.com/refusedguy/Harbor-Harness/pull/3021) |
| F130, cost alert | разбор и оценка порога стоимости | `src/Harbor.Tools.Builtin/Tools/Bash/CostAlertThreshold.cs` | `d7a3cfe8`, fix `5dcda00e` | `49b2774c` | [#3025](https://github.com/refusedguy/Harbor-Harness/pull/3025) |
| F177, таймаут команд | централизованный разбор таймаута и границ | `src/Harbor.Tools.Builtin/Tools/Bash/CommandTimeoutResolver.cs` | `8c0fab85` | `6b68000a` | [#3024](https://github.com/refusedguy/Harbor-Harness/pull/3024) |

Все шесть merge commit подтверждены как предки базы через
`git merge-base --is-ancestor`, `abc83115` является предком каждого из них.
Комментарии issue фиксируют те же хеши. Два открытых PR по этому отчёту,
[#3027](https://github.com/refusedguy/Harbor-Harness/pull/3027) (F152) и
[#3030](https://github.com/refusedguy/Harbor-Harness/pull/3030) (F108), в таблицу
не входят: они не влиты и в базе отсутствуют. Нового решения владелец issue не
называл: комментарии фиксируют только хеши slice.

## Decision

Код не писать. Конкретно:

- Шесть влитых item не перевыводятся: у каждого свой PR и файл в дереве, оба
  подтверждены как предки базы. Повторная правка создала бы второй источник
  истины там, где отчёт требует одного владельца.
- Остальные item не объединяются в meta-PR. Каждый имеет свой контракт и свой
  критерий приёмки; новый slice открывается отдельной issue и PR, как это уже
  сделано для F157, F159, F179, F170, F115 и шести item выше.
- Открытые PR #3027 и #3030 не «доливаются» заодно: у них собственные slice и
  собственный критерий приёмки.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; гейты ослаблять запрещено.
- Новый guard не вводится: на дереве, где часть item влита, а часть ждёт
  владельца, проверка «все item закрыты» была бы вакуумной.
- Issue #2680 не закрывается: это self-contained отчёт, а не трекер одной
  задачи. Комментарий содержит только хеш коммита этого ADR.

## Consequences

- Меняются два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), их прогоняет CI.
- Issue остаётся OPEN. Пересмотр возможен по новому факту: владелец называет
  конкретный item, потребителя и измеримую цель. Тогда открывается отдельный
  slice, а не переоткрытие всего отчёта.

## References

- Issue [#2680](https://github.com/refusedguy/Harbor-Harness/issues/2680) —
  полный текст отчёта `kilocode-features part 2/2`.
- [ADR-049](ADR-049-kilocode-features-part2-report-verdict.md) — исходный
  вердикт по этому отчёту.
- [ADR-077](ADR-077-kilocode-part2-post-verdict.md) — предыдущая post-verdict
  запись; в ней шесть item выше ещё не были влиты.
- Merged PR: [#3009](https://github.com/refusedguy/Harbor-Harness/pull/3009),
  [#3016](https://github.com/refusedguy/Harbor-Harness/pull/3016),
  [#3019](https://github.com/refusedguy/Harbor-Harness/pull/3019),
  [#3021](https://github.com/refusedguy/Harbor-Harness/pull/3021),
  [#3025](https://github.com/refusedguy/Harbor-Harness/pull/3025) и
  [#3024](https://github.com/refusedguy/Harbor-Harness/pull/3024).
- [ADR-084](ADR-084-dx-docs-gaps-four-ideas-post-verdict.md) — та же форма
  post-verdict записи по влитым slice.
- [DECISIONS.md](DECISIONS.md).
