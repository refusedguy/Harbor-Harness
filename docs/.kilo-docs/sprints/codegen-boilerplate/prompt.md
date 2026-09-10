# Sprint: CodeGen Boilerplate Reduction

## Context
Harbor уже использует 6 source generators (ResourceKeyGenerator, MemoryPack, CommunityToolkit.Mvvm, ZLinq, System.Text.Json, TUnit), но boilerplate in TUI/render pipeline remains heavy. Самый больной участок — UI layer (biggest pain point per user). Цель: добавить новые source generators для устранения повторяющегося кода в renderer backends и CAS-циклах.

## Architecture
- **Phase 1**: EscapeCodeGenerator для AnsiPlain/EscapeCodes — замена ручных `\x1b[...m` на enum-based generation
- **Phase 2**: RendererAdapterGenerator — авто-генерация `IRoundRobinRenderer.Frame()` для каждого backend из шаблона
- **Phase 3**: MoodFrameGenerator — атрибуты `[MoodFrame(Mood.Working)]` → авто-регистрация в `PanelFx.BlendRegion`

## Hot-Spots (from local grep, 37 files analyzed)

| Priority | Target | Files affected | Boilerplate saved |
|----------|--------|----------------|-------------------|
| HIGH | `IP-ProtocolCodec` escape codes | AnsiPlain/*.cs (12 files) | 200+ lines |
| HIGH | `IRoundRobinRenderer.Frame()` | 5 backends | 150+ lines each |
| MED | `PanelFx.BlendRegion` | PanelFx.cs, ApprovalGateView.cs | 50+ lines |
| MED | CAS-циклы (`AppReducer`, `ChatReducer`) | AppState, ChatViewState | 100+ lines |
| LOW | `NickConsoleEx` wrapper | 1 adapter | 30 lines |

## Constraints
1. **Zero-alloc** — все generated code должен быть stack-only (spans, static arrays)
2. **Golden-test coverage** — каждый новый generator требует golden-frame test (Harbor.Tui.RendererTests)
3. **AOT-safe** — не использовать reflection в runtime paths
4. **No breaking changes** — generators should be opt-in via attributes
5. **Integration** — новый generator должен быть в `src/Harbor.CodeGen/` проекте, wired через `<RoslynComponent>` pattern

## Tasks
### Task 1: EscapeCodeGenerator
- Создать `src/Harbor.CodeGen/EscapeCodeGenerator.cs` — `IIncrementalGenerator`
- Атрибут `[TerminalEscape]` на enum-и: `Color8Bit`, `CursorDirection`, `StyleFlag`
- Сгенерировать статические классы `EscapeCodes` с предвычисленными span-ами
- Acceptance: заменить 50+ ручных `\x1b[...m` в `AnsiPlain/EscapeCodes.cs`
- Test: golden-frame test в `Harbor.Tui.RendererTests/AnsiPlain/EscapeCodeTests.cs`

### Task 2: RendererAdapterGenerator
- Атрибут `[TuiRenderer(backend = "cellforge|nickconsoleex|ansi|plain")]`
- Генерировать `Frame()` wrapper с заменой только escape-code calls
- Acceptance: 3 из 5 backends (AnsiPlain, CellForge, NickConsoleEx) покрыты
- Test: golden-frame comparison для всех 3 backends

### Task 3: MoodFrameGenerator
- Атрибут `[MoodFrame(Mood.Idle, Mood.Thinking, Mood.Working)]`
- Генерировать dispatch-таблицу mood → frame index для `PanelFx.BlendRegion`
- Acceptance: убрать ручной switch в `MoodBrain.cs`
- Test: golden test на mood transitions

## Deliverables
- 3 source generator + attribute проекта в `src/Harbor.CodeGen/`
- 3 golden-frame теста в `tests/Harbor.Tui.RendererTests/`
- `docs/CODEGEN_BOILERPLATE.md` с инструкциями по использованию
- HTML-отчёт в `.kilo-docs/sprints/codegen-boilerplate/report.html`

## Hard Rules
1. Один коммит = 1-2 файла. Не собирай гигантский дифф.
2. Все generated code должен проходить `dotnet build` без warnings
3. Никаких breaking changes в public API
4. Каждый generator = отдельный .cs файл в Harbor.CodeGen
