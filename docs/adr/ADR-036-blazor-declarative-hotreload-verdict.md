# ADR-036: Blazor declarative UI и Hot Reload vs Harbor DSL. Сравнение и вердикт

> **Status (2026-11-18):** dated record, not normative. Источник: issue
> [#2695](https://github.com/refusedguy/Harbor-Harness/issues/2695), competitor
> research по декларативному UI и Hot Reload в Blazor. Отчёт прочитал исходники
> aspnetcore, razor и sdk, сборки и тесты Harbor не запускал. Этот PR меняет только
> документацию: ни кода, ни гейтов, ни порогов, ни AOT-настроек.

- Дата: 2026-11-18
- База исследования: `origin/dev` = `09a673bc558c6da2f670c46f87ee0abfdb6e66b4`;
  запись перебазирована на `origin/dev` = `eb3797d35ad64204127f962700ce3b5de4ab151d`
- Затрагивает: только этот ADR и индекс [DECISIONS.md](DECISIONS.md)

## §1. Вопрос

Отчёт называет восемь приёмов генерации разметки и Hot Reload, которые теоретически можно
перенести в Harbor DSL: двухфазный source generator, кэш по входу фазы, вызовы с
sequence numbers, разделение recreate и rerender, dev-only обработчик обновлений,
изолированные обработчики, позиционные отображения с checksum и диагностики с
SourceSpan. Здесь фиксируется, что все применимые пункты уже покрыты DSL-платформой
Harbor (issues
[#2424](https://github.com/refusedguy/Harbor-Harness/issues/2424) и
[#2441](https://github.com/refusedguy/Harbor-Harness/issues/2441),
[#2688](https://github.com/refusedguy/Harbor-Harness/issues/2688)), а прямое
переносимое звено, `MetadataUpdateHandler`, отклонено по причине, которую сам отчёт
и называет. Повторно реализовывать нечего.

## §2. Сравнение по пунктам

| # | Приём из отчёта | Состояние в Harbor DSL | Вердикт |
|---|---|---|---|
| 1 | Двухфазный `IIncrementalGenerator`: объявления → компиляция → полное `BuildRoot` | `MarkupGenerator` (#2424, #2428) добавляет контракт `ICellForgeRoot` в post-initialization. Фаза 1 эмитит partial class и declarations, фаза 2 берёт её провайдер как вход и эмитит `BuildRoot` | Покрыто |
| 2 | Кэшировать каждую фазу по входу, а не весь результат | Конвейер ключуется парой `(TargetPath, text)`. `ResolveTargetPath` расшифровывает base64 `TargetPath` и повторяет `ComputeProjectItems` из `RazorSourceGenerator.RazorProviders.cs`. Записи value-comparable, неизменённый файл переиспользует шаг | Покрыто |
| 3 | Sequence-number вызовы в `BuildRoot` как ключи cell diff | `BuildRootEmitter` эмитит `root.Begin(seq)`, `root.Text(seq, ...)`, `root.End()` с плотными локальными номерами в pre-order. Контракт прямо называет их «роль sequence кадра render tree Blazor». `.id` добавляет `TestId(seq, id)` | Покрыто, с явной отсылкой к Blazor в коде |
| 4 | Разделить «пересоздать экземпляр» и «перерисовать метод» по изменённым параметрам | `BuildRootOverrideCoordinator.Decide` реализует правило `HasRemovedDirectParameters`: удалённый параметр даёт `Recreate`, иначе `Rerender`. Интерпретатор отделяет patch на стабильных id от rebuild со сбросом состояния | Покрыто буквально |
| 5 | Один dev-only `MetadataUpdateHandler` с AppContext-выключателем для AOT | Литеральный `MetadataUpdateHandler` не применяется. Отчёт сам указывает, что агент требует JIT и не работает при trimming (`IL2026`/`IL2070` в `MetadataUpdateHandlerInvoker`). Вместо него Debug-only trampoline `BuildRootOverride` плюс `RootBuilderOverride.Current`, `DslDevFileWatcher` и `harbor dsl reload/watch`. Границу задаёт `HarborWithDslDev` / `HARBOR_WITH_DSL_DEV`, который AOT publish выключает принудительно | Покрыто более жёсткой альтернативой, прямой перенос отклонён |
| 6 | Обработчики падают изолированно, неудачный reload не роняет приложение | Парс-ошибка даёт `ReloadResult.Rejected`, дерево и последний валидный кадр остаются нетронутыми. `DevLogChannel` пишет результат, не трогая `IEventBus` и рендерер. Watcher и интерпретатор не связаны с frame loop | Покрыто |
| 7 | `#line`-подобный маппинг и checksum исходника | `MarkupChecksum` считает SHA-256 и кладёт его в сгенерированный код как `SourceChecksum`, dev-режим сверяет значение и отвергает stale-вывод. Каждый node и operation несёт 1-based `SourceLine`/`SourceColumn`, те же позиции идут в комментариях `.g.cs`. `HZ0001` даёт `Location` в `.harbor-ui` | Покрыто. Breakpoint в исходнике DSL не делается |
| 8 | Диагностики first-class с id и SourceSpan, документ с диагностикой не кэшировать | `HZ0001` для синтаксиса и `HUI002`-`HUI006` для state, binding и panel приходят с 1-based `LinePosition`. Документ с диагностикой не порождает исходники вообще, поэтому пустой `BuildRoot` не эмитится и не попадает в кэш | Покрыто |

Пункты 3, 4, 5 и 7 перекликаются с отчётом не по смыслу, а дословно. В
`BuildRootEmitter.cs`, `BuildRootOverrideCoordinator.cs` и `MarkupChecksum.cs`
лежат те же правила, которые отчёт вывел из `RenderTreeDiffBuilder.cs`,
`HotReloadManager.cs` и `ChecksumUtilities.cs`. Это не совпадение и не вторая
реализация: DSL-платформа влила их раньше отдельными slices по issue-цепочке
`2424-2441`.

## §3. Что неприменимо и почему

`MetadataUpdater.ApplyUpdate`, `DOTNET_STARTUP_HOOKS`, `MetadataUpdateHandlerInvoker`
с топологической сортировкой сборок и вызовами `ClearCache` / `UpdateApplication`
привязаны к JIT и к загружаемым сборкам. Harbor.Core собирается в NativeAOT, где
этот путь недоступен. Отчёт подтверждает то же самое подавлением IL2026/IL2070 в
инвокторе SDK. Отчёт честно замечает про `ComponentHotReload`: такого класса
нет, а реальные типы это `HotReloadManager`, `HotReloadRenderHandler`,
`HotReloadService`, `HotReloadAgent` и `MetadataUpdateHandlerInvoker`.

Жизненный цикл `ComponentBase`, `ParameterView` как view над `RenderTreeFrame`,
`EventCallback` с автоматическим `StateHasChanged`, каскадные параметры и
пересоздание экземпляра в `RenderTreeDiffBuilder` относятся к модели компонента с
DOM-подобным деревом. В Harbor такую роль играют дерево ячеек CellForge и парный
`UiState` / `AppMsg` в TEA-редьюсерах. Инвалидация после события там уже делает
редьюсер, отдельный `StateHasChanged` не нужен.

Enhanced `#line` с PDB-маппингом в исходник `.harbor-ui` требует отладчика, который
понимает DSL как язык исходников. Harbor даёт позиционные комментарии и `Location`
в диагностике, но не ставит breakpoint в разметку. Сервисная инъекция
`[InjectAttribute]`, tag helpers из metadata references и кэш по MVID не имеют
аналога в терминале: там нет ни разметки тегов, ни DI-контейнера в генерируемом
коде.

## §4. Решение

Ни один из восьми пунктов не требует новой реализации в этом PR. DSL-платформа уже
держит двухфазный генератор с кэшем по `TargetPath` и тексту, sequence numbers в
`BuildRoot`, правило `HasRemovedDirectParameters`, dev-подмену с изолированным
reject, checksum и диагностики с позицией. Путь через `MetadataUpdateHandler`
отклонён на основании AOT-ограничения, которое отчёт зафиксировал сам. Граница
dev-кода в Harbor работает на compile-time флаге и жёстче runtime-переключателя
AppContext из отчёта.

Issue #2695 остаётся OPEN как источник сравнения и этим PR не закрывается.
Пересмотр возможен по новому факту, например если появится измеримая выгода от
живой IL-дельты в JIT-only dev-сборке по сравнению с существующим Debug-trampoline.
Такой пересмотр будет отдельным owner-decision и отдельным PR, а не повторным
открытием этого вердикта.

## §5. Границы проверки

Утверждения §2 основаны на исходниках aspnetcore по SHA
`c5baf473f31fa3bbe9f8201e3411361c632f3a91`, razor по SHA
`58ec96978ef4e5823b54e960b9fd64cff45d7e68` и sdk по SHA
`36329803ce8ae9b4866eb6f84bbd5d48fc3e5911`, а также на состоянии `origin/dev` =
`09a673bc`. Проверка `gh pr list --search 2695` показала только неотносящийся
слитый PR #1255, дублирующих не нашлось. Локально `dotnet`, тесты и гейты по
условию смены не запускались, проверка только через CI. Влияние на код нулевое.

- Смежные вердикты той же серии:
  [ADR-028](ADR-028-avalonia-declarative-xaml-dsl-verdict.md),
  [ADR-029](ADR-029-gpui-kit-patterns-already-shipped.md),
  [ADR-031](ADR-031-steal-list-2026-11-no-new-slice.md).
- Индекс решений: [DECISIONS.md](DECISIONS.md).
