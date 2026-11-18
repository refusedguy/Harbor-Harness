# ADR-028: Avalonia AXAML vs Harbor DSL — сравнение и вердикт

> **Status (2026-11-18):** dated record, not normative. Источник — issue
> [#2694](https://github.com/refusedguy/Harbor-Harness/issues/2694), competitor
> research по декларативному XAML в Avalonia; прочитан исходный код Avalonia и
> XamlX, сборки и тесты Harbor не запускались. Этот PR меняет только документацию:
> ни кода, ни гейтов, ни порогов, ни AOT-настроек.

- Дата: 2026-11-18
- База исследования: `origin/dev` = `c33c5216482aae794cd1bf20c8189c4d1cdb2e2d`;
  запись перебазирована на `origin/dev` = `eb33bc33`
- Затрагивает: только этот ADR и индекс [DECISIONS.md](DECISIONS.md)

## §1. Вопрос

Отчёт предлагает восемь приёмов Avalonia, которые теоретически можно перенести
в Harbor: компиляцию разметки в код, source generator вместо IL-postprocessing,
типобезопасные биндинги с обязательной аннотацией типа, типизированную эмиссию
путей без reflection, dev-хук подмены `Populate`, разделение build-time и dev
компиляции, реестр шаблонов по типу данных и протокол обновления с ошибкой в
ответе. Здесь фиксируется, что из этого уже покрыто DSL-платформой Harbor
([#2424](https://github.com/refusedguy/Harbor-Harness/issues/2424) —
[#2441](https://github.com/refusedguy/Harbor-Harness/issues/2441),
[#2688](https://github.com/refusedguy/Harbor-Harness/issues/2688),
[#2727](https://github.com/refusedguy/Harbor-Harness/issues/2727)), а что
принципиально не переносится в терминал. Повторно реализовать ничего не нужно.

## §2. Сравнение по пунктам

| # | Приём в Avalonia | Состояние в Harbor DSL | Вердикт |
|---|---|---|---|
| 1 | AXAML компилируется в IL постобработкой сборки через Cecil после `CoreCompile` | `.harbor-ui` разбирается общим парсером в `SpecArena`, `BuildRootEmitter` выдаёт фазы деклараций и `BuildRoot` как обычный C# (#2424, #2426, #2428) | Покрыто; IL-rewrite не принят |
| 2 | Source generator как альтернатива IL-postprocessing | `MarkupGenerator` — Roslyn `IIncrementalGenerator` с кэшем по `(TargetPath, text)`; второй проход по сборке не нужен | Покрыто; выбран более простой для CI и AOT путь |
| 3 | `x:DataType` плюс `CompiledBinding`: путь проверяется на сборке | Директива `state: Type` и `{Binding Path}`; HUI002/HUI003/HUI004/HUI005 на unknown type, отсутствие аннотации и неверный путь (#2429) | Покрыто |
| 4 | `TypedBindingExpression<TSource,TValue>` вместо поиска свойств по имени | `TypedBindingsEmitter` эмитирует `Func<TState,TValue>` и `Action<TState,TValue>` с прямым доступом к свойству, без reflection и `dynamic` (#2429) | Покрыто; hot path обновления VM без IL2026 |
| 5 | `!XamlIlPopulateOverride` подменяет скомпилированный `Populate` в dev | `DevReloadInterpreter` патчит живое дерево с сохранением element id (#2433, PR #2727 влит); Debug-only trampoline `BuildRoot` и координатор подмены — PR [#2764](https://github.com/refusedguy/Harbor-Harness/pull/2764) (OPEN, in flight) | Покрыто/в работе; дублировать не нужно |
| 6 | Runtime-компилятор помечен `[RequiresUnreferencedCode]` и `[RequiresDynamicCode]` | Dev-код живёт отдельной сборкой под `HarborWithDslDev` / `HARBOR_WITH_DSL_DEV`; AOT-сборка не содержит интерпретатор и Roslyn | Покрыто; граница даже жёстче, чем у Avalonia |
| 7 | `IDataTemplate` / `ITypedDataTemplate`: упорядоченный `Match` по типу, локальные и глобальные правила | `IPanelDescriptor` с `StateType`, `PanelDescriptorRegistry` (local → global → fallback), диагностика HUI006 (#2430, PR #2535 влит) | Покрыто |
| 8 | `UpdateXamlMessage` и `UpdateXamlResultMessage` несут ошибку с деталями в ответе | `ReloadResult.Rejected` с `Reason`, куда включены `line:column` и текст диагностики; `dsl check` и `dsl reload` печатают их и возвращают код 1 (#2431, #2432, #2433) | Покрыто по смыслу; формат сообщений и транспорт не переносятся |

## §3. Что неприменимо и почему

Previewer в отдельном окне, `RemoteDesignerEntryPoint`, `DesignWindowLoader`,
HTML/WebSocket-транспорт, `Design.Width/Height/DataContext`, стили, селекторы,
темы, `ResourceDictionary`, SRE-compilation (`AssemblyBuilder.RunAndCollect`) и
рантайм-движок `CompiledBinding` на `INotifyPropertyChanged` / `WeakEvent`
привязаны к GUI-окну, DOM и JIT. У Harbor нет окна, platformImpl и GPU-композитинга,
а `Harbor.Core` ориентирован на NativeAOT. Это owner-decision: переносить их в
терминальный DSL бессмысленно. Ближайший аналог окна в Harbor — дерево ViewModel
и дескрипторы панелей, а не control tree.

## §4. Решение

Ни один из восьми пунктов не требует новой реализации в этом PR. DSL-платформа
уже держит компиляцию разметки в C#, обязательную аннотацию типа состояния с
проверкой путей на сборке, dev-подмену `BuildRoot` и реестр панелей по типу.
Три приёма Avalonia, которые не переносятся (оконный previewer, стили, SRE),
остаются за пределами продукта по решению владельца. Пересмотреть вердикт стоит
только при появлении нового требования, например реальной интероперабельности с
Avalonia-приложением; это будет отдельный owner-decision и отдельный PR.

## §5. Границы проверки

Утверждения §2 основаны на исходниках Avalonia по SHA
`281e59e13854766c4a8152c944ce0ab8063b7525` и XamlX по SHA
`f28a1415b91e1161e78f3caeee4705ba38e4f730`, а также на состоянии
`origin/dev` = `c33c5216` и PR [#2764](https://github.com/refusedguy/Harbor-Harness/pull/2764).
Локально `dotnet`, тесты и гейты по условию смены не запускались; проверка — только
CI. Влияние на код: нулевое.
