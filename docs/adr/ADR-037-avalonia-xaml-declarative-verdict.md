# ADR-037: отчёт по Avalonia XAML (#2694) — финальный вердикт, нового slice нет

> **Status (2026-11-18):** dated record, not normative. Источник — issue
> [#2694](https://github.com/refusedguy/Harbor-Harness/issues/2694),
> self-contained competitor research по декларативному XAML в Avalonia. Этот PR
> меняет только два markdown-файла: код, тесты, гейты и пороги не трогаются.
> Факты проверены через `gh pr list --search 2694`, `git log` и
> инвентаризацию `origin/dev` (`872b6157`); локальные `dotnet`, сборка и
> тесты по условию смены не запускались. Запись перебазирована на
> `origin/dev` = `9525ab87`; ADR-034, ADR-035 и ADR-036 заняты
> параллельными PR #2887, #2890 и #2888.
> Документ не несёт `Status: normative`, поэтому его отсылки к файлам не
> являются machine-checkable утверждениями.

## Status

**Decided:** [ADR-028](ADR-028-avalonia-declarative-xaml-dsl-verdict.md)
остаётся в силе, новый slice по #2694 не открывается. issue остаётся OPEN как
источник сравнения и этим PR не закрывается.

## Context

ADR-028 зафиксировал сравнение восьми приёмов Avalonia с DSL-платформой Harbor.
В нём пункт 5 был помечен «Покрыто/в работе»: PR
[#2764](https://github.com/refusedguy/Harbor-Harness/pull/2764) с Debug-only
trampoline и координатором подмены был OPEN. После этого владелец
переоткрыл issue, пояснив, что оно остаётся self-contained отчётом, а не
трекером одного slices.

Проверка на `origin/dev` = `872b6157` меняет только эту одну деталь. #2764
закрыт с причиной «Superseded by #2789 — CI did not register checks after the
force-push on this branch». PR
[#2789](https://github.com/refusedguy/Harbor-Harness/pull/2789) влит
(merge commit `61cebdf6`, commit `3b6cbac3`): `BuildRootOverrideCoordinator`
и `#if DEBUG`-ветка в `BuildRootEmitter` живут в `src/`, а тесты — в
`tests/Harbor.Tui.CellForge.Tests`. Части «in flight» больше нет.

| # | Приём Avalonia | Статус на `872b6157` | След в Harbor |
|---|---|---|---|
| 1 | AXAML → IL постобработкой через Cecil | merged | `.harbor-ui` → `SpecArena` → `BuildRootEmitter` (#2424, #2426, #2428) |
| 2 | Source generator вместо IL-rewrite | merged | `MarkupGenerator` как `IIncrementalGenerator` (#2424) |
| 3 | `x:DataType` + compiled bindings | merged | `state: Type`, `{Binding Path}`, HUI002–HUI005 (#2429) |
| 4 | Typed path emission без reflection | merged | `TypedBindingsEmitter`: `Func<TState,TValue>` / `Action<TState,TValue>` (#2429) |
| 5 | `!XamlIlPopulateOverride` для dev reload | merged | `DevReloadInterpreter` (#2727); trampoline и координатор (#2789, после закрытого #2764) |
| 6 | Разделение build-time и runtime dev compilation | merged | `HarborWithDslDev` / `HARBOR_WITH_DSL_DEV`, dev-сборка отделена от AOT |
| 7 | `IDataTemplate`-цепочка по типу | merged | `IPanelDescriptor`, `PanelDescriptorChain`, HUI006 (#2430, PR #2535) |
| 8 | Ошибка в ответе на обновление разметки | merged | `ReloadResult.Rejected` с `Reason`, `dsl check` / `dsl reload` (#2431–#2433) |

Оконный previewer, стили/селекторы/темы, SRE-компиляция
(`AssemblyBuilder.RunAndCollect`) и runtime-движок `CompiledBinding`
привязаны к GUI, DOM и JIT и остаются owner-decision из ADR-028. Проверка
исходников Avalonia по SHA `281e59e13854766c4a8152c944ce0ab8063b7525` и
XamlX по SHA `f28a1415b91e1161e78f3caeee4705ba38e4f730` подтверждает ту же
картину, что и ADR-028: новые факты из upstream не появились.

## Decision

Код не писать. Конкретно:

- Восемь пунктов отчёта не реализовываются второй раз. Повторная реализация
  создала бы второй источник истины там, где отчёт требует одного владельца.
- Отдельный ADR вместо ADR-028 не заводится: этот документ лишь закрывает его
  единственную открытую позицию и не меняет вердикта.
- Исключения из ADR-028 не пересматриваются без нового требования или
  owner-решения.
- issue #2694 не закрывается: владелец переоткрыл его как self-contained
  отчёт, а не трекер одной задачи. Это решение сохраняется.
- Гейты, пороги, self-test и AOT-настройки не меняются.

## Consequences

- Меняются только этот ADR и [DECISIONS.md](DECISIONS.md). Единственный риск —
  документационные гейты `docs.yml`: `check-md-links` и `md-lint`.
- Новый guard не вводится: на дереве, где все восемь пунктов влиты или
  отклонены по решению владельца, он был бы красным или вакуумным.
- Пересмотр — по новому факту: реальная интероперабельность с
  Avalonia-приложением, снятие owner-decision по окнам/стилям или новый срез
  DSL. В каждом таком случае открывается отдельный PR и отдельная запись, а
  не переписывается этот отчёт.

## References

- Issue [#2694](https://github.com/refusedguy/Harbor-Harness/issues/2694) —
  полный текст отчёта по Avalonia XAML.
- Merged PR: #2535 (панели по типу), #2727 (dev-интерпретатор),
  #2786 (ADR-028), #2789 (override hook); закрытый PR #2764 заменён #2789.
- [ADR-028](ADR-028-avalonia-declarative-xaml-dsl-verdict.md) — сравнение и
  вердикт по восьми пунктам.
- [ADR-033](ADR-033-declarative-ui-crosslang-report-verdict.md) — смежный
  вердикт по кросс-языковому declarative UI.
- [DECISIONS.md](DECISIONS.md).
